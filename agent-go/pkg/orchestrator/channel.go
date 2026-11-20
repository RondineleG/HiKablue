package orchestrator

import (
	"context"
	"errors"
	"fmt"
	"io"
	"strconv"
	"sync"
	"sync/atomic"
	"time"

	edgev1 "github.com/RondineleG/HiKablue/agent-go/gen/edge/v1"
	"github.com/golang/protobuf/ptypes"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
)

// defaultHeartbeatInterval is how often NodeHeartbeat is sent on the
// stream when Config.HeartbeatInterval is zero.
const defaultHeartbeatInterval = 10 * time.Second

// Handler applies control-plane commands to the local network backend.
// Implementations must be idempotent: the dispatcher may replay a command
// after a reconnect.
type Handler interface {
	ProvisionPeer(ctx context.Context, p *edgev1.ProvisionPeer) error
	RevokePeer(ctx context.Context, p *edgev1.RevokePeer) error
	SyncState(ctx context.Context, p *edgev1.SyncState) error
	// EngineHealthy reports whether the local engine is running; it is
	// copied into each NodeHeartbeat.
	EngineHealthy() bool
}

// Channel runs one NodeChannel stream against the control plane. Reconnects
// create a new Channel with a fresh stream epoch.
type Channel struct {
	cfg       Config
	handler   Handler
	heartBeat func(ctx context.Context) (healthy bool, metrics map[string]int64)

	streamEpoch     atomic.Value // string
	appliedRevision uint64
	// grpc-go forbids concurrent Send calls on one stream; the heartbeat
	// goroutine and the command loop share this mutex.
	sendMu sync.Mutex
}

// NewChannel returns a Channel for the given config and command handler.
func NewChannel(cfg Config, h Handler) *Channel {
	c := &Channel{cfg: cfg, handler: h}
	c.streamEpoch.Store("")
	return c
}

// Run keeps one NodeChannel alive until ctx is cancelled or the stream
// fails. It sends NodeRegister first, heartbeats in a goroutine, and
// dispatches commands with the spec fencing rules.
func (c *Channel) Run(ctx context.Context, conn *grpc.ClientConn) error {
	client := edgev1.NewEdgeOrchestratorClient(conn)
	stream, err := client.NodeChannel(ctx)
	if err != nil {
		return fmt.Errorf("orchestrator: open NodeChannel: %w", err)
	}

	register := &edgev1.AgentMessage{Payload: &edgev1.AgentMessage_Register{
		Register: &edgev1.NodeRegister{
			NodeId:            c.cfg.NodeId,
			BackendInstanceId: c.cfg.BackendInstanceId,
			AgentVersion:      c.cfg.AgentVersion,
			AppliedRevision:   atomic.LoadUint64(&c.appliedRevision),
		},
	}}
	if err := stream.Send(register); err != nil {
		return fmt.Errorf("orchestrator: send NodeRegister: %w", err)
	}
	// The stream epoch is the Unix-millis timestamp assigned to this stream;
	// the first command tells us the server's authoritative value, until
	// then fence on our own start time.
	c.streamEpoch.Store(strconv.FormatInt(time.Now().UnixMilli(), 10))

	errCh := make(chan error, 1)
	go func() { errCh <- c.heartbeatLoop(ctx, stream) }()

	for {
		cmd, err := stream.Recv()
		if err != nil {
			if errors.Is(err, io.EOF) {
				err = fmt.Errorf("orchestrator: stream closed by server")
			}
			return fmt.Errorf("orchestrator: recv: %w", err)
		}
		ack, dispatch := c.evaluate(cmd, time.Now())
		if dispatch {
			if err := c.dispatch(ctx, cmd); err != nil {
				ack.Outcome = edgev1.AckOutcome_ACK_OUTCOME_ERROR
				ack.ErrorCode = edgev1.AckErrorCode_ACK_ERROR_INTERNAL
				ack.Error = err.Error()
			} else {
				atomic.StoreUint64(&c.appliedRevision, cmd.Revision)
				ack.AppliedRevision = cmd.Revision
			}
		}
		if ack != nil {
			c.sendMu.Lock()
			err := stream.Send(&edgev1.AgentMessage{
				Payload: &edgev1.AgentMessage_CommandAck{CommandAck: ack},
			})
			c.sendMu.Unlock()
			if err != nil {
				return fmt.Errorf("orchestrator: send CommandAck: %w", err)
			}
		}
	}
}

