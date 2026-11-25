package agent

import (
	"context"
	"crypto/sha256"
	"encoding/binary"
	"encoding/json"
	"io"
	"net"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	edgev1 "github.com/RondineleG/HiKablue/agent-go/gen/edge/v1"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"
)

type recordedOp struct {
	Op      string
	Payload map[string]any
}

func controlServer(t *testing.T, sessions ControlSessions) (string, func() []recordedOp) {
	t.Helper()
	return controlServerWithHook(t, sessions, nil)
}

func controlServerWithHook(t *testing.T, sessions ControlSessions, respond func(string) bool) (string, func() []recordedOp) {
	t.Helper()
	path := filepath.Join(t.TempDir(), "control.sock")
	l, err := net.Listen("unix", path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { l.Close() })
	var mu sync.Mutex
	ops := make([]recordedOp, 0)
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
				var req struct {
					RequestID string          `json:"request_id"`
					Op        string          `json:"op"`
					Payload   json.RawMessage `json:"payload"`
				}
				_ = json.Unmarshal(buf, &req)
				var payload map[string]any
				_ = json.Unmarshal(req.Payload, &payload)
				mu.Lock()
				ops = append(ops, recordedOp{Op: req.Op, Payload: payload})
				mu.Unlock()
				if respond != nil && !respond(req.Op) {
					return
				}
				result := map[string]any{}
				if req.Op == "GetSessions" {
					out, _ := json.Marshal(sessions)
					_ = json.Unmarshal(out, &result)
				}
				resp, _ := json.Marshal(map[string]any{
					"request_id": req.RequestID, "version": 1, "status": "ok", "result": result,
				})
				_ = binary.Write(c, binary.BigEndian, uint32(len(resp)))
				_, _ = c.Write(resp)
			}(c)
		}
	}()
	return path, func() []recordedOp {
		mu.Lock()
		defer mu.Unlock()
		return append([]recordedOp(nil), ops...)
	}
}

func testPeer(lab uint32, epoch uint64) *edgev1.Peer {
	return &edgev1.Peer{
		SessionId:    "00000000-0000-0000-0000-000000000001",
		LabSessionId: lab,
		VirtualIp:    "10.80.1.2",
		ExpiresAt:    timestamppb.New(time.Now().Add(time.Hour)),
		KeyRef:       "ref",
		KeyEpoch:     epoch,
	}
}

func testProvision(lab uint32, epoch uint64) *edgev1.ProvisionPeer {
	return &edgev1.ProvisionPeer{
		Peer: testPeer(lab, epoch),
		KeyMaterial: &edgev1.PeerKeyMaterial{
			KeyEpoch:    epoch,
			EgressKey:   bytes32(1),
			IngressKey:  bytes32(2),
			SenderIndex: 11,
			PeerIndex:   22,
		},
	}
}

func bytes32(v byte) []byte {
	b := make([]byte, 32)
	for i := range b {
		b[i] = v
	}
	return b
}

func TestProvisionStagesAndActivatesWithHexKeys(t *testing.T) {
	path, ops := controlServer(t, ControlSessions{})
	h := NewEngineHandler(path, filepath.Join(t.TempDir(), "metrics.sock"))
	if err := h.ProvisionPeer(context.Background(), testProvision(7, 3)); err != nil {
		t.Fatal(err)
	}
	got := ops()
	if len(got) != 2 || got[0].Op != "StagePeer" || got[1].Op != "Activate" {
		t.Fatalf("unexpected ops: %+v", got)
	}
	if got[0].Payload["lab_session_id"].(float64) != 7 || got[0].Payload["key_epoch"].(float64) != 3 {
		t.Fatalf("unexpected stage payload: %+v", got[0].Payload)
	}
	if got[0].Payload["egress_key"].(string) != strings.Repeat("01", 32) {
		t.Fatalf("egress key was not hex encoded: %+v", got[0].Payload)
	}
	if got[1].Payload["key_epoch"].(float64) != 3 {
		t.Fatalf("unexpected activate payload: %+v", got[1].Payload)
	}
}

