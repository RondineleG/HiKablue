package agent

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"io"
	"net"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// fakeEngine answers GetMetrics frames with the supplied response, or — when
// silent is true — accepts the connection and never writes. The silent case
// is the milestone exercise: without the context deadline the read would
// hang forever and stall every later collection.
func fakeEngine(t *testing.T, silent bool) (string, func(), <-chan struct{}) {
	t.Helper()
	dir := t.TempDir()
	path := filepath.Join(dir, "engine.sock")
	l, err := net.Listen("unix", path)
	if err != nil {
		t.Fatal(err)
	}
	readCh := make(chan struct{}, 8)
	stopCh := make(chan struct{})
	go func() {
		for {
			c, err := l.Accept()
			if err != nil {
				return
			}
			if silent {
				go func(c net.Conn) {
					defer c.Close()
					var n uint32
					if err := binary.Read(c, binary.BigEndian, &n); err != nil {
						return
					}
					buf := make([]byte, n)
					if _, err := io.ReadFull(c, buf); err != nil {
						return
					}
					select {
					case readCh <- struct{}{}:
					default:
					}
					// Read the frame, signal it, then hold the conn until the
					// client hangs up or the test ends — never a parked goroutine.
					select {
					case <-stopCh:
					default:
						_, _ = io.Copy(io.Discard, c)
					}
				}(c)
				continue
			}
			go func(c net.Conn) {
				defer c.Close()
				var n uint32
				if err := binary.Read(c, binary.BigEndian, &n); err != nil {
					return
				}
				buf := make([]byte, n)
				if _, err := io.ReadFull(c, buf); err != nil {
					return
				}
				// Echo the request's request_id: the envelope protocol
				// correlates responses to requests.
				var req struct {
					RequestID string `json:"request_id"`
				}
				_ = json.Unmarshal(buf, &req)
				resp, _ := json.Marshal(map[string]any{
					"request_id": req.RequestID, "version": 1, "status": "ok",
					"result": Metrics{RxBytes: 5, RxPkts: 0, Peers: 1},
				})
				_ = binary.Write(c, binary.BigEndian, uint32(len(resp)))
				_, _ = c.Write(resp)
			}(c)
		}
	}()
	return path, func() { close(stopCh); l.Close() }, readCh
}

func TestFetchMetricsDecodesResponse(t *testing.T) {
	path, stop, _ := fakeEngine(t, false)
	defer stop()
	m, err := FetchMetrics(context.Background(), path)
	if err != nil {
		t.Fatal(err)
	}
	if m.RxBytes != 5 || m.Peers != 1 {
		t.Fatalf("unexpected metrics: %+v", m)
	}
}

// A plain cancel (no deadline in the context) must also interrupt a read
// already in flight — the watcher forces the socket deadline to expire.
func TestCancelWithoutDeadlineStopsSilentEngine(t *testing.T) {
	path, stop, readCh := fakeEngine(t, true)
	defer stop()

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() {
		_, err := FetchMetrics(ctx, path)
		done <- err
	}()
	// Wait until the engine read our request frame: the read is provably in
	// flight when we cancel, so the error is the interruption, not a dial.
	select {
	case <-readCh:
	case <-time.After(2 * time.Second):
		t.Fatal("engine never received the request frame")
	}
	cancel()
	select {
	case err := <-done:
		if err == nil {
			t.Fatal("expected error after cancel")
		}
	case <-time.After(2 * time.Second):
		t.Fatal("cancel did not interrupt the read")
	}
}

func TestDeadlineStopsSilentEngineAndNextPollRuns(t *testing.T) {
	path, stop, _ := fakeEngine(t, true)
	defer stop()

	ctx, cancel := context.WithTimeout(context.Background(), 150*time.Millisecond)
	defer cancel()
	start := time.Now()
	if _, err := FetchMetrics(ctx, path); err == nil {
		t.Fatal("expected deadline error from silent engine")
	}
	if elapsed := time.Since(start); elapsed > 2*time.Second {
		t.Fatalf("deadline did not stop the read in time: %v", elapsed)
	}

	// The next collection still runs: same client code, healthy engine.
	l, err := net.Listen("unix", path+".live")
	if err != nil {
		t.Fatal(err)
	}
	defer l.Close()
	go func() {
		c, _ := l.Accept()
		var n uint32
		_ = binary.Read(c, binary.BigEndian, &n)
		buf := make([]byte, n)
		_, _ = io.ReadFull(c, buf)
		var req struct {
			RequestID string `json:"request_id"`
		}
		_ = json.Unmarshal(buf, &req)
		resp, _ := json.Marshal(map[string]any{
			"request_id": req.RequestID, "version": 1, "status": "ok",
			"result": Metrics{RxBytes: 10},
		})
		_ = binary.Write(c, binary.BigEndian, uint32(len(resp)))
		_, _ = c.Write(resp)
		c.Close()
	}()

	m, err := FetchMetrics(context.Background(), path+".live")
	if err != nil {
		t.Fatal(err)
	}
	if m.RxBytes != 10 {
		t.Fatalf("second collection got %+v", m)
	}
}

