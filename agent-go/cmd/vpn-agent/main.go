// vpn-agent is the node agent. Without -serve it preserves the milestone-1
// one-shot metrics behavior; with -serve it owns the gRPC NodeChannel and
// applies commands to the Rust engine control socket.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"os"
	"os/signal"
	"syscall"
	"time"

	agent "github.com/RondineleG/HiKablue/agent-go"
	"github.com/RondineleG/HiKablue/agent-go/pkg/orchestrator"
)

func main() {
	os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))
}

func run(args []string, stdout, stderr io.Writer) int {
	return runWithContext(context.Background(), args, stdout, stderr)
}

func runWithContext(parent context.Context, args []string, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("vpn-agent", flag.ContinueOnError)
	fs.SetOutput(stderr)
	sock := fs.String("metrics-sock", "/run/vpn/engine.sock", "engine metrics Unix socket")
	controlSock := fs.String("control-sock", "/run/vpn/control.sock", "engine control Unix socket")
	timeout := fs.Duration("timeout", 2*time.Second, "collection deadline")
	serve := fs.Bool("serve", false, "run the orchestration channel instead of one metrics fetch")
	address := fs.String("orchestrator", "127.0.0.1:7443", "control plane gRPC host:port")
	caPath := fs.String("ca", "", "lab CA certificate PEM")
	certPath := fs.String("cert", "", "node client certificate PEM")
	keyPath := fs.String("key", "", "node client private key PEM")
	nodeID := fs.String("node-id", "", "SPIFFE node id embedded in the node certificate")
	agentVersion := fs.String("agent-version", "dev", "agent version reported on NodeRegister")
	if err := fs.Parse(args); err != nil {
		if err == flag.ErrHelp {
			return 0
		}
		return 2
	}

	if !*serve {
		ctx, cancel := context.WithTimeout(context.Background(), *timeout)
		defer cancel()
		m, err := agent.FetchMetrics(ctx, *sock)
		if err != nil {
			fmt.Fprintln(stderr, "vpn-agent:", err)
			return 1
		}
		out, _ := json.Marshal(m)
		fmt.Fprintln(stdout, string(out))
		return 0
	}

	if *nodeID == "" || *caPath == "" || *certPath == "" || *keyPath == "" {
		fmt.Fprintln(stderr, "vpn-agent: -serve requires -node-id, -ca, -cert and -key")
		return 2
	}

	ctx, stop := signal.NotifyContext(parent, os.Interrupt, syscall.SIGTERM)
	defer stop()
	control := agent.ControlClient{SocketPath: *controlSock, Timeout: 2 * time.Second}
	sessions, err := control.Sessions(ctx)
	if err != nil {
		fmt.Fprintln(stderr, "vpn-agent: read engine backend instance:", err)
		return 1
	}
	handler := agent.NewEngineHandler(*controlSock, *sock)
	go handler.RunExpiryLoop(ctx, 500*time.Millisecond)

	cfg := orchestrator.Config{
		Address:           *address,
		CaPath:            *caPath,
		CertPath:          *certPath,
		KeyPath:           *keyPath,
		NodeId:            *nodeID,
		BackendInstanceId: sessions.BackendInstanceID,
		AgentVersion:      *agentVersion,
		HeartbeatInterval: 2 * time.Second,
	}
	cfg.ReplayState = orchestrator.NewReplayState(cfg.NodeId, cfg.BackendInstanceId)

	backoff := 500 * time.Millisecond
	for ctx.Err() == nil {
		conn, err := orchestrator.Dial(ctx, cfg)
		if err != nil {
			fmt.Fprintln(stderr, "vpn-agent:", err)
		} else {
			err = orchestrator.NewChannel(cfg, handler).Run(ctx, conn)
			_ = conn.Close()
			if ctx.Err() != nil {
				return 0
			}
			fmt.Fprintln(stderr, "vpn-agent: NodeChannel ended:", err)
		}
		select {
		case <-ctx.Done():
			return 0
		case <-time.After(backoff):
		}
		if backoff < 10*time.Second {
			backoff *= 2
			if backoff > 10*time.Second {
				backoff = 10 * time.Second
			}
		}
	}
	return 0
}
