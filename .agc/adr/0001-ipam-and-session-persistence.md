# ADR-0001: IPAM and session persistence for the control plane

- Status: Proposed
- Date: 2026-11-20
- Related specification: `vpn-debian-specification.md` §5 (API surface, persistence model) and milestone 3.

## Context

Milestone 3 turns `vpn-control` from an in-memory heartbeat intake into the
control plane: session lifecycle (`pending → active → revoking → revoked`,
plus `failed`), per-node peer provisioning, and lease management for a
configured IPv4/IPv6 pool. The spec fixes the contract — PostgreSQL,
single-transaction session+lease+command, a uniqueness constraint on the
lease address, an `outbox` table, and UUID API sessions plus a numeric lab
session id — but leaves schema shape and locking strategy open.

## Decision

Four tables, exactly the spec's starting set:

```sql
nodes (
    node_id            text        primary key,   -- matches mTLS cert identity
    backend_instance_id text       not null,      -- current data-plane instance
    registered_at      timestamptz not null,
    last_seen_at       timestamptz,
    engine_healthy     boolean     not null default false
);

sessions (
    session_id         uuid        primary key,
    idempotency_key    text        unique,        -- nullable; set by POST /v1/sessions
    status             text        not null,      -- pending|active|revoking|revoked|failed|quarantined
    virtual_ip         inet        not null,      -- audit trail; uniqueness enforced by ip_leases
    node_id            text        not null references nodes(node_id),
    lab_session_id     integer     not null,      -- unique among ACTIVE rows per node
    key_epoch          bigint      not null default 0,
    expires_at         timestamptz not null,
    created_at         timestamptz not null default now(),
    updated_at         timestamptz not null default now()
);

ip_leases (
    address            inet        primary key,   -- uniqueness IS the allocator
    session_id         uuid        references sessions(session_id),
    node_id            text        not null,
    reserved_at        timestamptz not null default now()
);
-- ip_leases holds only ACTIVE leases: release = DELETE the row.
-- The freed address is immediately reusable; the audit trail lives in
-- sessions.virtual_ip + sessions.status, so no address is ever unique
-- across history (only among live leases).

outbox (
    command_id         uuid        primary key,
    node_id            text        not null,
    command_type       text        not null,      -- provision_peer|revoke_peer|sync_state
    payload            jsonb       not null,      -- serialized ControlCommand
    created_at         timestamptz not null default now(),
    delivered_at       timestamptz,
    acked_at           timestamptz
);
```

Key mechanics:

- **IP allocation** = `INSERT INTO ip_leases` under the PK, tried against a
  small ordered candidate set derived from the configured CIDR. A unique
  violation retries the next candidate; pool exhaustion surfaces as
  `session.status = failed` + 409/503 to the API. Two competing requests for
  the last IP: exactly one insert wins — the acceptance criterion.
- **Atomicity** = one PostgreSQL transaction writes the `sessions` row, the
  `ip_leases` row and the `outbox` `ProvisionPeer` command — the spec's
  required transactional boundary. No advisory locks needed; row
  constraints do the fencing.
- **Idempotency** = `idempotency_key` unique on `sessions`. `POST
  /v1/sessions` inserts it in the same transaction; a replay hits the
  unique index and returns the existing status URL instead of a second
  lease.
- **lab_session_id** = per-node `MAX(lab_session_id)+1` selected inside the
  same transaction, constrained by a partial unique index
  `ON sessions(node_id, lab_session_id) WHERE status IN ('pending','active','revoking')`.
  Revoked ids are reusable; numeric ids stay small for the lab protocol.
- **Outbox/dispatcher** = a single background dispatcher claims pending
  rows (`FOR UPDATE SKIP LOCKED`), pushes `ControlCommand`s onto the
  node's live stream if registered — else queues until `NodeRegister` —
  stamps `delivered_at`, and maps `CommandAck` to `acked_at` +
  `sessions.status` transition. Redelivery of an already-acked command is
  idempotent on the agent (same `command_id` → same result).
