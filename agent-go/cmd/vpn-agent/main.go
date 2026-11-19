// vpn-agent fetches engine metrics once and prints them — the smallest
// observable slice of the node agent for the telemetry milestone.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"os"
	"time"

	agent "github.com/RondineleG/HiKablue/agent-go"
)

func main() {
	os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))
}

// run parses args, fetches metrics once and prints them as JSON. It returns
// the process exit code so tests can exercise every path without spawning
// the binary.
func run(args []string, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("vpn-agent", flag.ContinueOnError)
	fs.SetOutput(stderr)
	sock := fs.String("metrics-sock", "/run/vpn/engine.sock", "engine metrics Unix socket")
	timeout := fs.Duration("timeout", 2*time.Second, "collection deadline")
	if err := fs.Parse(args); err != nil {
		return 2
	}

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
