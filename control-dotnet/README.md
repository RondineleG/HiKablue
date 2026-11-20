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

Layout (single project, separation by folders):

```
VpnControl/
  Program.cs                        bootstrap + loopback guard
  Endpoints/Base/                   BaseEndPoint<TEntity> (UseCase → HTTP mapping)
  Endpoints/Internal/               lab-only intake (heartbeat)
  Endpoints/V1/Nodes/               public node list
  UseCases/Nodes/                   IUseCase<TRequest, TResponse> implementations
  Services/                         NodeService over NodeRegistry (in-memory)
  Entities/                         Node (+ Base/BaseEntity)
  Interfaces/                       IEndpointAsync, IUseCase, INodeService
  Requests/                         RequestResult / EResultStatus (OnForkHub result pattern)
  Extensions/                       MapRegisteredEndpointsAsync + AddVpnControl (convention DI)
  Models/                           Heartbeat, NodeView, ApiResponse records
  Security/                         LoopbackGuard
```

## Limitações conhecidas

Estas limitações são aceitas no Marco 1 (telemetria, laboratório isolado) e
serão endereçadas nos marcos de control plane:

- O endpoint `/internal/heartbeats` não tem autenticação — qualquer cliente
  que alcance o listener loopback pode registrar batimentos.
- O dicionário de métricas é aceito sem limite de número de chaves nem
  tamanho de chave/valor — payloads grandes ou com muitas chaves não são
  rejeitados.
- O registro não expira nós — um nó que para de enviar heartbeat permanece
  listado indefinidamente (com `LastSeen` estagnado).
