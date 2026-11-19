# control-dotnet — vpn-control

`vpn-control` (C#/.NET) will own authentication, IPAM, session lifecycle and
auditing backed by PostgreSQL. Milestone 1 delivers only the telemetry
intake, deliberately in-memory:

- `POST /internal/heartbeats` — records a node-agent heartbeat
  (`{nodeId, engineHealthy, metrics?}`). Temporary HTTP transport: replaced
  by the gRPC `NodeChannel` stream in the control-plane milestone.
- `GET /v1/nodes` — lists nodes with `lastSeen` and `engineHealthy` tracked
  separately: a quiet node is stale, a beating node with a dead engine is
  degraded — different failures, different fields.
- `VpnControl.Tests` — xunit tests for the registry, including the
  missing-heartbeat-vs-engine-failure exercise.

Run: `dotnet run --project VpnControl` · Test: `dotnet test`
