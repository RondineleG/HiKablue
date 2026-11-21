package orchestrator

import (
	"context"
	"crypto/x509"
	"errors"
	"io"
	"net"
	"os"
	"testing"
	"time"

	edgev1 "github.com/RondineleG/HiKablue/agent-go/gen/edge/v1"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"google.golang.org/grpc/test/bufconn"
)

// fakeOrchestrator is a minimal in-process control plane: it records what
// the agent sends and replays one scripted command stream.
type fakeOrchestrator struct {
	edgev1.UnimplementedEdgeOrchestratorServer
	gotRegister  chan *edgev1.NodeRegister
	gotHeartbeat chan *edgev1.NodeHeartbeat
	gotAck       chan *edgev1.CommandAck
	commands     []*edgev1.ControlCommand
	acksSeen     int
}

func (f *fakeOrchestrator) NodeChannel(
	stream edgev1.EdgeOrchestrator_NodeChannelServer) error {
	for _, cmd := range f.commands {
		if err := stream.Send(cmd); err != nil {
			return err
		}
	}
	for {
		msg, err := stream.Recv()
		if err != nil {
			return err
		}
		switch p := msg.Payload.(type) {
		case *edgev1.AgentMessage_Register:
			select {
			case f.gotRegister <- p.Register:
			default:
			}
		case *edgev1.AgentMessage_Heartbeat:
			select {
			case f.gotHeartbeat <- p.Heartbeat:
			default:
			}
		case *edgev1.AgentMessage_CommandAck:
			f.acksSeen++
			select {
			case f.gotAck <- p.CommandAck:
			default:
			}
		}
	}
}

// bufconnServer starts a fake orchestrator on an in-memory pipe and returns
// a client conn pointed at it.
func bufconnServer(t *testing.T, f edgev1.EdgeOrchestratorServer) *grpc.ClientConn {
	t.Helper()
	lis := bufconn.Listen(1 << 20)
	srv := grpc.NewServer()
	edgev1.RegisterEdgeOrchestratorServer(srv, f)
	go srv.Serve(lis)
	t.Cleanup(srv.Stop)
	conn, err := grpc.Dial("bufnet",
		grpc.WithContextDialer(
			func(context.Context, string) (net.Conn, error) {
				return lis.Dial()
			}),
		grpc.WithInsecure())
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { conn.Close() })
	return conn
}

func TestRunRegistersDispatchesAndAcks(t *testing.T) {
	h := &stubHandler{healthy: true}
	fake := &fakeOrchestrator{
		gotRegister:  make(chan *edgev1.NodeRegister, 4),
		gotHeartbeat: make(chan *edgev1.NodeHeartbeat, 4),
		gotAck:       make(chan *edgev1.CommandAck, 4),
		commands: []*edgev1.ControlCommand{
			command("9999999999999", "backend-1", 1,
				time.Now().Add(time.Minute)),
		},
	}
	conn := bufconnServer(t, fake)

	c := NewChannel(Config{
		NodeId:            "edge-1",
		BackendInstanceId: "backend-1",
		AgentVersion:      "test",
		HeartbeatInterval: 20 * time.Millisecond,
	}, h)

	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	runDone := make(chan error, 1)
	go func() { runDone <- c.Run(ctx, conn) }()

	select {
	case reg := <-fake.gotRegister:
		if reg.NodeId != "edge-1" || reg.BackendInstanceId != "backend-1" {
			t.Fatalf("bad register: %+v", reg)
		}
	case <-time.After(time.Second):
		t.Fatal("no NodeRegister received")
	}

	select {
	case ack := <-fake.gotAck:
		if ack.Outcome != edgev1.AckOutcome_ACK_OUTCOME_APPLIED {
			t.Fatalf("expected APPLIED ack, got %v", ack.Outcome)
		}
	case <-time.After(time.Second):
		t.Fatal("no CommandAck received")
	}

	select {
	case hb := <-fake.gotHeartbeat:
		if !hb.EngineHealthy || hb.BackendInstanceId != "backend-1" {
			t.Fatalf("bad heartbeat: %+v", hb)
		}
	case <-time.After(time.Second):
		t.Fatal("no heartbeat received")
	}
	if h.provisioned != 1 {
		t.Fatalf("expected 1 provisioned peer, got %d", h.provisioned)
	}

	cancel()
	select {
	case <-runDone:
	case <-time.After(2 * time.Second):
		t.Fatal("Run did not return after context cancel")
	}
}

