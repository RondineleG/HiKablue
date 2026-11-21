<!-- chapter-id: ch03-control-plane -->
# Chapter 3 — Who May Speak: The Control Plane Learns to Remember

*The Journey of my VPN* · Chapter `ch03-control-plane` · Milestone 3:
control plane & edge agent gRPC loop (phases 1–2: contracts, lab CA,
persistence, IPAM, sessions API)

Chapters 1–2 gave the engine a tunnel and a voice. But the whole mesh still
ran on trust: any process that knew the shared `X-Heartbeat-Token` could
impersonate any node, and the control plane forgot everything at restart.
This milestone begins the real control plane — the piece that decides *who
may speak*, *which addresses are free*, and *which sessions exist* — and
this chapter covers its first two phases: the contract and its identity,
then the memory.

> **Lab boundary.** Everything still runs in an isolated, authorized lab
> (`.agc/RULES.md` LAB-01). The CA under `tests/lab/pki/` is generated
> locally by `gen-lab-pki.sh` and is *lab-only* — never promoted. Session
> ownership uses an `X-Subject` header plus an admin token; real user
> authentication is deferred to the user-accounts milestone.

## The contract comes first: `NodeChannel`

Before any code, `proto/edge_orchestrator.proto` freezes the conversation
between agent and control plane — because every other piece compiles
against it:

```text
AgentMessage   = NodeRegister | NodeHeartbeat | CommandAck
ControlCommand = ProvisionPeer | RevokePeer | SyncState
```

Two design choices matter:

- **Every command carries fencing fields.** `command_id`, `stream_epoch`,
  `backend_instance_id`, `revision` and `deadline` travel together, so a
  stale command from an old stream — or a command that outlived its
  deadline — can be *recognized* as stale and rejected with a typed
  `AckErrorCode` (`FENCED_STREAM`, `DEADLINE_EXCEEDED`, …) instead of
  silently corrupting the dataplane.
- **Raw key material only in `ProvisionPeer`.** `Peer.key_ref` is a
  reference; snapshots (`SyncState`) carry framing metadata —
  `peer_count`, `peer_list_length`, `peer_list_sha256` — so a truncated or
  corrupted snapshot fails verification instead of applying half a state.

## Identity: a lab CA and SPIFFE names

`tests/lab/gen-lab-pki.sh` builds a throwaway ECDSA P-256 CA (`umask 077`,
CA extensions `basicConstraints=critical,CA:TRUE` +
`keyCertSign,cRLSign`) and issues per-node client certificates
(`extendedKeyUsage=clientAuth` only). The node's identity lives in a
Subject Alternative Name URI:

```text
spiffe://lab.hikablue/node/<node-id>
```

Re-issuing a certificate for the same node id *is* rotation. Phase 3 will
bind that SAN to the gRPC stream — the control plane will trust the name
on the wire only because the CA signed it.

## The memory: four tables

PostgreSQL is the allocator's truth, per ADR-0001:

| Table | Row = | Why it is safe under concurrency |
|---|---|---|
| `nodes` | a registered backend node | `FOR UPDATE` on the node row serializes all provisioning for it |
| `sessions` | a client lease request | `UNIQUE (node_id, lab_session_id)` + `idempotency_key` unique |
| `ip_leases` | one reserved address | `PRIMARY KEY (address)` — the unique index *is* the allocator |
| `outbox` | a pending command to a node | `FOR UPDATE SKIP LOCKED` lets dispatchers steal work safely |

EF Core maps these with one quirk worth remembering: `IPAddress` cannot be
an EF key in .NET 10, so `IpLeaseRow` is keyless to the ORM, the real
`PRIMARY KEY (address)` is added by the migration, and lease
insert/delete go through raw SQL — Npgsql maps `IPAddress` to `inet`
natively.

## The allocator's move, step by step

`SessionService.CreateSessionAsync` runs inside `ReadCommitted`:

1. `INSERT ... ON CONFLICT DO NOTHING` the node, then
   `SELECT node_id ... FOR UPDATE` — one provisioning at a time per node.
2. `lab_session_id = COALESCE(MAX(...), 0) + 1` under that lock.
3. Candidate addresses come from the **whole** CIDR, hash-shuffled by
   `HashCode.Combine(node_id, session_id, address)` — dispersed probing,
   never a linear scan from index 0 and never a truncated prefix.
4. Insert session + `ProvisionPeer` outbox row, then the lease row, one
   transaction — the PK rejects a loser deterministically.
