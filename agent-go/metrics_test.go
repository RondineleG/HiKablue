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
				continue // hold the connection open, say nothing
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
				resp, _ := json.Marshal(map[string]any{
					"request_id": "fake-1", "version": 1, "status": "ok",
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
		resp, _ := json.Marshal(map[string]any{
			"request_id": "fake-2", "version": 1, "status": "ok",
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