func TestReplayStatePersistsAcrossChannelsButNotBackendReplacement(t *testing.T) {
	cfg := Config{NodeId: "edge-1", BackendInstanceId: "backend-1"}
	cfg.ReplayState = NewReplayState(cfg.NodeId, cfg.BackendInstanceId)
	h := &stubHandler{}
	run := func(cfg Config, cmd *edgev1.ControlCommand, wantRegistered, wantApplied uint64) {
		t.Helper()
		fake := &fakeOrchestrator{
			gotRegister: make(chan *edgev1.NodeRegister, 1),
			gotAck:      make(chan *edgev1.CommandAck, 1),
			commands:    []*edgev1.ControlCommand{cmd},
		}
		conn := bufconnServer(t, fake)
		ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cancel()
		done := make(chan error, 1)
		go func() { done <- NewChannel(cfg, h).Run(ctx, conn) }()
		select {
		case reg := <-fake.gotRegister:
			if reg.AppliedRevision != wantRegistered || reg.BackendInstanceId != cfg.BackendInstanceId {
				t.Fatalf("unexpected reconnect register: %v", reg)
			}
		case <-ctx.Done():
			t.Fatal("no register")
		}
		select {
		case ack := <-fake.gotAck:
			if ack.Outcome != edgev1.AckOutcome_ACK_OUTCOME_APPLIED || ack.AppliedRevision != wantApplied {
				t.Fatalf("unexpected reconnect ACK: %v", ack)
			}
		case <-ctx.Done():
			t.Fatal("no reconnect ACK")
		}
		cancel()
		<-done
	}

	now := time.Now()
	run(cfg, command("1000", cfg.BackendInstanceId, 7, now.Add(time.Minute)), 0, 7)
	run(cfg, command("1001", cfg.BackendInstanceId, 7, now.Add(time.Minute)), 7, 7)
	if h.provisioned != 1 {
		t.Fatalf("reconnected channel reapplied a command: %d provisions", h.provisioned)
	}

	cfg.BackendInstanceId = "backend-2"
	snapshot := command("1002", cfg.BackendInstanceId, 1, now.Add(time.Minute))
	snapshot.Payload = &edgev1.ControlCommand_SyncState{SyncState: &edgev1.SyncState{}}
	run(cfg, snapshot, 0, 1)
	if h.synced != 1 {
		t.Fatal("replacement backend did not reconcile")
	}
}

func TestChannelCannotShareReplayStateAcrossConcurrentRuns(t *testing.T) {
	cfg := Config{NodeId: "edge-1", BackendInstanceId: "backend-1"}
	cfg.ReplayState = NewReplayState(cfg.NodeId, cfg.BackendInstanceId)
	first := NewChannel(cfg, &stubHandler{})
	first.replay.runMu.Lock()
	defer first.replay.runMu.Unlock()
	if err := NewChannel(cfg, &stubHandler{}).run(context.Background(), nil); err == nil {
		t.Fatal("second channel unexpectedly took ownership of replay state")
	}
}

func TestDialFailsWithoutCaFile(t *testing.T) {
	_, err := Dial(context.Background(), Config{
		Address:  "127.0.0.1:1",
		CaPath:   "/nonexistent/ca.crt",
		CertPath: "x", KeyPath: "y",
	})
	if err == nil {
		t.Fatal("expected error for missing CA file")
	}
}

