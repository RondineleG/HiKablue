package agent

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"io"
	"net"
	"path/filepath"
	"testing"
	"time"
)

// fakeEngine answers GetMetrics frames with the supplied response, or — when
// silent is true — accepts the connection and never writes. The silent case
// is the milestone exercise: without the context deadline the read would
// hang forever and stall every later collection.
var readCh = make(chan struct{}, 8)

func fakeEngine(t *testing.T, silent bool) (string, func()) {
	t.Helper()
	dir := t.TempDir()
	path := filepath.Join(dir, "engine.sock")
	l, err := net.Listen("unix", path)
	if err != nil {
		t.Fatal(err)
	}
	go func() {
		for {
			c, err := l.Accept()
			if err != nil {
				return
			}
			if silent {
				go func(c net.Conn) {
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
					select {} // read the frame, then stay silent forever
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
	return path, func() { _ = l.Close() }
}

func TestFetchMetricsDecodesResponse(t *testing.T) {
	path, stop := fakeEngine(t, false)
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
	path, stop := fakeEngine(t, true)
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
	path, stop := fakeEngine(t, true)
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
	if _, err := FetchMetrics(context.Background(), path); err == nil {
		t.Fatal("expected mismatched request_id to be rejected")
	}
}