func TestProvisionRejectsInvalidMaterial(t *testing.T) {
	h := NewEngineHandler("missing", "missing")
	p := testProvision(1, 1)
	p.KeyMaterial.IngressKey = p.KeyMaterial.EgressKey
	if err := h.ProvisionPeer(context.Background(), p); err == nil {
		t.Fatal("identical direction keys accepted")
	}
	p = testProvision(1, 1)
	p.KeyMaterial.KeyEpoch = 2
	if err := h.ProvisionPeer(context.Background(), p); err == nil {
		t.Fatal("mismatched epoch accepted")
	}
	p = testProvision(1, 1)
	p.Peer.VirtualIp = "not-an-ip"
	if err := h.ProvisionPeer(context.Background(), p); err == nil {
		t.Fatal("invalid virtual IP accepted")
	}
}

func TestRevokeAndExpiryCallControlSocket(t *testing.T) {
	path, ops := controlServer(t, ControlSessions{})
	h := NewEngineHandler(path, "missing")
	if err := h.ProvisionPeer(context.Background(), testProvision(4, 1)); err != nil {
		t.Fatal(err)
	}
	if err := h.RevokePeer(context.Background(), &edgev1.RevokePeer{LabSessionId: 4}); err != nil {
		t.Fatal(err)
	}
	h.mu.Lock()
	h.expires[5] = time.Now().Add(-time.Second)
	h.mu.Unlock()
	h.ExpireDuePeers(context.Background())
	got := ops()
	if got[2].Op != "RevokePeer" || got[3].Op != "RevokePeer" {
		t.Fatalf("unexpected ops: %+v", got)
	}
	if got[2].Payload["lab_session_id"].(float64) != 4 || got[3].Payload["lab_session_id"].(float64) != 5 {
		t.Fatalf("unexpected revoke payloads: %+v", got[2:])
	}
}

func snapshotBytes(t *testing.T, peers ...*edgev1.Peer) ([]byte, *edgev1.PeerSnapshot) {
	t.Helper()
	raw, err := proto.Marshal(&edgev1.PeerList{Peers: peers})
	if err != nil {
		t.Fatal(err)
	}
	digest := sha256.Sum256(raw)
	return raw, &edgev1.PeerSnapshot{
		Revision:          9,
		DeclaredPeerCount: uint32(len(peers)),
		SerializedLength:  uint32(len(raw)),
		Sha256Digest:      digest[:],
		PeerList:          raw,
	}
}

func TestSyncStateValidatesFramingAndKeepsDesiredPeers(t *testing.T) {
	peer := testPeer(7, 1)
	raw, snap := snapshotBytes(t, peer)
	path, ops := controlServer(t, ControlSessions{
		BackendInstanceID: "backend",
		Peers:             []ControlPeer{{LabSessionID: 7, VirtualIP: peer.VirtualIp}},
	})
	h := NewEngineHandler(path, "missing")
	if err := h.SyncState(context.Background(), &edgev1.SyncState{Snapshot: snap}); err != nil {
		t.Fatal(err)
	}
	if len(ops()) != 1 {
		t.Fatalf("unexpected mutation for converged snapshot: %+v", ops())
	}
	bad := proto.Clone(snap).(*edgev1.PeerSnapshot)
	bad.Sha256Digest = bytes32(9)
	if err := h.SyncState(context.Background(), &edgev1.SyncState{Snapshot: bad}); err == nil {
		t.Fatal("bad digest accepted")
	}
	bad = proto.Clone(snap).(*edgev1.PeerSnapshot)
	bad.SerializedLength++
	bad.PeerList = raw
	if err := h.SyncState(context.Background(), &edgev1.SyncState{Snapshot: bad}); err == nil {
		t.Fatal("bad serialized length accepted")
	}
}