func TestDialBuildsConnectionWithValidMaterial(t *testing.T) {
	dir := t.TempDir()
	ca := newTestCA(t)
	cert, key := ca.issue(t, "edge-1",
		[]string{"spiffe://lab.hikablue/node/edge-1"},
		x509.ExtKeyUsageClientAuth)
	caPath := dir + "/ca.crt"
	if err := os.WriteFile(caPath, ca.certPEM, 0o644); err != nil {
		t.Fatal(err)
	}
	certPath, keyPath := writePair(t, dir, "edge-1", cert, key)

	conn, err := Dial(context.Background(), Config{
		Address: "127.0.0.1:1", // nothing listens; DialContext is lazy
		CaPath:  caPath, CertPath: certPath, KeyPath: keyPath,
	})
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
}

// unimplementedService is registered instead of the real orchestrator so
// the client stream fails at first Recv — exercising Run's error path.
type unimplementedService struct{}

func bufconnNoService(t *testing.T) *grpc.ClientConn {
	t.Helper()
	lis := bufconn.Listen(1 << 20)
	srv := grpc.NewServer()
	go srv.Serve(lis)
	t.Cleanup(srv.Stop)
	conn, err := grpc.Dial("bufnet",
		grpc.WithContextDialer(
			func(context.Context, string) (net.Conn, error) {
				return lis.Dial()
			}),
		grpc.WithInsecure())
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { conn.Close() })
	return conn
}

func TestRunFailsWhenServiceIsMissing(t *testing.T) {
	conn := bufconnNoService(t)
	c := newTestChannel(&stubHandler{})
	err := c.Run(context.Background(), conn)
	if err == nil {
		t.Fatal("expected Run to fail on unimplemented service")
	}
}

func TestHeartbeatLoopExitsOnSendError(t *testing.T) {
	conn := bufconnNoService(t)
	c := NewChannel(Config{BackendInstanceId: "b", HeartbeatInterval: time.Millisecond},
		&stubHandler{healthy: true})
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	stream, err := edgev1.NewEdgeOrchestratorClient(conn).NodeChannel(ctx)
	if err != nil {
		t.Fatal(err)
	}
	// With no orchestrator registered, gRPC ends the stream and Send returns EOF.
	if err := c.heartbeatLoop(ctx, cancel, stream); !errors.Is(err, io.EOF) {
		t.Fatalf("expected EOF from heartbeat send, got %v", err)
	}
}

func TestHeartbeatLoopLeavesReceiveAliveAfterEOF(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	stream := &lifecycleStream{heartbeatErr: io.EOF}
	c := NewChannel(Config{HeartbeatInterval: time.Millisecond}, &stubHandler{})

	if err := c.heartbeatLoop(ctx, cancel, stream); !errors.Is(err, io.EOF) {
		t.Fatalf("expected heartbeat EOF, got %v", err)
	}
	if err := ctx.Err(); err != nil {
		t.Fatalf("heartbeat EOF cancelled the receive context: %v", err)
	}
}

type lifecycleClient struct {
	stream *lifecycleStream
}

func (c lifecycleClient) NodeChannel(ctx context.Context,
	_ ...grpc.CallOption) (edgev1.EdgeOrchestrator_NodeChannelClient, error) {
	c.stream.ctx = ctx
	return c.stream, nil
}

type lifecycleStream struct {
	grpc.ClientStream
	ctx           context.Context
	recvErr       error
	heartbeatErr  error
	heartbeatSent chan struct{}
}

func (s *lifecycleStream) Send(msg *edgev1.AgentMessage) error {
	if msg.GetHeartbeat() != nil {
		if s.heartbeatSent != nil {
			close(s.heartbeatSent)
		}
		return s.heartbeatErr
	}
	return nil
}

