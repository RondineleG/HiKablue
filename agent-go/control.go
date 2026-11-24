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

// ControlClient speaks the engine's length-prefixed JSON control protocol.
// It intentionally has no persistent connection: every control operation is
// a small request/response and a fresh Unix connection keeps cancellation
// and ownership simple.
type ControlClient struct {
	SocketPath string
	Timeout    time.Duration
}

type controlEnvelope struct {
	RequestID string          `json:"request_id"`
	Version   int             `json:"version"`
	Op        string          `json:"op"`
	Payload   json.RawMessage `json:"payload"`
}

type controlResponse struct {
	RequestID string          `json:"request_id"`
	Status    string          `json:"status"`
	Result    json.RawMessage `json:"result"`
	Error     *struct {
		Code    string `json:"code"`
		Message string `json:"message"`
	} `json:"error"`
}

// Call sends one op and decodes its result into out when out is non-nil.
func (c ControlClient) Call(ctx context.Context, op string, payload any, out any) error {
	if c.SocketPath == "" {
		return fmt.Errorf("control socket path is empty")
	}
	timeout := c.Timeout
	if timeout <= 0 {
		timeout = 2 * time.Second
	}
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()

	var d net.Dialer
	conn, err := d.DialContext(ctx, "unix", c.SocketPath)
	if err != nil {
		return fmt.Errorf("dial control socket %s: %w", c.SocketPath, err)
	}
	defer conn.Close()
	if deadline, ok := ctx.Deadline(); ok {
		_ = conn.SetDeadline(deadline)
	}
	done := make(chan struct{})
	defer close(done)
	go func() {
		select {
		case <-ctx.Done():
			_ = conn.SetDeadline(time.Now())
		case <-done:
		}
	}()

	body, err := json.Marshal(payload)
	if err != nil {
		return err
	}
	if body == nil {
		body = []byte(`{}`)
	}
	reqID := fmt.Sprintf("agent-control-%d", time.Now().UnixNano())
	req, err := json.Marshal(controlEnvelope{
		RequestID: reqID,
		Version:   1,
		Op:        op,
		Payload:   body,
	})
	if err != nil {
		return err
	}
	if err := binary.Write(conn, binary.BigEndian, uint32(len(req))); err != nil {
		return fmt.Errorf("write control frame length: %w", err)
	}
	if _, err := conn.Write(req); err != nil {
		return fmt.Errorf("write control frame: %w", err)
	}

	var n uint32
	if err := binary.Read(conn, binary.BigEndian, &n); err != nil {
		return fmt.Errorf("read control frame length: %w", err)
	}
	if n > 1<<20 {
		return fmt.Errorf("control frame of %d bytes exceeds 1 MiB", n)
	}
	frame := make([]byte, n)
	if _, err := io.ReadFull(conn, frame); err != nil {
		return fmt.Errorf("read control frame: %w", err)
	}
	var resp controlResponse
	if err := json.Unmarshal(frame, &resp); err != nil {
		return fmt.Errorf("decode control response: %w", err)
	}
	if resp.RequestID != reqID {
		return fmt.Errorf("control response request_id %q does not match %q", resp.RequestID, reqID)
	}
	if resp.Status != "ok" {
		if resp.Error != nil {
			return fmt.Errorf("%s: %s", resp.Error.Code, resp.Error.Message)
		}
		return fmt.Errorf("control op %s failed: %s", op, string(frame))
	}
	if out != nil {
		if err := json.Unmarshal(resp.Result, out); err != nil {
			return fmt.Errorf("decode control result for %s: %w", op, err)
		}
	}
	return nil
}

type ControlEpoch struct {
	KeyEpoch    uint64 `json:"key_epoch"`
	SenderIndex uint32 `json:"sender_index"`
	PeerIndex   uint32 `json:"peer_index"`
	Role        string `json:"role"`
}

type ControlPeer struct {
	LabSessionID uint32         `json:"lab_session_id"`
	VirtualIP    string         `json:"virtual_ip"`
	Endpoint     string         `json:"endpoint"`
	Epochs       []ControlEpoch `json:"epochs"`
}

type ControlSessions struct {
	BackendInstanceID string        `json:"backend_instance_id"`
	Peers             []ControlPeer `json:"peers"`
}

func (c ControlClient) Sessions(ctx context.Context) (ControlSessions, error) {
	var sessions ControlSessions
	err := c.Call(ctx, "GetSessions", map[string]any{}, &sessions)
	return sessions, err
}
