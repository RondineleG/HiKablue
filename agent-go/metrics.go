// Package agent contains the node-agent side of the Go <-> Rust telemetry
// link: FetchMetrics reads the engine's GetMetrics frame over a Unix socket.
//
// Frame format (shared with vpn-engine): a big-endian uint32 length prefix
// followed by a UTF-8 JSON object. The read is assembled with io.ReadFull —
// a stream socket may deliver the frame in several reads.
package agent

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"time"
)

// Metrics mirrors the JSON the engine answers to GetMetrics.
type Metrics struct {
	RxBytes     uint64 `json:"rx_bytes"`
	RxPkts      uint64 `json:"rx_pkts"`
	TxBytes     uint64 `json:"tx_bytes"`
	TxPkts      uint64 `json:"tx_pkts"`
	DroppedPkts uint64 `json:"dropped_pkts"`
	Peers       uint64 `json:"peers"`
	UptimeSecs  uint64 `json:"uptime_secs"`
}

// FetchMetrics connects to the engine's Unix socket, requests GetMetrics and
// decodes the response. The context deadline, when present, is applied to the
// socket: a peer that accepts the connection and then stays silent is cut by
// the deadline instead of blocking the collection loop forever.
func FetchMetrics(ctx context.Context, socketPath string) (Metrics, error) {
	var m Metrics

	var d net.Dialer
	conn, err := d.DialContext(ctx, "unix", socketPath)
	if err != nil {
		return m, fmt.Errorf("dial %s: %w", socketPath, err)
	}
	defer conn.Close()

	if deadline, ok := ctx.Deadline(); ok {
		if err := conn.SetDeadline(deadline); err != nil {
			return m, fmt.Errorf("set deadline: %w", err)
		}
	}

	// Cancelling ctx mid-read does not unblock a blocking socket call by
	// itself; this watcher forces the deadline to expire immediately.
	done := make(chan struct{})
	defer close(done)
	go func() {
		select {
		case <-ctx.Done():
			_ = conn.SetDeadline(time.Now())
		case <-done:
		}
	}()

	reqID := fmt.Sprintf("vpn-agent-%d", time.Now().UnixNano())
	req, err := json.Marshal(map[string]any{
		"request_id": reqID,
		"version":    1,
		"op":         "GetMetrics",
	})
	if err != nil {
		return m, err
	}
	if err := binary.Write(conn, binary.BigEndian, uint32(len(req))); err != nil {
		return m, fmt.Errorf("write frame length: %w", err)
	}
	if _, err := conn.Write(req); err != nil {
		return m, fmt.Errorf("write frame: %w", err)
	}

	var n uint32
	if err := binary.Read(conn, binary.BigEndian, &n); err != nil {
		return m, fmt.Errorf("read frame length: %w", err)
	}
	if n > 1<<20 {
		return m, fmt.Errorf("metrics frame of %d bytes exceeds 1 MiB", n)
	}
	frame := make([]byte, n)
	if _, err := io.ReadFull(conn, frame); err != nil {
		return m, fmt.Errorf("read frame: %w", err)
	}

	var env struct {
		RequestID string           `json:"request_id"`
		Status    string           `json:"status"`
		Result    json.RawMessage  `json:"result"`
		Error     *json.RawMessage `json:"error"`
	}
	if err := json.Unmarshal(frame, &env); err != nil {
		return m, fmt.Errorf("decode response envelope: %w", err)
	}
	if env.Status != "ok" {
		return m, fmt.Errorf("engine returned status %q: %s", env.Status, string(frame))
	}
	if err := json.Unmarshal(env.Result, &m); err != nil {
		return m, fmt.Errorf("decode metrics: %w", err)
	}
	return m, nil
}

// PollMetrics is a convenience for the agent loop: fetch once per interval
// until ctx is cancelled.
func PollMetrics(ctx context.Context, socketPath string, interval time.Duration, visit func(Metrics, error)) {
	for {
		m, err := FetchMetrics(ctx, socketPath)
		visit(m, err)
		select {
		case <-ctx.Done():
			return
		case <-time.After(interval):
		}
	}
}
