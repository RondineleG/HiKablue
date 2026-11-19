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

**Security:** the heartbeat endpoint is **unauthenticated and lab-only** —
any local process can set any node's state. It must never face a network:
the service checks its URLs at startup and **refuses to start on a
non-loopback bind** (wildcard, LAN, `0.0.0.0`), whatever the configuration
source (`urls`, `ASPNETCORE_URLS`, appsettings). Real identity arrives with
the mTLS gRPC `NodeChannel` in the control-plane milestone.

Run: `dotnet run --project VpnControl` (binds `http://127.0.0.1:5080` by
default; `urls` config or `ASPNETCORE_URLS` may override but must stay
loopback) · Test: `dotnet test
VpnControl.Tests`