- **Revocation** = `DELETE /v1/sessions/{id}` → `revoking` + `RevokePeer`
  in one transaction; `acked_at` flips it to `revoked` and deletes the
  `ip_leases` row, freeing the address for reuse. IP is freed only after
  confirmed removal.
- **Deletion guard** enforced agent-side per spec (reject snapshots whose
  removals exceed 20% of current peers or replace a nonempty set with
  empty, unless a forced, audited override).

## Consequences

- One database round-trip decides allocation; no reservation state can
  leak between API and dispatcher.
- `ip_leases.address` as PK makes the allocator provably serial without
  extra locking infrastructure.
- `outbox` gives at-least-once delivery for free and survives control
  restarts; command replay is safe because commands carry
  `command_id`/`stream_epoch`/`backend_instance_id`.
- Deferred to implementation: EF Core mappings + migrations, dispatcher
  concurrency limits, `GET /v1/nodes` switching from in-memory registry to
  the `nodes` table, and user-facing auth/audit (spec marks them
  milestone-3 scope, separated from this design).

## Evidence and alternatives

- Alternative — advisory-lock allocator (`pg_advisory_xact_lock` over a
  lease range): works, but hides the conflict signal and complicates
  proving the "two contenders, one wins" acceptance test. Unique-PK retry
  is simpler and observably correct.
- Alternative — separate `lease_assignments` table: rejected; the lease
  row already carries `session_id`/`node_id`, a second table duplicates it.
- Spec constraint honored verbatim: "at minimum the entities needed for
  session creation, uniqueness, delivery tracking, and cleanup" — the four
  tables above cover exactly that.

### Invariants & Concurrency Rules (Milestone 3 Hardening)

1. **Subnet Sizing vs Registry Cap:** the `NodeRegistry` caps at 1024 nodes
   (PR #10). The IPAM pool CIDR must be at least a /22 (1022 usable hosts)
   or several /24 blocks, and the `ip_leases` budget must count both active
   and pending sessions.
2. **Session Allocation Race Prevention (`lab_session_id`):** provisioning
   transactions take an exclusive lock on the parent node row
   (`SELECT node_id FROM nodes WHERE node_id = $1 FOR UPDATE;`) before computing
   `MAX(lab_session_id) + 1`, with a bounded exponential retry loop
   (max 3 attempts, 10–50 ms jitter) to absorb error 23505
   (unique_violation).
3. **IP Probing Dispersion:** linear probing from index 0 under
   concurrency is forbidden; probe order is a pseudo-random seed from
   `hash(node_id + stream_epoch)` or
   `SELECT candidate FROM available_ips ORDER BY random() LIMIT 1`
   inside the reservation attempt.
4. **Stale Lease Reclamation (Reaper Policy):** normally `ip_leases` rows
   are deleted only after a valid `RevokePeer` `CommandAck` with matching
   `stream_epoch` and `backend_instance_id`. For dead/unresponsive nodes a
   background reaper runs every 60 s, moves sessions whose `expires_at <
   NOW()` without heartbeat/ACK for more than 2× the quarantine window
   (default 300 s) to `QUARANTINED`, force-releases the lease writing an
   audit record (`FORCED_REAPER_RELEASE`), and increments
   `ipam_reaped_leases_total`. A late ACK for an expired session is
   rejected with `ACK_ERROR_DEADLINE_EXCEEDED` and triggers an immediate
   agent resync.

   **Recorded divergence (ARCH-01):** the session spec currently requires
   keeping an IP reserved while its node is unreachable — release only
   after confirmed removal. The reaper above relaxes that rule for dead
   nodes (leases would otherwise leak forever on a permanently lost node):
   forced release is allowed only after the full quarantine window and is
   always audited. The spec must be amended to carry this exception before
   implementation; if it is not, the reaper falls back to quarantining
   without releasing the lease.
