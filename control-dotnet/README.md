# control-dotnet — vpn-control

`vpn-control` (C#/.NET) owns authentication, IPAM, session lifecycle and
auditing backed by PostgreSQL. Today it delivers:

- `POST /internal/heartbeats` — records a node-agent heartbeat
  (`{nodeId, engineHealthy, metrics?}`). Temporary HTTP transport: replaced
  by the gRPC `NodeChannel` stream in the control-plane milestone.
- `GET /v1/nodes` — lists nodes with `lastSeen` and `engineHealthy` tracked
  separately: a quiet node is stale, a beating node with a dead engine is
  degraded — different failures, different fields.
- `POST /v1/sessions` — allocates a session: picks an eligible (healthy,
  beating) node, reserves a virtual IP and writes the `ProvisionPeer`
  outbox command **in one transaction** (ADR-0001). Answers 202 with the
  session view and its `statusUrl`. Honors `Idempotency-Key`: same key +
  same payload replays the original session, same key + a different
  payload answers 409.
- `GET /v1/sessions/{id}` — session view for the owner or an admin.
- `DELETE /v1/sessions/{id}` — moves the session to `revoking` and writes
  the `RevokePeer` outbox command; the IP lease is released only after the
  backend confirms removal.
- `GET /health/live`, `GET /health/ready` — liveness, and readiness that
  additionally proves PostgreSQL answers (`SELECT 1`).

Caller identity is lab-grade: `X-Subject` names the owner (required on
POST; GET/DELETE need it unless `X-Admin-Token` matches
`VpnControl:AdminToken`). Session responses carry
`Cache-Control: no-store`. Persistence lives in `Data/` (EF Core +
Npgsql, schema in `initial-control-plane` migration per ADR-0001); the
pool comes from `VpnControl:IpPoolCidr` (default `10.42.0.0/22`). With no
`VpnControl:ConnectionString` configured the process still boots — the
heartbeat slice stays usable and only DB-backed routes fail.
- `VpnControl.Tests` — xunit tests: registry, HTTP contracts, and the
  session service on real PostgreSQL via Testcontainers (last-IP
  contention, idempotency replay/conflict, revoke lifecycle, owner/admin
  reads).

**Security:** the heartbeat endpoint is **lab-only** and answers only to a
**shared secret**: every request must carry `X-Heartbeat-Token`, configured
via `VpnControl:HeartbeatToken` (or the `VpnControl__HeartbeatToken`
environment variable). The service **refuses to start without a token** and
rejects a missing or wrong token with HTTP 401. It must never face a
network: the service checks its URLs at startup and **refuses to start on a
non-loopback bind** (wildcard, LAN, `0.0.0.0`), whatever the configuration
source (`urls`, `ASPNETCORE_URLS`, appsettings).

**Current protection:** a shared secret — adequate for the isolated lab
only; any local process holding it can report as any `nodeId` (no per-node
identity). **Future protection:** individual node identity via the mTLS
gRPC `NodeChannel` in the control-plane milestone. See "Limitações
conhecidas" below for payload limits and node expiry.

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
  Endpoints/V1/Sessions/            session create/read/revoke
  Endpoints/HealthEndpoint.cs       /health/live + /health/ready
  Data/                             ControlDbContext + rows + migrations (ADR-0001)
  UseCases/{Nodes,Sessions}/        IUseCase<TRequest, TResponse> implementations
  Services/                         NodeService over NodeRegistry (in-memory),
                                    SessionService (IPAM + lifecycle on PostgreSQL)
  Entities/                         Node (+ Base/BaseEntity)
  Interfaces/                       IEndpointAsync, IUseCase, INodeService
  Requests/                         RequestResult / EResultStatus (OnForkHub result pattern)
  Extensions/                       MapRegisteredEndpointsAsync + AddVpnControl (convention DI)
  Models/                           Heartbeat, NodeView, ApiResponse records
  Security/                         LoopbackGuard, HeartbeatAuth
```

## Limitações conhecidas

Proteções ativas no Marco 1 (laboratório isolado):

- `/internal/heartbeats` exige o segredo compartilhado `X-Heartbeat-Token`
  (config `VpnControl:HeartbeatToken` / env `VpnControl__HeartbeatToken`);
  sem token configurado o serviço recusa subir. Respostas inválidas: 401.
- O payload do heartbeat é limitado — `node_id` ≤ 128 caracteres, ≤ 64
  chaves de métrica com ≤ 64 caracteres cada; violações respondem 422.
- O registro em memória é limitado a 1024 nós (`maxNodes`) — um `nodeId`
  novo com a tabela cheia responde 429; nós conhecidos continuam
  atualizando.
- Nós que param de enviar heartbeat expiram do registro após 10 minutos
  sem `LastSeen` novo.
- O documento OpenAPI é servido em `/openapi/v1.json` (loopback apenas).
- A identidade de chamada em `/v1/sessions` é por cabeçalho `X-Subject` —
  mero rótulo de laboratório, sem verificação; autenticação real chega com
  o marco de usuários. `X-Admin-Token` (`VpnControl:AdminToken`) permite
  leitura/revogação administrativa.

Limitação que permanece, deferida ao marco de control plane:

- O token é um segredo **compartilhado**, não identidade por nó — qualquer
  processo local com o segredo pode bater por qualquer `nodeId`. A
  identidade real chega com o `NodeChannel` gRPC (mTLS). `GET /v1/nodes`
  segue aberto no loopback, e o serviço continua recusando binds fora do
  loopback via `LoopbackGuard`.
