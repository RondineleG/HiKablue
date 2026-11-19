// vpn-agent fetches engine metrics once and prints them — the smallest
// observable slice of the node agent for the telemetry milestone.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"time"

	agent "github.com/RondineleG/HiKablue/agent-go"
)

func main() {
	sock := flag.String("metrics-sock", "/run/vpn/engine.sock", "engine metrics Unix socket")
	timeout := flag.Duration("timeout", 2*time.Second, "collection deadline")
	flag.Parse()

	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	m, err := agent.FetchMetrics(ctx, *sock)
	if err != nil {
		fmt.Fprintln(os.Stderr, "vpn-agent:", err)
		os.Exit(1)
	}
	out, _ := json.Marshal(m)
	fmt.Println(string(out))
}
