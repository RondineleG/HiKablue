package agent

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"net"
	"sync"
	"time"

	edgev1 "github.com/RondineleG/HiKablue/agent-go/gen/edge/v1"
	"google.golang.org/protobuf/proto"
)

// EngineHandler applies orchestrator commands to the Rust engine control
// socket. Command application is deliberately synchronous: the channel ACK
// is meaningful only after StagePeer+Activate or RevokePeer has completed.
type EngineHandler struct {
	Control     ControlClient
	MetricsSock string

	mu      sync.Mutex
	expires map[uint32]time.Time
}

func NewEngineHandler(controlSock, metricsSock string) *EngineHandler {
	return &EngineHandler{
		Control:     ControlClient{SocketPath: controlSock, Timeout: 2 * time.Second},
		MetricsSock: metricsSock,
		expires:     make(map[uint32]time.Time),
	}
}

func validKeyMaterial(k *edgev1.PeerKeyMaterial) error {
	if k == nil {
		return fmt.Errorf("missing key_material")
	}
	if len(k.EgressKey) != 32 || len(k.IngressKey) != 32 {
		return fmt.Errorf("key material must contain two 32-byte direction keys")
	}
	if bytes.Equal(k.EgressKey, k.IngressKey) {
		return fmt.Errorf("egress_key and ingress_key must differ")
	}
	if k.SenderIndex == 0 || k.PeerIndex == 0 || k.SenderIndex == k.PeerIndex {
		return fmt.Errorf("sender_index and peer_index must be nonzero and distinct")
	}
	return nil
}

func validatePeer(p *edgev1.Peer) error {
	if p == nil {
		return fmt.Errorf("missing peer")
	}
	if p.SessionId == "" || p.LabSessionId == 0 {
		return fmt.Errorf("peer requires session_id and nonzero lab_session_id")
	}
	if ip := net.ParseIP(p.VirtualIp); ip == nil || ip.To4() == nil {
		return fmt.Errorf("peer virtual_ip %q is not IPv4", p.VirtualIp)
	}
	if p.ExpiresAt == nil || p.ExpiresAt.CheckValid() != nil {
		return fmt.Errorf("peer expires_at is missing or invalid")
	}
	return nil
}

// ProvisionPeer stages and activates the initial epoch. The Rust control
// boundary makes each op idempotent; together they are the atomic transition
// intended by one ProvisionPeer command for this milestone.
func (h *EngineHandler) ProvisionPeer(ctx context.Context, p *edgev1.ProvisionPeer) error {
	if p == nil {
		return fmt.Errorf("missing provision payload")
	}
	if err := validatePeer(p.Peer); err != nil {
		return err
	}
	if err := validKeyMaterial(p.KeyMaterial); err != nil {
		return err
	}
	km := p.KeyMaterial
	if km.KeyEpoch != p.Peer.KeyEpoch {
		return fmt.Errorf("key_material epoch %d does not match peer epoch %d", km.KeyEpoch, p.Peer.KeyEpoch)
	}
	lab := p.Peer.LabSessionId
	stage := map[string]any{
		"lab_session_id": lab,
		"virtual_ip":     p.Peer.VirtualIp,
		"key_epoch":      km.KeyEpoch,
		"sender_index":   km.SenderIndex,
		"peer_index":     km.PeerIndex,
		"egress_key":     hex.EncodeToString(km.EgressKey),
		"ingress_key":    hex.EncodeToString(km.IngressKey),
	}
	if err := h.Control.Call(ctx, "StagePeer", stage, nil); err != nil {
		return fmt.Errorf("stage peer %d: %w", lab, err)
	}
	activate := map[string]any{"lab_session_id": lab, "key_epoch": km.KeyEpoch}
	if err := h.Control.Call(ctx, "Activate", activate, nil); err != nil {
		return fmt.Errorf("activate peer %d epoch %d: %w", lab, km.KeyEpoch, err)
	}
	h.mu.Lock()
	h.expires[lab] = p.Peer.ExpiresAt.AsTime()
	h.mu.Unlock()
	return nil
}

func (h *EngineHandler) RevokePeer(ctx context.Context, p *edgev1.RevokePeer) error {
	if p == nil || p.LabSessionId == 0 {
		return fmt.Errorf("revoke requires nonzero lab_session_id")
	}
	payload := map[string]any{"lab_session_id": p.LabSessionId}
	if err := h.Control.Call(ctx, "RevokePeer", payload, nil); err != nil {
		return fmt.Errorf("revoke peer %d: %w", p.LabSessionId, err)
	}
	h.mu.Lock()
	delete(h.expires, p.LabSessionId)
	h.mu.Unlock()
	return nil
}