func TestSyncStateToleratesLocallyExpiredPeer(t *testing.T) {
	peer := testPeer(7, 1)
	peer.ExpiresAt = timestamppb.New(time.Now().Add(-time.Second))
	_, snap := snapshotBytes(t, peer)
	path, ops := controlServer(t, ControlSessions{})
	h := NewEngineHandler(path, "missing")
	if err := h.SyncState(context.Background(), &edgev1.SyncState{Snapshot: snap}); err != nil {
		t.Fatal(err)
	}
	if len(ops()) != 1 {
		t.Fatalf("expired missing peer caused mutation: %+v", ops())
	}

	peer.ExpiresAt = timestamppb.New(time.Now().Add(time.Hour))
	_, snap = snapshotBytes(t, peer)
	if err := h.SyncState(context.Background(), &edgev1.SyncState{Snapshot: snap}); err == nil {
		t.Fatal("unexpired missing peer accepted")
	}
}

func TestSyncStateDeletionGuardAndForcedRemoval(t *testing.T) {
	_, snap := snapshotBytes(t)
	path, ops := controlServer(t, ControlSessions{
		Peers: []ControlPeer{{LabSessionID: 7, VirtualIP: "10.80.1.2"}},
	})
	h := NewEngineHandler(path, "missing")
	if err := h.SyncState(context.Background(), &edgev1.SyncState{Snapshot: snap}); err == nil {
		t.Fatal("empty replacement bypassed deletion guard")
	}
	if err := h.SyncState(context.Background(), &edgev1.SyncState{Snapshot: snap, Force: true}); err != nil {
		t.Fatal(err)
	}
	got := ops()
	if len(got) != 3 || got[2].Op != "RevokePeer" {
		t.Fatalf("forced sync did not revoke extra peer: %+v", got)
	}
}

func TestExpiryStateSurvivesAgentRestart(t *testing.T) {
	provision := testProvision(8, 1)
	provision.Peer.ExpiresAt = timestamppb.New(time.Now().Add(20 * time.Millisecond))
	path, ops := controlServer(t, ControlSessions{})
	h := NewEngineHandler(path, "missing")
	h.ExpiryStatePath = filepath.Join(t.TempDir(), "expiry.json")
	if err := h.ProvisionPeer(context.Background(), provision); err != nil {
		t.Fatal(err)
	}
	time.Sleep(30 * time.Millisecond)

	restarted := NewEngineHandler(path, "missing")
	restarted.ExpiryStatePath = h.ExpiryStatePath
	if err := restarted.LoadExpiryState(); err != nil {
		t.Fatal(err)
	}
	restarted.ExpireDuePeers(context.Background())

	got := ops()
	if len(got) != 3 || got[2].Op != "RevokePeer" {
		t.Fatalf("restarted agent did not expire persisted peer: %+v", got)
	}
	if err := restarted.LoadExpiryState(); err != nil {
		t.Fatal(err)
	}
	restarted.mu.Lock()
	remaining := len(restarted.expires)
	restarted.mu.Unlock()
	if remaining != 0 {
		t.Fatalf("expired peer remained in persisted state: %d entries", remaining)
	}
}

func TestPrepareExpiryStateDirValidatesPrivateWritableDirectory(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "agent", "state")
	path, err := PrepareExpiryStateDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	if filepath.Base(path) != "expiry.json" || filepath.Dir(path) != dir {
		t.Fatalf("unexpected expiry state path %q", path)
	}
	info, err := os.Stat(dir)
	if err != nil {
		t.Fatal(err)
	}
	if info.Mode().Perm() != 0o700 {
		t.Fatalf("state dir mode %v", info.Mode().Perm())
	}
	shared := filepath.Join(t.TempDir(), "shared")
	if err := os.Mkdir(shared, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(shared, 0o755); err != nil {
		t.Fatal(err)
	}
	if _, err := PrepareExpiryStateDir(shared); err == nil {
		t.Fatal("group/other-accessible state dir accepted")
	}
}

