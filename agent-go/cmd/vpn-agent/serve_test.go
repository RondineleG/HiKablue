package main

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/binary"
	"encoding/json"
	"encoding/pem"
	"io"
	"math/big"
	"net"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func writeTestPKI(t *testing.T, dir string) (ca, cert, key string) {
	t.Helper()
	caKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	caTmpl := &x509.Certificate{
		SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: "test ca"},
		NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour),
		IsCA: true, KeyUsage: x509.KeyUsageCertSign, BasicConstraintsValid: true,
	}
	caDER, err := x509.CreateCertificate(rand.Reader, caTmpl, caTmpl, &caKey.PublicKey, caKey)
	if err != nil {
		t.Fatal(err)
	}
	caCert, err := x509.ParseCertificate(caDER)
	if err != nil {
		t.Fatal(err)
	}
	nodeKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	nodeTmpl := &x509.Certificate{
		SerialNumber: big.NewInt(2), Subject: pkix.Name{CommonName: "exit-node"},
		NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour),
		KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageClientAuth},
	}
	nodeDER, err := x509.CreateCertificate(rand.Reader, nodeTmpl, caCert, &nodeKey.PublicKey, caKey)
	if err != nil {
		t.Fatal(err)
	}
	keyDER, err := x509.MarshalECPrivateKey(nodeKey)
	if err != nil {
		t.Fatal(err)
	}
	write := func(name string, block *pem.Block, mode os.FileMode) string {
		path := filepath.Join(dir, name)
		if err := os.WriteFile(path, pem.EncodeToMemory(block), mode); err != nil {
			t.Fatal(err)
		}
		return path
	}
	ca = write("ca.crt", &pem.Block{Type: "CERTIFICATE", Bytes: caDER}, 0o644)
	cert = write("node.crt", &pem.Block{Type: "CERTIFICATE", Bytes: nodeDER}, 0o644)
	key = write("node.key", &pem.Block{Type: "EC PRIVATE KEY", Bytes: keyDER}, 0o600)
	return ca, cert, key
}

func sessionsSocket(t *testing.T) string {
	t.Helper()
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
				resp, _ := json.Marshal(map[string]any{
					"request_id": req.RequestID, "version": 1, "status": "ok",
					"result": map[string]any{"backend_instance_id": "backend-test", "peers": []any{}},
				})
				_ = binary.Write(c, binary.BigEndian, uint32(len(resp)))
				_, _ = c.Write(resp)
			}(c)
		}
	}()
	return path
}

func TestServeRequiresMTLSArguments(t *testing.T) {
	var out, errout bytes.Buffer
	if code := run([]string{"-serve"}, &out, &errout); code != 2 {
		t.Fatalf("exit %d", code)
	}
}

func TestServeStartsChannelAndStopsWithContext(t *testing.T) {
	dir := t.TempDir()
	ca, cert, key := writeTestPKI(t, dir)
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan int, 1)
	go func() {
		var out, errout bytes.Buffer
		done <- runWithContext(ctx, []string{
			"-serve", "-orchestrator", "127.0.0.1:1",
			"-ca", ca, "-cert", cert, "-key", key, "-node-id", "exit-node",
			"-control-sock", sessionsSocket(t),
			"-metrics-sock", filepath.Join(dir, "metrics.sock"),
			"-state-dir", filepath.Join(dir, "agent-state"),
		}, &out, &errout)
	}()
	time.Sleep(650 * time.Millisecond)
	cancel()
	select {
	case code := <-done:
		if code != 0 {
			t.Fatalf("exit %d", code)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("serve did not stop with context")
	}
}

func TestServeKeepsExpiryStateOutsideReadOnlySocketDir(t *testing.T) {
	dir := t.TempDir()
	ca, cert, key := writeTestPKI(t, dir)
	socketDir := filepath.Join(dir, "engine")
	if err := os.Mkdir(socketDir, 0o755); err != nil {
		t.Fatal(err)
	}
	sock := filepath.Join(socketDir, "control.sock")
	l, err := net.Listen("unix", sock)
	if err != nil {
		t.Fatal(err)
	}
	defer l.Close()
	go func() {
		for {
			c, err := l.Accept()
			if err != nil {
				return
			}
			_ = c.Close()
		}
	}()
	if err := os.Chmod(socketDir, 0o555); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.Chmod(socketDir, 0o755) })

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan int, 1)
	var out, errout bytes.Buffer
	go func() {
		done <- runWithContext(ctx, []string{
			"-serve", "-orchestrator", "127.0.0.1:1",
			"-ca", ca, "-cert", cert, "-key", key, "-node-id", "exit-node",
			"-control-sock", sock,
			"-metrics-sock", filepath.Join(dir, "metrics.sock"),
			"-state-dir", filepath.Join(dir, "agent-state"),
		}, &out, &errout)
	}()
	time.Sleep(200 * time.Millisecond)
	cancel()
	select {
	case code := <-done:
		if code != 1 || !bytes.Contains(errout.Bytes(), []byte("read engine backend instance")) {
			t.Fatalf("exit %d stderr %q", code, errout.String())
		}
	case <-time.After(3 * time.Second):
		t.Fatal("serve did not stop with context")
	}
}

func TestServeReportsMissingControlSocket(t *testing.T) {
	dir := t.TempDir()
	ca, cert, key := writeTestPKI(t, dir)
	var out, errout bytes.Buffer
	code := runWithContext(context.Background(), []string{
		"-serve", "-orchestrator", "127.0.0.1:1",
		"-ca", ca, "-cert", cert, "-key", key, "-node-id", "exit-node",
		"-control-sock", filepath.Join(dir, "missing.sock"),
		"-state-dir", filepath.Join(dir, "agent-state"),
	}, &out, &errout)
	if code != 1 {
		t.Fatalf("exit %d stderr %q", code, errout.String())
	}
}

func TestServeRejectsSharedExpiryStateDirectory(t *testing.T) {
	dir := t.TempDir()
	ca, cert, key := writeTestPKI(t, dir)
	shared := filepath.Join(dir, "shared")
	if err := os.Mkdir(shared, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(shared, 0o755); err != nil {
		t.Fatal(err)
	}
	var out, errout bytes.Buffer
	code := runWithContext(context.Background(), []string{
		"-serve", "-orchestrator", "127.0.0.1:1",
		"-ca", ca, "-cert", cert, "-key", key, "-node-id", "exit-node",
		"-control-sock", filepath.Join(dir, "missing.sock"),
		"-state-dir", shared,
	}, &out, &errout)
	if code != 1 || !bytes.Contains(errout.Bytes(), []byte("private")) {
		t.Fatalf("exit %d stderr %q", code, errout.String())
	}
}

func TestReconnectDelayResetsOnlyAfterStableStream(t *testing.T) {
	now := time.Now()
	delay, next := reconnectDelays(8*time.Second, time.Time{}, now)
	if delay != 8*time.Second || next != 10*time.Second {
		t.Fatalf("unstable connection delay = %s next = %s", delay, next)
	}
	delay, next = reconnectDelays(500*time.Millisecond, now.Add(-time.Second), now)
	if delay != 500*time.Millisecond || next != time.Second {
		t.Fatalf("short-lived connection delay = %s next = %s", delay, next)
	}
	delay, next = reconnectDelays(10*time.Second, now.Add(-reconnectStableAfter), now)
	if delay != reconnectInitialDelay || next != reconnectInitialDelay {
		t.Fatalf("stable connection delay = %s next = %s", delay, next)
	}
}