5. On `23505` (unique violation): retry up to 3 times with 10–50 ms
   jitter; on exhaustion, re-read the idempotency key — a concurrent
   winner's session replays instead of a spurious 503.

The CIDR is masked to its network first (`10.99.0.2/30` → network
`10.99.0.0`), so a misaligned pool can never lease addresses outside the
block.

## The door: `POST /v1/sessions`

`POST /v1/sessions` with `Idempotency-Key` returns **202** plus a status
URL — provisioning is async by design. Same key + same payload replays the
original response; same key + different payload is a **409** conflict.
`DELETE` moves a session to `revoking` and enqueues `RevokePeer`; the IP
lease is *retained* until the node confirms — confirmed release lands with
the `CommandAck` handling in phase 3. Every response is `no-store`.

## The loop closes: `NodeChannel`

With persistence in place, the phase-3 change set wired the stream the
contract promised:

- **Identity before anything else.** Kestrel asks for a client certificate
  only when `VpnControl:LabCaPath` points at the lab CA (chains are
  validated with `CustomRootTrust`). `SpiffeCertificateValidator` then
  opens the SAN extension (OID 2.5.29.17) with `AsnReader`, picks the
  uniformResourceIdentifier entry, and requires
  `spiffe://lab.hikablue/node/<id>` — the id becomes `UserState["NodeId"]`
  and must equal `register.node_id`, so a certificate can only ever speak
  as itself.
- **`NodeChannelService`.** The first frame must be `NodeRegister`;
  `stream_epoch` is a monotonic numeric ID seeded from Unix milliseconds,
  allocated at attach (see r2 below). `NodeStreamRegistry`
  atomically replaces any previous stream (its CancellationTokenSource is
  cancelled — the fencing switch). Heartbeats refresh `nodes.last_seen_at`
  and the in-memory registry. A `CommandAck` is correlated by
  `command_id` *and* fenced: an ack stamped for another `stream_epoch` or
  `backend_instance_id` is ignored. The ACK must also match a stored
  `sent` attempt; PostgreSQL locks that row while applying the outcome.
  `APPLIED` marks the outbox row `acked`
  (and a `RevokePeer` ack deletes the lease and revokes the session);
  `FENCED_STREAM`/`FENCED_BACKEND` fail the row, close the stream and
  enqueue a `SyncState` resync; `DEADLINE_EXCEEDED`,
  `REVISION_REGRESSION`, `INVALID_PAYLOAD` fail the row and bump the
  failed-commands counter; anything else returns the command to `pending`
  with a 250 ms `next_retry_at`.
- **`OutboxDispatcherWorker`.** A short `FOR UPDATE SKIP LOCKED`
  transaction picks the oldest outstanding command per node if it is due.
  With a live stream it stamps the epoch, backend and a 30 s deadline,
  and commits `sent` with `attempts + 1` **before** the gRPC write.
  Writes to different nodes are independent. A write failure does not
  prove non-delivery: an unacknowledged attempt is retried after its
  deadline, with the same command ID, even without a reconnect. Without a
  live stream it waits in `pending` at `next_retry_at`. After three
  attempts without confirmation, the worker marks the command `failed`
  without releasing any lease. The `outbox-delivery-indexes` migration
  adds partial indexes for expired attempts and per-node ordering.

  **Exercise:** Run `dotnet test control-dotnet/VpnControl.Tests --filter FullyQualifiedName~DispatcherDoesNotHoldLocksDuringWritesOrRollBackOtherNodes`
  with Docker running. Find the two test writers: the healthy stream
  must be able to ACK while the other write is still blocked.
- **`LeaseReaperWorker`.** Every 60 s: sessions past `expires_at` whose
  node has been silent longer than the quarantine window (default 300 s)
  move to `quarantined`, their lease is deleted, and the audit log shows
  `FORCED_REAPER_RELEASE` while `ipam_reaped_leases_total` increments —
  the ADR-0001 hardening invariant, implemented.
