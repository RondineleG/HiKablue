package main

import (
	"bytes"
	"encoding/binary"
	"encoding/json"
	"io"
	"net"
	"path/filepath"
	"strings"
	"testing"

	agent "github.com/RondineleG/HiKablue/agent-go"
)

// engineOnce accepts a single connection and answers one GetMetrics frame
// with the supplied response body.
func engineOnce(t *testing.T, respond func(req []byte) []byte) string {
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
				_ = binary.Write(c, binary.BigEndian, uint32(len(resp)))
				_, _ = c.Write(resp)
			}(c)
		}
	}()
	return path
}

func TestRunPrintsMetricsJSON(t *testing.T) {
	path := engineOnce(t, func(req []byte) []byte {
		var r struct {
			RequestID string `json:"request_id"`
		}
		_ = json.Unmarshal(req, &r)
		resp, _ := json.Marshal(map[string]any{
			"request_id": r.RequestID, "version": 1, "status": "ok",
			"result": agent.Metrics{RxBytes: 42, Peers: 1},
		})
		return resp
	})

	var out, stderr bytes.Buffer
	code := run([]string{"-metrics-sock", path}, &out, &stderr)
	if code != 0 {
		t.Fatalf("exit %d: %s", code, stderr.String())
	}
	var m agent.Metrics
	if err := json.Unmarshal(bytes.TrimSpace(out.Bytes()), &m); err != nil {
		t.Fatalf("output is not metrics JSON: %q", out.String())
	}
	if m.RxBytes != 42 {
		t.Fatalf("got %+v", m)
	}
}

func TestRunReportsFetchFailure(t *testing.T) {
	var out, stderr bytes.Buffer
	code := run([]string{"-metrics-sock", filepath.Join(t.TempDir(), "missing.sock")}, &out, &stderr)
	if code != 1 || !strings.Contains(stderr.String(), "vpn-agent:") {
		t.Fatalf("exit %d stderr %q", code, stderr.String())
	}
}

func TestRunRejectsBadFlags(t *testing.T) {
	var out, stderr bytes.Buffer
	if code := run([]string{"-nope"}, &out, &stderr); code != 2 {
		t.Fatalf("exit %d", code)
	}
}
