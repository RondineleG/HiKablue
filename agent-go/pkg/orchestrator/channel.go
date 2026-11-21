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
	"google.golang.org/grpc"
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

// ReplayState keeps command history in memory for one node/backend pair.
// Reuse it only while that backend retains its applied state. The Go
// process does not persist this history; handlers must remain idempotent.
type ReplayState struct {
	appliedRevision uint64
	nodeID          string
	backendID       string
	runMu           sync.Mutex
	appliedCommands map[string]struct{}
}

// NewReplayState binds an empty replay history to a running backend.
func NewReplayState(nodeID, backendInstanceID string) *ReplayState {
	return &ReplayState{
		nodeID:          nodeID,
		backendID:       backendInstanceID,
		appliedCommands: make(map[string]struct{}),
	}
}

// Channel runs one NodeChannel stream at a time. A subsequent Run resets
// the stream epoch, not the replay history of its backend.
type Channel struct {
	cfg         Config
	handler     Handler
	replay      *ReplayState
	streamEpoch uint64
	// grpc-go forbids concurrent Send calls on one stream; the heartbeat
	// goroutine and the command loop share this mutex.
	sendMu sync.Mutex
}

// NewChannel returns a Channel for the given config and command handler.
func NewChannel(cfg Config, h Handler) *Channel {
	replay := cfg.ReplayState
	if replay == nil || replay.nodeID != cfg.NodeId || replay.backendID != cfg.BackendInstanceId ||
		cfg.NodeId == "" || cfg.BackendInstanceId == "" {
		replay = NewReplayState(cfg.NodeId, cfg.BackendInstanceId)
	}
	return &Channel{cfg: cfg, handler: h, replay: replay}
}

// Run keeps one NodeChannel alive until ctx is cancelled or the stream
// fails. It sends NodeRegister first, heartbeats in a goroutine, and
// dispatches commands with the spec fencing rules.
func (c *Channel) Run(ctx context.Context, conn *grpc.ClientConn) error {
	return c.run(ctx, edgev1.NewEdgeOrchestratorClient(conn))
}