- **The Go side.** `pkg/orchestrator` dials with `credentials.NewTLS`
  (lab CA as the only root, node cert as client identity), keepalive
  30 s/10 s and reconnect backoff. `Channel.Run` sends `NodeRegister`
  first, heartbeats every 10 s on a goroutine (a `sendMu` serialises
  concurrent `Send` calls, which grpc-go forbids), and `evaluate` fences
  before dispatch: invalid or older `stream_epoch` → `FENCED_STREAM`,
  a foreign `backend_instance_id` → `FENCED_BACKEND`, missing or invalid
  `deadline` → `INVALID_PAYLOAD`, expired deadline → `DEADLINE_EXCEEDED`,
  and an unseen command at a nonzero `revision` ≤ applied →
  `REVISION_REGRESSION`. New commands require synchronized clocks: the
  deadline is absolute (no grace period and no reset at receipt). A
  command already applied can receive another `APPLIED` ACK without
  calling the handler again.

  Reuse `Channel` or pass the same `ReplayState` to a replacement channel
  for the same node and backend. This preserves the last applied revision
  and command IDs in memory. A new backend ID starts fresh, and registration
  requests reconciliation via `SyncState`. This is not disk persistence;
  the handler still has to be idempotent.

Verified locally: 16 new C# tests (SPIFFE acceptance/rejection matrix,
fencing, lease release on revoke ack) plus 14 Go tests — a real mTLS
handshake against a test CA, untrusted-cert rejection, and an in-process
bufconn `NodeChannel` that registers, heartbeats, dispatches and acks.
Coverage: C# ≥ 95%, Go ≥ 90% (generated `agent-go/gen` excluded, like the
source-generated OpenApi code).

## Reading the code

The allocator's whole safety argument fits in a dozen lines. First the
node lock — every provisioning on a node queues behind this:

```csharp
private async Task EnsureNodeLockedAsync(string nodeId, Entities.Node node, CancellationToken ct)
{
    // Upsert the row then lock it FOR UPDATE — single-issuer for
    // lab_session_id and desired_revision on this node (ADR-0001 §2).
    var now = DateTimeOffset.UtcNow;
    await db.Database.ExecuteSqlAsync($"""
        INSERT INTO nodes (node_id, backend_instance_id, registered_at, last_seen_at, engine_healthy, desired_revision)
        VALUES ({nodeId}, {""}, {now}, {node.LastSeen}, {node.EngineHealthy}, 0)
        ON CONFLICT (node_id) DO NOTHING
        """, ct);
    await LockNodeAsync(nodeId, ct);
}

private Task<int> LockNodeAsync(string nodeId, CancellationToken ct) =>
    db.Database.ExecuteSqlAsync(
        $"SELECT node_id FROM nodes WHERE node_id = {nodeId} FOR UPDATE", ct);
```

- `INSERT ... ON CONFLICT DO NOTHING` creates the node row if a session
  arrives before the node ever registered — upsert without failing.
- `SELECT ... FOR UPDATE` is the lease on the *parent* row: every
  concurrent `CreateSessionAsync` for this node serializes here, so the
  `MAX(lab_session_id)+1` below is computed by exactly one transaction.
- `ExecuteSqlAsync` interpolates parameters (`{nodeId}`) — it is **not**
  string concatenation; Npgsql sends them as bound parameters.

Then the probe — the part that survives contention:

```csharp
var candidates = pool
    .OrderBy(x => (uint)HashCode.Combine(node.NodeId, sessionId, x))
    .ToArray();
var taken = await db.Database
    .SqlQuery<string>($"SELECT host(address) AS \"Value\" FROM ip_leases WHERE address = ANY({candidates})")
    .ToListAsync(cancellationToken);
var free = candidates.FirstOrDefault(a => !taken.Contains(a.ToString()));
```

- `HashCode.Combine(node.NodeId, sessionId, x)` shuffles the **entire**
  pool per session — two concurrent creates start at unrelated offsets,
  so they only collide when the pool is genuinely nearly full.
- `host(address)` strips the `/32` that `address::text` would render, so
  a stored `inet` compares cleanly to `IPAddress.ToString()`.
- `FirstOrDefault` picks the first *unclaimed* candidate; a claimed one
  is skipped in memory before the INSERT — cheap insurance before the
  real arbiter (the PK) decides.
- If the insert still loses a race (`23505`), the transaction rolls back
  and the retry loop re-reads which addresses are now taken — the winner's
  claim is skipped on the next pass through the same dispersed order.

