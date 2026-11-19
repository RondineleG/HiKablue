# agent-go — vpn-agent

`vpn-agent` (Go) is the node agent: it polls the engine's telemetry socket
and will apply peer commands from the control plane in later milestones.

Milestone 1 slice (telemetry):

- `FetchMetrics(ctx, socketPath)` — dials `/run/vpn/engine.sock`, sends a
  `{"method":"GetMetrics"}` frame (u32-BE length + JSON), decodes the
  response. The context deadline is applied to the socket, so a silent peer
  cannot stall the collection loop (`metrics_test.go` reproduces that case).
- `cmd/vpn-agent` — one-shot CLI: `vpn-agent --metrics-sock PATH [--timeout 2s]`
  prints the decoded counters as JSON.

Run tests: `go test ./...` · Build: `go build ./cmd/vpn-agent`