func (c *Channel) run(ctx context.Context, client edgev1.EdgeOrchestratorClient) error {
	if !c.replay.runMu.TryLock() {
		return fmt.Errorf("orchestrator: backend already has an active channel")
	}
	defer c.replay.runMu.Unlock()
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	stream, err := client.NodeChannel(ctx)
	if err != nil {
		return fmt.Errorf("orchestrator: open NodeChannel: %w", err)
	}

	register := &edgev1.AgentMessage{Payload: &edgev1.AgentMessage_Register{
		Register: &edgev1.NodeRegister{
			NodeId:            c.cfg.NodeId,
			BackendInstanceId: c.cfg.BackendInstanceId,
			AgentVersion:      c.cfg.AgentVersion,
			AppliedRevision:   atomic.LoadUint64(&c.replay.appliedRevision),
		},
	}}
	if err := stream.Send(register); err != nil {
		return fmt.Errorf("orchestrator: send NodeRegister: %w", err)
	}
	// The numeric stream epoch is assigned monotonically by the server;
	// fencing on a locally computed timestamp would reject the
	// first command whenever the agent clock runs ahead.
	c.streamEpoch = 0

	heartbeatDone := make(chan struct{})
	var heartbeatErr error
	go func() {
		defer close(heartbeatDone)
		heartbeatErr = c.heartbeatLoop(ctx, cancel, stream)
	}()
	defer func() {
		cancel()
		<-heartbeatDone
	}()

	for {
		cmd, err := stream.Recv()
		if err != nil {
			cancel()
			<-heartbeatDone
			if heartbeatErr != nil &&
				!errors.Is(heartbeatErr, io.EOF) &&
				!errors.Is(heartbeatErr, context.Canceled) &&
				!errors.Is(heartbeatErr, context.DeadlineExceeded) {
				return heartbeatErr
			}
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
				if cmd.Revision > ack.AppliedRevision {
					atomic.StoreUint64(&c.replay.appliedRevision, cmd.Revision)
					ack.AppliedRevision = cmd.Revision
				}
				c.replay.appliedCommands[cmd.CommandId] = struct{}{}
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
		AppliedRevision:   atomic.LoadUint64(&c.replay.appliedRevision),
		Outcome:           edgev1.AckOutcome_ACK_OUTCOME_APPLIED,
	}

	epoch, err := strconv.ParseUint(cmd.StreamEpoch, 10, 63)
	if err != nil || epoch == 0 {
		ack.Outcome = edgev1.AckOutcome_ACK_OUTCOME_REJECTED
		ack.ErrorCode = edgev1.AckErrorCode_ACK_ERROR_FENCED_STREAM
		ack.Error = "invalid stream_epoch"
		return ack, false
	}
	if epoch < c.streamEpoch {
		ack.Outcome = edgev1.AckOutcome_ACK_OUTCOME_REJECTED
		ack.ErrorCode = edgev1.AckErrorCode_ACK_ERROR_FENCED_STREAM
		ack.Error = "stale stream_epoch"
		return ack, false
	}
	// A command stamped for another backend instance means this process
	// has been replaced; it must not apply anything anymore.
	if cmd.BackendInstanceId != c.cfg.BackendInstanceId {
		ack.Outcome = edgev1.AckOutcome_ACK_OUTCOME_REJECTED
		ack.ErrorCode = edgev1.AckErrorCode_ACK_ERROR_FENCED_BACKEND
		ack.Error = "command targets a replaced backend instance"
		return ack, false
	}
	if err := cmd.Deadline.CheckValid(); err != nil {
		ack.Outcome = edgev1.AckOutcome_ACK_OUTCOME_REJECTED
		ack.ErrorCode = edgev1.AckErrorCode_ACK_ERROR_INVALID_PAYLOAD
		ack.Error = "missing or invalid deadline"
		return ack, false
	}
	// An already-applied command can be confirmed without applying anything.
	if _, applied := c.replay.appliedCommands[cmd.CommandId]; applied {
		c.streamEpoch = epoch
		return ack, false
	}
	deadline := cmd.Deadline.AsTime()
	if !now.Before(deadline) {
		ack.Outcome = edgev1.AckOutcome_ACK_OUTCOME_REJECTED
		ack.ErrorCode = edgev1.AckErrorCode_ACK_ERROR_DEADLINE_EXCEEDED
		ack.Error = fmt.Sprintf("deadline %s already expired", deadline)
		return ack, false
	}
	// Revision fencing: a revision at or below the last applied one is a
	// replay/regression — acknowledge it without re-applying state.
	if cmd.Revision != 0 && cmd.Revision <= ack.AppliedRevision {
		ack.Outcome = edgev1.AckOutcome_ACK_OUTCOME_REJECTED
		ack.ErrorCode = edgev1.AckErrorCode_ACK_ERROR_REVISION_REGRESSION
		ack.Error = fmt.Sprintf("revision %d <= applied %d",
			cmd.Revision, ack.AppliedRevision)
		return ack, false
	}
	c.streamEpoch = epoch
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

func (c *Channel) heartbeatLoop(ctx context.Context, cancel context.CancelFunc,
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
					AppliedRevision:   atomic.LoadUint64(&c.replay.appliedRevision),
					EngineHealthy:     c.handler.EngineHealthy(),
				},
			}}
			c.sendMu.Lock()
			err := stream.Send(msg)
			c.sendMu.Unlock()
			if err != nil {
				// gRPC Send may return EOF before Recv delivers the final status.
				// Leave the receive alive in that case.
				if !errors.Is(err, io.EOF) {
					cancel()
				}
				return fmt.Errorf("orchestrator: heartbeat send: %w", err)
			}
		}
	}
}