| File | What it does |
|---|---|
| `proto/edge_orchestrator.proto` | `EdgeOrchestrator.NodeChannel` bidi stream; fencing fields on every command; `AckErrorCode` taxonomy; `PeerList`/`PeerSnapshot` framing |
| `tests/lab/gen-lab-pki.sh` | Lab CA (`init`) + per-node client certs (`issue`) with SPIFFE SANs; `umask 077`, clientAuth-only EKU |
| `.agc/adr/0001-ipam-and-session-persistence.md` | Schema + invariants: node-row lock, dispersed probing, reaper policy |
| `control-dotnet/VpnControl/Data/` | `ControlDbContext` + rows: `nodes`, `sessions`, `ip_leases` (keyless EF row, real PK via migration), `outbox` |
| `control-dotnet/VpnControl/Services/SessionService.cs` | The allocation loop above + `RevokeSessionAsync` (revoking + `RevokePeer` outbox, lease retained) |
| `control-dotnet/VpnControl/Endpoints/V1/Sessions/` | `POST`/`GET`/`DELETE /v1/sessions` — `X-Subject`, `X-Admin-Token`, `Idempotency-Key`, 202 + `statusUrl`, `no-store` |
| `control-dotnet/VpnControl/Endpoints/HealthEndpoint.cs` | `/health/live` static, `/health/ready` = real `SELECT 1` round-trip |
| `control-dotnet/VpnControl.Tests/SessionServiceTests.cs` | Testcontainers Postgres: last-/30 race, concurrent idempotency, CIDR masking, lease lifecycle |

## Glossary

- **`AckErrorCode`** — the typed reason an agent puts on a `CommandAck`
  (`FENCED_STREAM`, `DEADLINE_EXCEEDED`, `REVISION_REGRESSION`, …) so the
  control plane reacts without parsing free text.
- **`backend_instance_id`** — a fresh identifier the backend mints per
  data-plane start; a restart changes it and fences pending commands.
- **CA (Certificate Authority)** — the lab's throwaway ECDSA P-256 root
  that signs each node's certificate; `gen-lab-pki.sh` creates it.
- **CIDR** — "Classless Inter-Domain Routing" notation (`10.42.0.0/22`):
  a base address plus a prefix length that defines the IPAM pool.
- **`CommandAck`** — the agent's answer to a `ControlCommand`: applied,
  rejected, or errored, always echoing the command's fencing fields.
- **EF Core / Npgsql** — the .NET ORM and its PostgreSQL provider; maps
  rows to classes and `IPAddress` to the `inet` column type.
- **`FOR UPDATE` / `SKIP LOCKED`** — PostgreSQL row locking: `FOR UPDATE`
  serializes writers on a row; `SKIP LOCKED` lets a worker claim pending
  outbox rows another worker hasn't locked yet.
- **Fencing** — rejecting a command because the stream or backend that
  issued/executed it is stale (`stream_epoch`, `backend_instance_id`).
- **gRPC `NodeChannel`** — the bidirectional streaming RPC the agent
  opens; commands flow back on the same connection (phase 3).
- **`host(address)`** — PostgreSQL function returning the address part of
  an `inet` value with no `/32` suffix — what the probing query compares.
- **Idempotency-Key** — client-supplied key making a `POST` replayable:
  same key + same payload → same answer; different payload → 409.
- **`inet`** — PostgreSQL type for an IP address; its unique index is the
  lease allocator.
- **IPAM** — IP Address Management: the allocator that hands out and
  reclaims tunnel addresses.
- **`lab_session_id`** — the numeric session id unique among *live*
  sessions on a node; what the lab dataplane protocol carries.
- **mTLS** — mutual TLS: both sides present certificates; the agent's
  cert carries its SPIFFE identity in a SAN URI.
- **`23505`** — the PostgreSQL `unique_violation` SQLSTATE; the retry
  loop's signal that a candidate was claimed mid-transaction.
- **Outbox pattern** — commands are stored (`outbox` table) before being
  dispatched, so a crash can't lose a `ProvisionPeer`/`RevokePeer`.
- **SAN (Subject Alternative Name)** — the certificate extension (OID
  2.5.29.17) that carries the SPIFFE URI identifying the node.
- **SPIFFE** — a workload-identity standard; here the URI
  `spiffe://lab.hikablue/node/<id>` naming each node.
- **`stream_epoch`** — control-plane-issued id for one authenticated
  stream; a reconnect bumps it and fences the old stream's ACKs.
- **Testcontainers** — the library tests use to boot a real PostgreSQL
  in Docker, so allocator races are tested against the real engine.



- `tests/checks.sh --lab` — ALL PASSED (including a concurrent-create race
  against the last /30 address and idempotent replay under concurrency,
  on real PostgreSQL via Testcontainers).
- `openssl verify` on lab PKI artifacts; `protoc` compile clean.
- Coverage: C# ≥ 95% line.

## r2 — Stream lifetime and reconnect hardening

A failed receive must stop the heartbeat even if the caller stays alive.
A failed heartbeat must also cancel a blocked receive. In
`agent-go/pkg/orchestrator/channel.go`, the run owns a child context and
waits for its heartbeat before returning:

