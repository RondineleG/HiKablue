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

## What is still missing

The other half of the loop: `NodeChannelService` (the gRPC stream with
mTLS + SPIFFE validation), the `OutboxDispatcherWorker` (SKIP LOCKED →
live streams), the `LeaseReaperWorker` (quarantine stale sessions), and
the Go agent's mTLS client with fencing. They ride on the schema and
payloads built here.

## Verification

- `tests/checks.sh --lab` — ALL PASSED (including a concurrent-create race
  against the last /30 address and idempotent replay under concurrency,
  on real PostgreSQL via Testcontainers).
- `openssl verify` on lab PKI artifacts; `protoc` compile clean.
- Coverage: C# ≥ 95% line.

## Revisions

- `r0` — contracts (proto, AckErrorCode, fencing fields), lab CA, ADR-0001
  merged (PR #11); persistence + IPAM + `/v1/sessions` + health probes
  delivered (PR #12): 85 C# tests green, last-IP race and idempotency
  verified on PostgreSQL 16.