func TestExpiryStateAlreadyExpiredAtStartupIsEnforced(t *testing.T) {
	path, ops := controlServer(t, ControlSessions{})
	statePath := filepath.Join(t.TempDir(), "expiry.json")
	raw, _ := json.Marshal(expiryStateFile{Peers: map[uint32]time.Time{
		9: time.Now().Add(-time.Second),
	}})
	if err := os.WriteFile(statePath, raw, 0o600); err != nil {
		t.Fatal(err)
	}
	h := NewEngineHandler(path, "missing")
	h.ExpiryStatePath = statePath
	if err := h.LoadExpiryState(); err != nil {
		t.Fatal(err)
	}
	h.ExpireDuePeers(context.Background())
	got := ops()
	if len(got) != 1 || got[0].Op != "RevokePeer" || got[0].Payload["lab_session_id"].(float64) != 9 {
		t.Fatalf("expired deadline was not enforced at startup: %+v", got)
	}
}

func TestExpiryStateValidationAndPersistenceFailures(t *testing.T) {
	disabled := NewEngineHandler("missing", "missing")
	disabled.ExpiryStatePath = ""
	if err := disabled.LoadExpiryState(); err != nil {
		t.Fatal(err)
	}
	disabled.mu.Lock()
	err := disabled.rememberExpiry(7, time.Now())
	disabled.mu.Unlock()
	if err != nil {
		t.Fatal(err)
	}

	dir := t.TempDir()
	statePath := filepath.Join(dir, "expiry.json")
	h := NewEngineHandler(filepath.Join(dir, "control.sock"), "missing")
	h.ExpiryStatePath = dir
	if err := h.LoadExpiryState(); err == nil {
		t.Fatal("directory accepted as expiry state")
	}
	h.ExpiryStatePath = statePath
	if err := os.WriteFile(statePath, []byte("not-json"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := h.LoadExpiryState(); err == nil {
		t.Fatal("invalid JSON accepted as expiry state")
	}
	for _, raw := range []string{
		`{"peers":{"0":"2026-01-01T00:00:00Z"}}`,
		`{"peers":{"7":"0001-01-01T00:00:00Z"}}`,
	} {
		if err := os.WriteFile(statePath, []byte(raw), 0o600); err != nil {
			t.Fatal(err)
		}
		if err := h.LoadExpiryState(); err == nil {
			t.Fatalf("invalid expiry state accepted: %s", raw)
		}
	}
}

func TestProvisionAndRevokeReportExpiryPersistenceFailures(t *testing.T) {
	path, ops := controlServer(t, ControlSessions{})
	h := NewEngineHandler(path, "missing")
	h.ExpiryStatePath = filepath.Join(t.TempDir(), "missing", "expiry.json")
	if err := h.ProvisionPeer(context.Background(), testProvision(8, 1)); err == nil {
		t.Fatal("provision with unwritable expiry state accepted")
	}
	if got := ops(); len(got) != 0 {
		t.Fatalf("failed write must leave the engine untouched, got %+v", got)
	}

	h.ExpiryStatePath = filepath.Join(t.TempDir(), "expiry.json")
	if err := h.ProvisionPeer(context.Background(), testProvision(8, 1)); err != nil {
		t.Fatal(err)
	}
	h.ExpiryStatePath = filepath.Join(t.TempDir(), "missing", "expiry.json")
	if err := h.RevokePeer(context.Background(), &edgev1.RevokePeer{LabSessionId: 8}); err == nil {
		t.Fatal("revoke with unwritable expiry state accepted")
	}
	h.mu.Lock()
	_, stillTracked := h.expires[8]
	h.mu.Unlock()
	if !stillTracked {
		t.Fatal("failed expiry-state delete dropped the in-memory deadline")
	}
}

func TestProvisionPersistsDeadlineBeforeStaging(t *testing.T) {
	statePath := filepath.Join(t.TempDir(), "expiry.json")
	provision := testProvision(8, 1)
	deadline := provision.Peer.ExpiresAt.AsTime()
	path, _ := controlServerWithHook(t, ControlSessions{}, func(op string) bool {
		if op != "StagePeer" && op != "Activate" {
			return true
		}
		restarted := NewEngineHandler("unused", "unused")
		restarted.ExpiryStatePath = statePath
		if err := restarted.LoadExpiryState(); err != nil {
			t.Error(err)
		} else if got := restarted.expires[8]; !got.Equal(deadline) {
			t.Errorf("deadline at %s = %v, want %v", op, got, deadline)
		}
		return op != "Activate" // The engine may have applied Activate before disconnecting.
	})
	h := NewEngineHandler(path, "unused")
	h.ExpiryStatePath = statePath
	if err := h.ProvisionPeer(context.Background(), provision); err == nil {
		t.Fatal("lost activation response accepted")
	}
	if got := h.expires[8]; !got.Equal(deadline) {
		t.Fatalf("deadline after lost response = %v, want %v", got, deadline)
	}
}

func TestExpirySerializesRevocationWithPeerReplacement(t *testing.T) {
	h := NewEngineHandler("unused", "unused")
	path, ops := controlServerWithHook(t, ControlSessions{}, func(op string) bool {
		if op == "RevokePeer" && h.mu.TryLock() {
			h.mu.Unlock()
			t.Error("peer operations can interleave with the expiration revoke")
		}
		return true
	})
	h.Control.SocketPath = path
	h.expires[8] = time.Now().Add(-time.Second)
	h.ExpireDuePeers(context.Background())

	replacement := testProvision(8, 2)
	if err := h.ProvisionPeer(context.Background(), replacement); err != nil {
		t.Fatal(err)
	}
	h.ExpireDuePeers(context.Background())
	if got := ops(); len(got) != 3 || got[0].Op != "RevokePeer" || got[2].Op != "Activate" {
		t.Fatalf("replacement should stay active after expiry: %+v", got)
	}
	if got := h.expires[8]; !got.Equal(replacement.Peer.ExpiresAt.AsTime()) {
		t.Fatalf("replacement deadline was lost: %v", got)
	}
}

func TestExpiryStateRenameFailureIsReported(t *testing.T) {
	path, _ := controlServer(t, ControlSessions{})
	h := NewEngineHandler(path, "missing")
	statePath := filepath.Join(t.TempDir(), "expiry.json")
	if err := os.Mkdir(statePath, 0o700); err != nil {
		t.Fatal(err)
	}
	h.ExpiryStatePath = statePath
	if err := h.ProvisionPeer(context.Background(), testProvision(8, 1)); err == nil {
		t.Fatal("expiry state rename failure accepted")
	}
}

func TestEngineHealthyAndExpiryLoop(t *testing.T) {
	h := NewEngineHandler("missing-control", filepath.Join(t.TempDir(), "missing-metrics"))
	if h.EngineHealthy() {
		t.Fatal("missing metrics socket reported healthy")
	}
	path, ops := controlServer(t, ControlSessions{})
	h.Control.SocketPath = path
	h.ExpiryStatePath = filepath.Join(t.TempDir(), "expiry.json")
	h.mu.Lock()
	h.expires[8] = time.Now().Add(-time.Second)
	h.mu.Unlock()
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		h.RunExpiryLoop(ctx, 5*time.Millisecond)
		close(done)
	}()
	deadline := time.Now().Add(time.Second)
	for time.Now().Before(deadline) {
		if len(ops()) > 0 {
			break
		}
		time.Sleep(5 * time.Millisecond)
	}
	cancel()
	<-done
	if len(ops()) == 0 {
		t.Fatal("expiry loop never revoked the due peer")
	}
}

func TestControlCallReportsProtocolErrors(t *testing.T) {
	c := ControlClient{}
	if err := c.Call(context.Background(), "GetSessions", nil, nil); err == nil {
		t.Fatal("empty socket path accepted")
	}
	c.SocketPath = filepath.Join(t.TempDir(), "missing.sock")
	if err := c.Call(context.Background(), "GetSessions", nil, nil); err == nil {
		t.Fatal("missing socket accepted")
	}
}

func TestValidationRejectBranches(t *testing.T) {
	h := NewEngineHandler("missing", "missing")
	base := testProvision(1, 1)
	cases := []func(*edgev1.ProvisionPeer){
		func(p *edgev1.ProvisionPeer) { p.KeyMaterial = nil },
		func(p *edgev1.ProvisionPeer) { p.KeyMaterial.EgressKey = []byte{1} },
		func(p *edgev1.ProvisionPeer) { p.KeyMaterial.SenderIndex = 0 },
		func(p *edgev1.ProvisionPeer) { p.KeyMaterial.SenderIndex = p.KeyMaterial.PeerIndex },
		func(p *edgev1.ProvisionPeer) { p.Peer = nil },
		func(p *edgev1.ProvisionPeer) { p.Peer.SessionId = "" },
		func(p *edgev1.ProvisionPeer) { p.Peer.LabSessionId = 0 },
		func(p *edgev1.ProvisionPeer) { p.Peer.ExpiresAt = nil },
	}
	for i, mutate := range cases {
		p := proto.Clone(base).(*edgev1.ProvisionPeer)
		mutate(p)
		if err := h.ProvisionPeer(context.Background(), p); err == nil {
			t.Fatalf("case %d accepted invalid provision", i)
		}
	}
	if err := h.RevokePeer(context.Background(), &edgev1.RevokePeer{}); err == nil {
		t.Fatal("zero lab revocation accepted")
	}
	if err := h.SyncState(context.Background(), &edgev1.SyncState{}); err == nil {
		t.Fatal("missing snapshot accepted")
	}
}

func TestSyncRejectsDuplicatesAndMissingLocalPeer(t *testing.T) {
	peer := testPeer(7, 1)
	_, dup := snapshotBytes(t, peer, peer)
	h := NewEngineHandler("missing", "missing")
	if err := h.SyncState(context.Background(), &edgev1.SyncState{Snapshot: dup}); err == nil {
		t.Fatal("duplicate lab session accepted")
	}
	_, one := snapshotBytes(t, peer)
	path, _ := controlServer(t, ControlSessions{})
	h = NewEngineHandler(path, "missing")
	if err := h.SyncState(context.Background(), &edgev1.SyncState{Snapshot: one}); err == nil {
		t.Fatal("missing local peer accepted")
	}
}

func TestControlCallResponseValidation(t *testing.T) {
	server := func(mutate func(reqID string) map[string]any) string {
		path := filepath.Join(t.TempDir(), "control.sock")
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
					var req struct {
						RequestID string `json:"request_id"`
					}
					_ = json.Unmarshal(buf, &req)
					resp, _ := json.Marshal(mutate(req.RequestID))
					_ = binary.Write(c, binary.BigEndian, uint32(len(resp)))
					_, _ = c.Write(resp)
				}(c)
			}
		}()
		return path
	}
	ctx := context.Background()
	c := ControlClient{SocketPath: server(func(id string) map[string]any {
		return map[string]any{"request_id": "wrong", "status": "ok", "result": map[string]any{}}
	})}
	if err := c.Call(ctx, "GetSessions", nil, nil); err == nil {
		t.Fatal("mismatched request id accepted")
	}
	c.SocketPath = server(func(id string) map[string]any {
		return map[string]any{"request_id": id, "status": "error", "error": map[string]any{"code": "bad", "message": "broken"}}
	})
	if err := c.Call(ctx, "GetSessions", nil, nil); err == nil || !strings.Contains(err.Error(), "bad") {
		t.Fatalf("error envelope not surfaced: %v", err)
	}
	c.SocketPath = server(func(id string) map[string]any {
		return map[string]any{"request_id": id, "status": "ok", "result": "not-an-object"}
	})
	var sessions ControlSessions
	if err := c.Call(ctx, "GetSessions", nil, &sessions); err == nil {
		t.Fatal("malformed result accepted")
	}
}