func TestMismatchedRequestIDIsRejected(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "engine.sock")
	l, err := net.Listen("unix", path)
	if err != nil {
		t.Fatal(err)
	}
	defer l.Close()
	go func() {
		c, _ := l.Accept()
		var n uint32
		_ = binary.Read(c, binary.BigEndian, &n)
		buf := make([]byte, n)
		_, _ = io.ReadFull(c, buf)
		resp, _ := json.Marshal(map[string]any{
			"request_id": "not-mine", "version": 1, "status": "ok",
			"result": Metrics{RxBytes: 1},
		})
		_ = binary.Write(c, binary.BigEndian, uint32(len(resp)))
		_, _ = c.Write(resp)
		c.Close()
	}()
	_, err = FetchMetrics(context.Background(), path)
	if err == nil || !strings.Contains(err.Error(), "does not match") {
		t.Fatalf("expected request_id mismatch error, got %v", err)
	}
}

// ---- coverage tests: FetchMetrics error paths and PollMetrics ----

// scriptedEngine answers each accepted connection by calling respond with
// the request frame; a nil respond means "close without writing".
func scriptedEngine(t *testing.T, respond func(req []byte) []byte) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "engine.sock")
	l, err := net.Listen("unix", path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { l.Close() })
	go func() {
		for {
			c, err := l.Accept()
			if err != nil {
				return
			}
			go func(c net.Conn) {
				defer c.Close()
				var n uint32
				if binary.Read(c, binary.BigEndian, &n) != nil {
					return
				}
				buf := make([]byte, n)
				if _, err := io.ReadFull(c, buf); err != nil {
					return
				}
				resp := respond(buf)
				if resp == nil {
					return
				}
				_ = binary.Write(c, binary.BigEndian, uint32(len(resp)))
				_, _ = c.Write(resp)
			}(c)
		}
	}()
	return path
}

func TestDialMissingSocketFails(t *testing.T) {
	_, err := FetchMetrics(context.Background(), filepath.Join(t.TempDir(), "none.sock"))
	if err == nil || !strings.Contains(err.Error(), "dial") {
		t.Fatalf("expected dial error, got %v", err)
	}
}

func TestOversizedFrameIsRejected(t *testing.T) {
	// The server lies about the frame size: the client must reject it
	// before reading any payload bytes.
	big := filepath.Join(t.TempDir(), "big.sock")
	l, err := net.Listen("unix", big)
	if err != nil {
		t.Fatal(err)
	}
	defer l.Close()
	go func() {
		c, err := l.Accept()
		if err != nil {
			return
		}
		defer c.Close()
		// Read the request first: if we closed before the client finished
		// writing, its error would be about the connection, not the size.
		var n uint32
		if binary.Read(c, binary.BigEndian, &n) != nil {
			return
		}
		if _, err := io.ReadFull(c, make([]byte, n)); err != nil {
			return
		}
		_ = binary.Write(c, binary.BigEndian, uint32(2<<20))
	}()
	_, err = FetchMetrics(context.Background(), big)
	if err == nil || !strings.Contains(err.Error(), "exceeds") {
		t.Fatalf("expected oversize rejection, got %v", err)
	}
}

func TestGarbageEnvelopeIsRejected(t *testing.T) {
	path := scriptedEngine(t, func(req []byte) []byte {
		return []byte("not-json!!!")
	})
	_, err := FetchMetrics(context.Background(), path)
	if err == nil || !strings.Contains(err.Error(), "envelope") {
		t.Fatalf("expected envelope decode error, got %v", err)
	}
}

func TestErrorStatusIsRejected(t *testing.T) {
	path := scriptedEngine(t, func(req []byte) []byte {
		var r struct {
			RequestID string `json:"request_id"`
		}
		_ = json.Unmarshal(req, &r)
		resp, _ := json.Marshal(map[string]any{
			"request_id": r.RequestID, "version": 1, "status": "error",
			"error": map[string]any{"code": "unknown_op"},
		})
		return resp
	})
	_, err := FetchMetrics(context.Background(), path)
	if err == nil || !strings.Contains(err.Error(), `status "error"`) {
		t.Fatalf("expected status error, got %v", err)
	}
}

func TestUndecodableResultIsRejected(t *testing.T) {
	path := scriptedEngine(t, func(req []byte) []byte {
		var r struct {
			RequestID string `json:"request_id"`
		}
		_ = json.Unmarshal(req, &r)
		resp, _ := json.Marshal(map[string]any{
			"request_id": r.RequestID, "version": 1, "status": "ok",
			"result": "not-a-metrics-object",
		})
		return resp
	})
	_, err := FetchMetrics(context.Background(), path)
	if err == nil || !strings.Contains(err.Error(), "decode metrics") {
		t.Fatalf("expected result decode error, got %v", err)
	}
}

func TestPollMetricsVisitsUntilCancelled(t *testing.T) {
	path, stop, _ := fakeEngine(t, false)
	defer stop()

	ctx, cancel := context.WithCancel(context.Background())
	visits := 0
	done := make(chan struct{})
	go func() {
		defer close(done)
		PollMetrics(ctx, path, 10*time.Millisecond, func(m Metrics, err error) {
			visits++
			if visits == 3 {
				cancel()
			}
		})
	}()
	select {
	case <-done:
	case <-time.After(3 * time.Second):
		t.Fatal("PollMetrics never returned after cancel")
	}
	if visits < 3 {
		t.Fatalf("only %d visits", visits)
	}
}
