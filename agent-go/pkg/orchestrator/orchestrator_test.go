package orchestrator

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"errors"
	"math/big"
	"net/url"
	"os"
	"path/filepath"
	"testing"
	"time"

	edgev1 "github.com/RondineleG/HiKablue/agent-go/gen/edge/v1"
	"github.com/golang/protobuf/ptypes"
	"google.golang.org/protobuf/types/known/timestamppb"
)

// --- mTLS tests: a real TLS handshake proves the client credential loads
// and validates against the lab trust model (dedicated lab CA, clientAuth
// only) ---

type testCA struct {
	cert    *x509.Certificate
	key     *ecdsa.PrivateKey
	certPEM []byte
}

func newTestCA(t *testing.T) *testCA {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	tmpl := &x509.Certificate{
		SerialNumber:          big.NewInt(1),
		Subject:               pkix.Name{CommonName: "HiKablue Lab CA"},
		NotBefore:             time.Now().Add(-time.Hour),
		NotAfter:              time.Now().Add(time.Hour),
		IsCA:                  true,
		KeyUsage:              x509.KeyUsageCertSign | x509.KeyUsageCRLSign,
		BasicConstraintsValid: true,
	}
	der, err := x509.CreateCertificate(rand.Reader, tmpl, tmpl,
		&key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	cert, err := x509.ParseCertificate(der)
	if err != nil {
		t.Fatal(err)
	}
	return &testCA{cert: cert, key: key,
		certPEM: pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})}
}

func (ca *testCA) issue(t *testing.T, cn string, uris []string,
	eku x509.ExtKeyUsage) (certPEM, keyPEM []byte) {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	tmpl := &x509.Certificate{
		SerialNumber: big.NewInt(time.Now().UnixNano()),
		Subject:      pkix.Name{CommonName: cn},
		NotBefore:    time.Now().Add(-time.Hour),
		NotAfter:     time.Now().Add(time.Hour),
		KeyUsage:     x509.KeyUsageDigitalSignature,
		ExtKeyUsage:  []x509.ExtKeyUsage{eku},
		DNSNames:     []string{cn},
	}
	for _, u := range uris {
		uri := mustURI(t, u)
		tmpl.URIs = append(tmpl.URIs, uri)
	}
	der, err := x509.CreateCertificate(rand.Reader, tmpl, ca.cert,
		&key.PublicKey, ca.key)
	if err != nil {
		t.Fatal(err)
	}
	keyDER, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		t.Fatal(err)
	}
	return pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}),
		pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: keyDER})
}

func mustURI(t *testing.T, raw string) *url.URL {
	t.Helper()
	u, err := url.Parse(raw)
	if err != nil {
		t.Fatal(err)
	}
	return u
}