```go
defer func() {
	cancel()
	<-heartbeatDone
}()
```

Line 1 schedules cleanup on every return after the heartbeat starts.
Line 2 cancels only this run's child context, unblocking gRPC I/O and the
ticker. Line 3 waits for the heartbeat's completion channel to close.
Line 4 invokes the deferred function on return. A heartbeat send failure
normally cancels the run to unblock `Recv`. The exception is `io.EOF`:
gRPC can stop accepting sends before `Recv` delivers the server's final
status. `Run` leaves the receive active and reports its result instead.
`TestRunCancelsStreamAfterReceiveFailure`,
`TestRunHeartbeatFailureUnblocksReceive`, and
`TestRunPreservesServerStatusAfterHeartbeatEOF` exercise these cases with
scripted streams, without a live endpoint.
`TestHeartbeatLoopLeavesReceiveAliveAfterEOF` also checks that the context
is still active after the heartbeat returns.
`TestHeartbeatEOFLeavesFinalStatusReadableOnGRPCTransport` uses real gRPC
over an in-memory connection: the server receives a heartbeat while open,
then closes with `PermissionDenied`. After the next send reports EOF,
`Recv` still reads that status. This did not reproduce a stuck receive,
so the existing transport keepalive remains the fallback.

Two reconnects can occur in one millisecond; a clock can move backward.
`NodeStreamRegistry.Attach` now computes this inside the same lock that
publishes the replacement stream:

```csharp
_lastEpoch = Math.Max(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
    checked(_lastEpoch + 1));
```

Line 1 takes the current Unix milliseconds from an injectable clock.
Line 2 chooses at least the previous ID plus one; `checked` throws rather
than wrapping at integer overflow. Epoch allocation and stream replacement
are serialized, while cancellation stays outside the lock. An old stream's
`Detach` cannot remove its successor. `NodeStreamRegistryTests` covers a
frozen clock, clock rollback, stale cleanup and concurrent attaches.
This is monotonic **within one registry/process**, not a durable,
multi-control-plane fencing protocol.

**Terms:** `context.WithCancel` creates a child cancellation scope;
`defer` runs cleanup on return; `<-heartbeatDone` waits for a channel
close; `Recv` reads a gRPC frame or the final status; `io.EOF` signals
the end of a stream; `TimeProvider` supplies the clock;
`Math.Max` selects the larger number; `checked` detects arithmetic
overflow; a lock serializes access to shared state.

**Reproduce:** from `agent-go`, run `go test -race ./pkg/orchestrator`;
from the repository root, run
`dotnet test control-dotnet/VpnControl.Tests --filter FullyQualifiedName~NodeStreamRegistryTests`.
Both focused checks passed on the Ubuntu development VM. They prove
stream lifetime/fencing, not packet forwarding or application connectivity.
The `Quality gate` PR workflow is configured for Go formatting and vet,
.NET build, and `COVERAGE_GATE=1 bash tests/coverage.sh` (tests with coverage
≥90% for Rust, Go and .NET, including PostgreSQL via Docker). The workflow
did not start on PR #15 due to a GitHub billing lock; the evidence above is
local. `bash tests/checks.sh` remains the full unprivileged local gate.
The separate branch validator still requires `fix/<slug>` or `feat/<slug>`.

**Exercise:** freeze the test clock, attach a replacement, then detach the
old epoch. Hint: use `SameMillisecondReconnectGetsANewEpochAndSurvivesOldDetach`;
check that the second epoch is larger and `TryGet` still returns it.
Do not test this by changing the host clock.

## Revisions

- `r0` — contracts (proto, AckErrorCode, fencing fields), lab CA, ADR-0001
  merged (PR #11); persistence + IPAM + `/v1/sessions` + health probes
  delivered (PR #12): 85 C# tests green, last-IP race and idempotency
  verified on PostgreSQL 16.
- `r1` — gRPC loop delivered: `NodeChannelService` + SPIFFE validation,
  outbox dispatcher (SKIP LOCKED), lease reaper, and the Go agent's
  `pkg/orchestrator` (mTLS dial, register/heartbeat/ack loop, fencing).
  Outbox schema extended (`stream_epoch`, `backend_instance_id`, `status`,
  `attempts`, `next_retry_at`, `deadline` — migration `outbox-dispatch`).
- `r2` — child-context cancellation and heartbeat join; monotonic
  process-local stream epochs; regression tests and a PR quality/coverage
  gate. No new milestone acceptance or live-network result is claimed.