func (s *lifecycleStream) Recv() (*edgev1.ControlCommand, error) {
	if s.heartbeatSent != nil {
		select {
		case <-s.heartbeatSent:
		case <-s.ctx.Done():
			return nil, s.ctx.Err()
		}
	}
	if s.recvErr != nil {
		return nil, s.recvErr
	}
	<-s.ctx.Done()
	return nil, s.ctx.Err()
}

func TestRunCancelsStreamAfterReceiveFailure(t *testing.T) {
	stream := &lifecycleStream{recvErr: errors.New("receive failed")}
	c := NewChannel(Config{HeartbeatInterval: time.Hour}, &stubHandler{})
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	if err := c.run(ctx, lifecycleClient{stream}); !errors.Is(err, stream.recvErr) {
		t.Fatalf("expected receive error, got %v", err)
	}
	if ctx.Err() != nil {
		t.Fatal("Run cancelled its caller")
	}
	if stream.ctx.Err() == nil {
		t.Fatal("Run returned without cancelling the stream and heartbeat")
	}
}

func TestRunHeartbeatFailureUnblocksReceive(t *testing.T) {
	stream := &lifecycleStream{heartbeatErr: errors.New("heartbeat failed")}
	c := NewChannel(Config{HeartbeatInterval: time.Millisecond}, &stubHandler{})
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	done := make(chan error, 1)
	go func() { done <- c.run(ctx, lifecycleClient{stream}) }()
	select {
	case err := <-done:
		if !errors.Is(err, stream.heartbeatErr) {
			t.Fatalf("expected heartbeat error, got %v", err)
		}
	case <-time.After(time.Second):
		t.Fatal("heartbeat failure left Run blocked in Recv")
	}
}

func TestRunPreservesServerStatusAfterHeartbeatEOF(t *testing.T) {
	stream := &lifecycleStream{
		recvErr:       status.Error(codes.PermissionDenied, "node is not allowed"),
		heartbeatErr:  io.EOF,
		heartbeatSent: make(chan struct{}),
	}
	c := NewChannel(Config{HeartbeatInterval: time.Millisecond}, &stubHandler{})
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	if err := c.run(ctx, lifecycleClient{stream}); !errors.Is(err, stream.recvErr) {
		t.Fatalf("expected server status %v, got %v", stream.recvErr, err)
	}
}

type delayedStatusServer struct {
	edgev1.UnimplementedEdgeOrchestratorServer
	firstHeartbeat chan struct{}
	finish         chan struct{}
}

func (s *delayedStatusServer) NodeChannel(stream edgev1.EdgeOrchestrator_NodeChannelServer) error {
	for {
		msg, err := stream.Recv()
		if err != nil {
			return err
		}
		if msg.GetHeartbeat() != nil {
			close(s.firstHeartbeat)
			break
		}
	}
	select {
	case <-s.finish:
		return status.Error(codes.PermissionDenied, "node is not allowed")
	case <-stream.Context().Done():
		return stream.Context().Err()
	}
}

func TestHeartbeatEOFLeavesFinalStatusReadableOnGRPCTransport(t *testing.T) {
	server := &delayedStatusServer{firstHeartbeat: make(chan struct{}), finish: make(chan struct{})}
	conn := bufconnServer(t, server)
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	stream, err := edgev1.NewEdgeOrchestratorClient(conn).NodeChannel(ctx)
	if err != nil {
		t.Fatal(err)
	}
	c := NewChannel(Config{HeartbeatInterval: time.Millisecond}, &stubHandler{})
	done := make(chan error, 1)
	go func() { done <- c.heartbeatLoop(ctx, cancel, stream) }()
	select {
	case <-server.firstHeartbeat:
	case <-ctx.Done():
		t.Fatal("heartbeat did not reach the open stream")
	}
	close(server.finish)
	if err := <-done; !errors.Is(err, io.EOF) {
		t.Fatalf("expected heartbeat send EOF, got %v", err)
	}
	if _, err := stream.Recv(); status.Code(err) != codes.PermissionDenied {
		t.Fatalf("expected final server status after EOF, got %v", err)
	}
}