func writePair(t *testing.T, dir, name string, certPEM, keyPEM []byte) (string, string) {
	t.Helper()
	certPath := filepath.Join(dir, name+".crt")
	keyPath := filepath.Join(dir, name+".key")
	if err := os.WriteFile(certPath, certPEM, 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(keyPath, keyPEM, 0o600); err != nil {
		t.Fatal(err)
	}
	return certPath, keyPath
}

// handshake runs a real TLS handshake: the test listener demands and
// verifies a client cert against serverCA while the client uses the
// orchestrator's NewMTLSConfig against clientCA.
func handshake(t *testing.T, serverCA *testCA, serverCert, serverKey []byte,
	cfg *tls.Config) error {
	t.Helper()
	pool := x509.NewCertPool()
	if !pool.AppendCertsFromPEM(serverCA.certPEM) {
		t.Fatal("server CA pool")
	}
	pair, err := tls.X509KeyPair(serverCert, serverKey)
	if err != nil {
		t.Fatal(err)
	}
	ln, err := tls.Listen("tcp", "127.0.0.1:0", &tls.Config{
		MinVersion:   tls.VersionTLS12,
		Certificates: []tls.Certificate{pair},
		ClientAuth:   tls.RequireAndVerifyClientCert,
		ClientCAs:    pool,
	})
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()

	errCh := make(chan error, 1)
	go func() {
		conn, err := ln.Accept()
		if err != nil {
			errCh <- err
			return
		}
		defer conn.Close()
		errCh <- conn.(*tls.Conn).Handshake()
	}()

	cfg = cfg.Clone()
	cfg.ServerName = "localhost"
	conn, err := tls.Dial("tcp", ln.Addr().String(), cfg)
	if err != nil {
		return err
	}
	defer conn.Close()
	if err := conn.Handshake(); err != nil {
		return err
	}
	return <-errCh
}

func TestMTLSHandshakeAcceptsLabIssuedClientCert(t *testing.T) {
	dir := t.TempDir()
	serverCA := newTestCA(t)
	clientCA := newTestCA(t)

	serverCert, serverKey := serverCA.issue(t, "localhost", nil,
		x509.ExtKeyUsageServerAuth)
	clientCert, clientKey := clientCA.issue(t, "edge-1",
		[]string{"spiffe://lab.hikablue/node/edge-1"},
		x509.ExtKeyUsageClientAuth)

	caPath := filepath.Join(dir, "ca.crt")
	if err := os.WriteFile(caPath, serverCA.certPEM, 0o644); err != nil {
		t.Fatal(err)
	}
	certPath, keyPath := writePair(t, dir, "edge-1", clientCert, clientKey)

	cfg, err := NewMTLSConfig(caPath, certPath, keyPath)
	if err != nil {
		t.Fatal(err)
	}

	// The server trusts clientCA, the client trusts serverCA.
	if err := handshake(t, clientCA, serverCert, serverKey, cfg); err != nil {
		t.Fatalf("expected successful mTLS handshake, got %v", err)
	}
}

func TestMTLSHandshakeRejectsUntrustedClientCert(t *testing.T) {
	dir := t.TempDir()
	serverCA := newTestCA(t)
	trustedClientCA := newTestCA(t)
	rogueCA := newTestCA(t)

	serverCert, serverKey := serverCA.issue(t, "localhost", nil,
		x509.ExtKeyUsageServerAuth)
	rogueCert, rogueKey := rogueCA.issue(t, "rogue",
		[]string{"spiffe://lab.hikablue/node/rogue"},
		x509.ExtKeyUsageClientAuth)

	caPath := filepath.Join(dir, "ca.crt")
	if err := os.WriteFile(caPath, serverCA.certPEM, 0o644); err != nil {
		t.Fatal(err)
	}
	certPath, keyPath := writePair(t, dir, "rogue", rogueCert, rogueKey)

	cfg, err := NewMTLSConfig(caPath, certPath, keyPath)
	if err != nil {
		t.Fatal(err)
	}
	// Server only trusts the genuine client CA; the rogue cert is refused.
	if err := handshake(t, trustedClientCA, serverCert, serverKey, cfg); err == nil {
		t.Fatal("expected handshake failure with untrusted client cert")
	}
}

// --- Fencing / CommandAck tests ---

type stubHandler struct {
	provisioned int
	revoked     int
	synced      int
	err         error
	healthy     bool
}

func (s *stubHandler) ProvisionPeer(_ context.Context,
	_ *edgev1.ProvisionPeer) error {
	s.provisioned++
	return s.err
}

func (s *stubHandler) RevokePeer(_ context.Context,
	_ *edgev1.RevokePeer) error {
	s.revoked++
	return s.err
}

func (s *stubHandler) SyncState(_ context.Context,
	_ *edgev1.SyncState) error {
	s.synced++
	return s.err
}

func (s *stubHandler) EngineHealthy() bool { return s.healthy }

func newTestChannel(h Handler) *Channel {
	return NewChannel(Config{
		NodeId:            "edge-1",
		BackendInstanceId: "backend-1",
	}, h)
}

func command(epoch, backend string, revision uint64,
	deadline time.Time) *edgev1.ControlCommand {
	cmd := &edgev1.ControlCommand{
		CommandId:         "cmd-1",
		NodeId:            "edge-1",
		StreamEpoch:       epoch,
		BackendInstanceId: backend,
		Revision:          revision,
		Payload: &edgev1.ControlCommand_ProvisionPeer{
			ProvisionPeer: &edgev1.ProvisionPeer{},
		},
	}
	if !deadline.IsZero() {
		ts, err := ptypes.TimestampProto(deadline)
		if err != nil {
			panic(err)
		}
		cmd.Deadline = ts
	}
	return cmd
}

func TestExpiredDeadlineRejectsWithoutStateChange(t *testing.T) {
	h := &stubHandler{healthy: true}
	c := newTestChannel(h)
	c.streamEpoch.Store("1000")

	cmd := command("1000", "backend-1", 5, time.Now().Add(-time.Minute))
	ack, dispatch := c.evaluate(cmd, time.Now())

	if dispatch {
		t.Fatal("expired command must not be dispatched")
	}
	if ack.Outcome != edgev1.AckOutcome_ACK_OUTCOME_REJECTED ||
		ack.ErrorCode != edgev1.AckErrorCode_ACK_ERROR_DEADLINE_EXCEEDED {
		t.Fatalf("expected ACK_ERROR_DEADLINE_EXCEEDED, got %v / %v",
			ack.Outcome, ack.ErrorCode)
	}
	if h.provisioned != 0 {
		t.Fatal("handler must not run for an expired command")
	}
}

func TestStaleStreamEpochIsFenced(t *testing.T) {
	h := &stubHandler{}
	c := newTestChannel(h)
	c.streamEpoch.Store("2000")

	cmd := command("1000", "backend-1", 5, time.Now().Add(time.Minute))
	ack, dispatch := c.evaluate(cmd, time.Now())
	if dispatch || ack.ErrorCode != edgev1.AckErrorCode_ACK_ERROR_FENCED_STREAM {
		t.Fatalf("expected FENCED_STREAM, dispatch=%v ack=%v", dispatch, ack)
	}
}

func TestRevisionRegressionIsRejected(t *testing.T) {
	h := &stubHandler{}
	c := newTestChannel(h)
	c.streamEpoch.Store("1000")
	c.appliedRevision = 5

	cmd := command("1000", "backend-1", 5, time.Now().Add(time.Minute))
	ack, dispatch := c.evaluate(cmd, time.Now())
	if dispatch || ack.ErrorCode != edgev1.AckErrorCode_ACK_ERROR_REVISION_REGRESSION {
		t.Fatalf("expected REVISION_REGRESSION, dispatch=%v ack=%v", dispatch, ack)
	}
}

func TestValidCommandDispatchesAndAcksApplied(t *testing.T) {
	h := &stubHandler{}
	c := newTestChannel(h)
	c.streamEpoch.Store("1000")

	cmd := command("1000", "backend-1", 3, time.Now().Add(time.Minute))
	ack, dispatch := c.evaluate(cmd, time.Now())
	if !dispatch || ack.Outcome != edgev1.AckOutcome_ACK_OUTCOME_APPLIED {
		t.Fatalf("expected dispatch with APPLIED, dispatch=%v ack=%v", dispatch, ack)
	}
	if err := c.dispatch(context.Background(), cmd); err != nil {
		t.Fatal(err)
	}
	if h.provisioned != 1 {
		t.Fatal("handler should have been called once")
	}
}

func TestHandlerErrorProducesErrorAck(t *testing.T) {
	h := &stubHandler{err: errors.New("backend exploded")}
	c := newTestChannel(h)
	if err := c.dispatch(context.Background(),
		command("1", "b", 1, time.Time{})); err == nil {
		t.Fatal("expected handler error to propagate")
	}
}

func TestForeignBackendCommandIsFenced(t *testing.T) {
	h := &stubHandler{}
	c := newTestChannel(h)
	c.streamEpoch.Store("1000")

	cmd := command("1000", "other-backend", 5, time.Now().Add(time.Minute))
	ack, dispatch := c.evaluate(cmd, time.Now())
	if dispatch || ack.ErrorCode != edgev1.AckErrorCode_ACK_ERROR_FENCED_BACKEND {
		t.Fatalf("expected FENCED_BACKEND, dispatch=%v ack=%v", dispatch, ack)
	}
}

func TestNewerEpochIsAdopted(t *testing.T) {
	h := &stubHandler{}
	c := newTestChannel(h)
	c.streamEpoch.Store("1000")

	cmd := command("2000", "backend-1", 1, time.Now().Add(time.Minute))
	_, dispatch := c.evaluate(cmd, time.Now())
	if !dispatch {
		t.Fatal("newer epoch command should dispatch")
	}
	if got := c.currentEpoch(); got != "2000" {
		t.Fatalf("expected adopted epoch 2000, got %s", got)
	}
}

func TestReplayedAppliedCommandIsConfirmedWithoutRedispatch(t *testing.T) {
	h := &stubHandler{}
	c := newTestChannel(h)
	c.streamEpoch.Store("1000")

	cmd := command("1000", "backend-1", 3, time.Now().Add(time.Minute))
	if _, dispatch := c.evaluate(cmd, time.Now()); !dispatch {
		t.Fatal("first delivery must dispatch")
	}
	if err := c.dispatch(context.Background(), cmd); err != nil {
		t.Fatal(err)
	}
	// Run records the applied command only after a successful dispatch.
	c.appliedCommands.Store(cmd.CommandId, struct{}{})
	c.appliedRevision = cmd.Revision

	ack, dispatch := c.evaluate(cmd, time.Now())
	if dispatch {
		t.Fatal("replayed command must not be dispatched again")
	}
	if ack.Outcome != edgev1.AckOutcome_ACK_OUTCOME_APPLIED {
		t.Fatalf("replayed command must be confirmed APPLIED, got %v", ack.Outcome)
	}
	if h.provisioned != 1 {
		t.Fatalf("handler ran %d times, want 1", h.provisioned)
	}
}

func TestCommandWithoutDeadlineOrRevisionDispatches(t *testing.T) {
	h := &stubHandler{}
	c := newTestChannel(h)
	c.streamEpoch.Store("1000")
	if _, dispatch := c.evaluate(command("1000", "backend-1", 0, time.Time{}),
		time.Now()); !dispatch {
		t.Fatal("missing deadline/revision should still dispatch")
	}
}

func TestDispatchCoversRevokeSyncAndUnknown(t *testing.T) {
	h := &stubHandler{}
	c := newTestChannel(h)
	rev := &edgev1.ControlCommand{Payload: &edgev1.ControlCommand_RevokePeer{
		RevokePeer: &edgev1.RevokePeer{}}}
	if err := c.dispatch(context.Background(), rev); err != nil || h.revoked != 1 {
		t.Fatalf("revoke dispatch failed: %v", err)
	}
	sync := &edgev1.ControlCommand{Payload: &edgev1.ControlCommand_SyncState{
		SyncState: &edgev1.SyncState{}}}
	if err := c.dispatch(context.Background(), sync); err != nil || h.synced != 1 {
		t.Fatalf("sync dispatch failed: %v", err)
	}
	if err := c.dispatch(context.Background(),
		&edgev1.ControlCommand{}); err == nil {
		t.Fatal("unknown payload should error")
	}
}

func TestEpochHelpersFallBackToLexicalCompare(t *testing.T) {
	if !epochLess("abc", "abd") || epochLess("abd", "abc") {
		t.Fatal("lexical compare failed")
	}
	if !epochGreater("abd", "abc") || epochGreater("abc", "abd") {
		t.Fatal("lexical greater failed")
	}
}

func TestNewMTLSConfigErrors(t *testing.T) {
	if _, err := NewMTLSConfig("/no/such/ca", "x", "y"); err == nil {
		t.Fatal("missing CA file should error")
	}
	dir := t.TempDir()
	bad := dir + "/bad.pem"
	if err := os.WriteFile(bad, []byte("not pem"), 0o644); err != nil {
		t.Fatal(err)
	}
	if _, err := NewMTLSConfig(bad, "x", "y"); err == nil {
		t.Fatal("non-PEM CA should error")
	}
	ca := newTestCA(t)
	caPath := dir + "/ca.pem"
	if err := os.WriteFile(caPath, ca.certPEM, 0o644); err != nil {
		t.Fatal(err)
	}
	if _, err := NewMTLSConfig(caPath, bad, bad); err == nil {
		t.Fatal("bad key pair should error")
	}
}

func TestMalformedDeadlineDoesNotFence(t *testing.T) {
	h := &stubHandler{}
	c := newTestChannel(h)
	c.streamEpoch.Store("1000")
	cmd := command("1000", "backend-1", 1, time.Time{})
	cmd.Deadline = &timestamppb.Timestamp{Seconds: -1e15, Nanos: -2e9}
	if _, dispatch := c.evaluate(cmd, time.Now()); !dispatch {
		t.Fatal("unparseable deadline should not fence the command")
	}
}

func TestCurrentEpochDefaultsWhenUnset(t *testing.T) {
	c := newTestChannel(&stubHandler{})
	if got := c.currentEpoch(); got != "" {
		t.Fatalf("expected empty epoch, got %s", got)
	}
}
