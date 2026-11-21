// Package orchestrator implements the edge agent side of the NodeChannel
// contract: mTLS client against the lab CA, the bidirectional stream loop,
// and the fencing rules from vpn-debian-specification.md §5 / ADR-0001.
package orchestrator

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"fmt"
	"os"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/backoff"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/keepalive"
)

// Config carries everything the agent needs to dial the control plane.
type Config struct {
	// Address is host:port of the control plane gRPC endpoint.
	Address string
	// CaPath, CertPath and KeyPath point at the lab PKI material issued by
	// tests/lab/gen-lab-pki.sh (lab trust anchor, never a public CA).
	CaPath   string
	CertPath string
	KeyPath  string
	// NodeId is the SPIFFE node id embedded in the certificate SAN.
	NodeId string
	// BackendInstanceId identifies the running backend. The caller provides
	// a new ID when the backend restarts or loses its applied state.
	BackendInstanceId string
	// AgentVersion is reported on NodeRegister.
	AgentVersion string
	// HeartbeatInterval overrides the 10s default heartbeat cadence
	// (mainly for tests).
	HeartbeatInterval time.Duration
	// ReplayState optionally preserves in-memory command history across
	// replacement Channels for the same node and backend instance.
	ReplayState *ReplayState
}

// NewMTLSConfig builds the TLS config for the lab PKI: the lab CA as the
// only root and the node certificate as client identity. Returned as a
// *tls.Config so tests can also drive a plain TLS handshake against it.
func NewMTLSConfig(caPath, certPath, keyPath string) (*tls.Config, error) {
	caPEM, err := os.ReadFile(caPath)
	if err != nil {
		return nil, fmt.Errorf("orchestrator: read CA %s: %w", caPath, err)
	}
	pool := x509.NewCertPool()
	if !pool.AppendCertsFromPEM(caPEM) {
		return nil, fmt.Errorf("orchestrator: no PEM certificates in %s", caPath)
	}
	pair, err := tls.LoadX509KeyPair(certPath, keyPath)
	if err != nil {
		return nil, fmt.Errorf("orchestrator: load node key pair: %w", err)
	}
	return &tls.Config{
		MinVersion:   tls.VersionTLS12,
		RootCAs:      pool,
		Certificates: []tls.Certificate{pair},
	}, nil
}

// Dial opens the control plane connection with lab mTLS, keepalive and
// reconnect backoff per the M3 spec (Time 30s / Timeout 10s).
func Dial(ctx context.Context, cfg Config) (*grpc.ClientConn, error) {
	tlsCfg, err := NewMTLSConfig(cfg.CaPath, cfg.CertPath, cfg.KeyPath)
	if err != nil {
		return nil, err
	}
	conn, err := grpc.DialContext(ctx, cfg.Address,
		grpc.WithTransportCredentials(credentials.NewTLS(tlsCfg)),
		grpc.WithKeepaliveParams(keepalive.ClientParameters{
			Time:    30 * time.Second,
			Timeout: 10 * time.Second,
		}),
		grpc.WithConnectParams(grpc.ConnectParams{
			Backoff: backoff.Config{
				BaseDelay:  500 * time.Millisecond,
				Multiplier: 1.8,
				Jitter:     0.2,
				MaxDelay:   30 * time.Second,
			},
			MinConnectTimeout: 20 * time.Second,
		}),
	)
	if err != nil {
		return nil, fmt.Errorf("orchestrator: dial %s: %w", cfg.Address, err)
	}
	return conn, nil
}