// evaluate applies the fencing rules to an incoming command and decides
// whether it may be dispatched locally. The returned ack is nil only when
// the command must be dropped without a reply (none today).
func (c *Channel) evaluate(cmd *edgev1.ControlCommand, now time.Time) (
	ack *edgev1.CommandAck, dispatch bool) {
	ack = &edgev1.CommandAck{
		CommandId:         cmd.CommandId,
		StreamEpoch:       cmd.StreamEpoch,
		BackendInstanceId: c.cfg.BackendInstanceId,
		AppliedRevision:   atomic.LoadUint64(&c.appliedRevision),
		Outcome:           edgev1.AckOutcome_ACK_OUTCOME_APPLIED,
	}

	// Fence stale streams: an epoch older than the current stream's means the
	// control plane has already fenced this stream off; reject, don't apply.
	if epochLess(cmd.StreamEpoch, c.currentEpoch()) {
		ack.Outcome = edgev1.AckOutcome_ACK_OUTCOME_REJECTED
		ack.ErrorCode = edgev1.AckErrorCode_ACK_ERROR_FENCED_STREAM
		ack.Error = "stale stream_epoch"
		return ack, false
	}
	// Adopt the server's epoch on first valid command of this stream.
	if epochGreater(cmd.StreamEpoch, c.currentEpoch()) {
		c.streamEpoch.Store(cmd.StreamEpoch)
	}
	// A command stamped for another backend instance means this process
	// has been replaced; it must not apply anything anymore.
	if cmd.BackendInstanceId != "" && cmd.BackendInstanceId != c.cfg.BackendInstanceId {
		ack.Outcome = edgev1.AckOutcome_ACK_OUTCOME_REJECTED
		ack.ErrorCode = edgev1.AckErrorCode_ACK_ERROR_FENCED_BACKEND
		ack.Error = "command targets a replaced backend instance"
		return ack, false
	}
	// Deadline fencing: expired commands must not change local state.
	if cmd.Deadline != nil {
		deadline, err := ptypes.Timestamp(cmd.Deadline)
		if err == nil && now.After(deadline) {
			ack.Outcome = edgev1.AckOutcome_ACK_OUTCOME_REJECTED
			ack.ErrorCode = edgev1.AckErrorCode_ACK_ERROR_DEADLINE_EXCEEDED
			ack.Error = fmt.Sprintf("deadline %s already expired", deadline.UTC())
			return ack, false
		}
	}
	// Revision fencing: a revision at or below the last applied one is a
	// replay/regression — acknowledge it without re-applying state.
	if cmd.Revision != 0 && cmd.Revision <= atomic.LoadUint64(&c.appliedRevision) {
		ack.Outcome = edgev1.AckOutcome_ACK_OUTCOME_REJECTED
		ack.ErrorCode = edgev1.AckErrorCode_ACK_ERROR_REVISION_REGRESSION
		ack.Error = fmt.Sprintf("revision %d <= applied %d",
			cmd.Revision, atomic.LoadUint64(&c.appliedRevision))
		return ack, false
	}
	return ack, true
}

// dispatch routes a fenced-valid command payload to the local handler.
func (c *Channel) dispatch(ctx context.Context, cmd *edgev1.ControlCommand) error {
	switch p := cmd.Payload.(type) {
	case *edgev1.ControlCommand_ProvisionPeer:
		return c.handler.ProvisionPeer(ctx, p.ProvisionPeer)
	case *edgev1.ControlCommand_RevokePeer:
		return c.handler.RevokePeer(ctx, p.RevokePeer)
	case *edgev1.ControlCommand_SyncState:
		return c.handler.SyncState(ctx, p.SyncState)
	default:
		return fmt.Errorf("unsupported command payload %T", cmd.Payload)
	}
}

func (c *Channel) currentEpoch() string {
	if s, ok := c.streamEpoch.Load().(string); ok {
		return s
	}
	return ""
}

func (c *Channel) heartbeatLoop(ctx context.Context,
	stream edgev1.EdgeOrchestrator_NodeChannelClient) error {
	interval := c.cfg.HeartbeatInterval
	if interval <= 0 {
		interval = defaultHeartbeatInterval
	}
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-ticker.C:
			msg := &edgev1.AgentMessage{Payload: &edgev1.AgentMessage_Heartbeat{
				Heartbeat: &edgev1.NodeHeartbeat{
					BackendInstanceId: c.cfg.BackendInstanceId,
					AppliedRevision:   atomic.LoadUint64(&c.appliedRevision),
					EngineHealthy:     c.handler.EngineHealthy(),
				},
			}}
			c.sendMu.Lock()
			err := stream.Send(msg)
			c.sendMu.Unlock()
			if err != nil {
				if s, ok := status.FromError(err); ok && s.Code() == codes.Unavailable {
					return fmt.Errorf("orchestrator: heartbeat send: %w", err)
				}
				return fmt.Errorf("orchestrator: heartbeat send: %w", err)
			}
		}
	}
}

// epochLess compares two Unix-millis epoch strings numerically when
// possible, lexically otherwise.
func epochLess(a, b string) bool {
	ai, aerr := strconv.ParseInt(a, 10, 64)
	bi, berr := strconv.ParseInt(b, 10, 64)
	if aerr == nil && berr == nil {
		return ai < bi
	}
	return a < b
}

func epochGreater(a, b string) bool {
	ai, aerr := strconv.ParseInt(a, 10, 64)
	bi, berr := strconv.ParseInt(b, 10, 64)
	if aerr == nil && berr == nil {
		return ai > bi
	}
	return a > b
}