// SyncState reconciles drift without ever reconstructing keys from a
// snapshot. Snapshots carry key references only; peers missing locally are a
// backend/provisioning error for the control plane, not something the agent
// can invent key material for. Extra local peers are revoked, subject to the
// specification's deletion guard.
func (h *EngineHandler) SyncState(ctx context.Context, p *edgev1.SyncState) error {
	if p == nil || p.Snapshot == nil {
		return fmt.Errorf("missing snapshot")
	}
	snap := p.Snapshot
	if int(snap.SerializedLength) != len(snap.PeerList) {
		return fmt.Errorf("snapshot serialized_length %d != %d", snap.SerializedLength, len(snap.PeerList))
	}
	digest := sha256.Sum256(snap.PeerList)
	if !bytes.Equal(digest[:], snap.Sha256Digest) {
		return fmt.Errorf("snapshot sha256 digest mismatch")
	}
	var list edgev1.PeerList
	if err := proto.Unmarshal(snap.PeerList, &list); err != nil {
		return fmt.Errorf("decode snapshot peer_list: %w", err)
	}
	if int(snap.DeclaredPeerCount) != len(list.Peers) {
		return fmt.Errorf("snapshot declared_peer_count %d != decoded %d", snap.DeclaredPeerCount, len(list.Peers))
	}
	desired := make(map[uint32]bool, len(list.Peers))
	for _, peer := range list.Peers {
		if err := validatePeer(peer); err != nil {
			return fmt.Errorf("invalid snapshot peer: %w", err)
		}
		if desired[peer.LabSessionId] {
			return fmt.Errorf("snapshot repeats lab_session_id %d", peer.LabSessionId)
		}
		desired[peer.LabSessionId] = true
	}

	current, err := h.Control.Sessions(ctx)
	if err != nil {
		return err
	}
	local := make(map[uint32]bool, len(current.Peers))
	for _, peer := range current.Peers {
		if peer.LabSessionID != 0 {
			local[peer.LabSessionID] = true
		}
	}
	deletions := 0
	for lab := range local {
		if !desired[lab] {
			deletions++
		}
	}
	guarded := len(local) > 0 && (deletions*5 > len(local) || (len(desired) == 0 && deletions > 0))
	if guarded && !p.Force {
		return fmt.Errorf("snapshot deletion guard: %d of %d local peers would be removed", deletions, len(local))
	}
	for _, peer := range list.Peers {
		lab := peer.LabSessionId
		if !local[lab] {
			return fmt.Errorf("snapshot peer %d is missing locally; key material cannot be reconstructed", lab)
		}
		// Rehydrate the local expiration deadline after an agent restart.
		// The engine owns enforcement; this map only drives the agent's timer.
		h.mu.Lock()
		h.expires[lab] = peer.ExpiresAt.AsTime()
		h.mu.Unlock()
	}
	for lab := range local {
		if !desired[lab] {
			if err := h.Control.Call(ctx, "RevokePeer", map[string]any{"lab_session_id": lab}, nil); err != nil {
				return fmt.Errorf("remove extra peer %d: %w", lab, err)
			}
			h.mu.Lock()
			delete(h.expires, lab)
			h.mu.Unlock()
		}
	}
	return nil
}

func (h *EngineHandler) EngineHealthy() bool {
	ctx, cancel := context.WithTimeout(context.Background(), 300*time.Millisecond)
	defer cancel()
	_, err := FetchMetrics(ctx, h.MetricsSock)
	return err == nil
}

// ExpireDuePeers enforces expiration locally while the node is offline from
// the control plane. Control-plane state convergence remains asynchronous:
// expiration moves the control-plane session to revoking, while this loop
// guarantees the data plane stops passing traffic at the deadline.
func (h *EngineHandler) ExpireDuePeers(ctx context.Context) {
	h.mu.Lock()
	due := make([]uint32, 0)
	now := time.Now()
	for lab, expires := range h.expires {
		if !now.Before(expires) {
			due = append(due, lab)
		}
	}
	h.mu.Unlock()
	for _, lab := range due {
		if err := h.Control.Call(ctx, "RevokePeer", map[string]any{"lab_session_id": lab}, nil); err == nil {
			h.mu.Lock()
			delete(h.expires, lab)
			h.mu.Unlock()
		}
	}
}

func (h *EngineHandler) RunExpiryLoop(ctx context.Context, interval time.Duration) {
	if interval <= 0 {
		interval = time.Second
	}
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			h.ExpireDuePeers(ctx)
		}
	}
}
