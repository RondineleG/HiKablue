# Milestone 4 session laboratory

`session-e2e.sh` is the reproducible end-to-end acceptance laboratory for
milestone 4. It uses only test traffic inside Linux network namespaces
created by the script and removes those namespaces when it exits.

## Topology

```text
client A namespace ─┐
                    ├─ bridge in exit namespace ── exit engine ── service namespace
client B namespace ─┘        (outer veths)          (TUN/UDP)      HTTP 10.80.1.100:8080
```

The exit engine is provisioned through the real control path:

```text
POST /v1/sessions
  -> PostgreSQL session + lease + outbox row
  -> mTLS gRPC NodeChannel
  -> vpn-agent -serve
  -> Rust control socket StagePeer + Activate
  -> CommandAck
  -> session pending -> active
```

The two client namespaces run `vpn-engine client`. For milestone 4 their
first epoch is bootstrapped with the exact keys returned by the lab session
API, but the material never appears on a process command line: the script
writes each key to a `0600` file inside the private run directory and passes
`--tx-key-file`/`--rx-key-file` (the inline `--tx-key`/`--rx-key` forms still
exist for compatibility, warn on use, and are documented as unsafe for real
deployments). After each client starts, the script reads
`/proc/<pid>/cmdline` and fails if any key material leaked into the argument
list. Status polling and revocation present the bootstrap `sessionToken` as
`X-Session-Token`; the script preserves the create response separately for
that purpose. The client direction is the inverse of the exit engine
direction. This is lab-only bootstrap, not the later user-facing client
provisioning flow.

The service namespace runs `python3 -m http.server` bound to
`10.80.1.100:8080` behind the exit node. The control plane leases only
`10.80.1.128/25`, so this test address can never collide with a client VIP.
Client-to-client hairpinning is
intentionally denied in the milestone-4 policy; forwarded service answers
may keep the service source address.

## Run-scoped resource ownership

Every run generates a random `RUN_TAG`; all namespaces
(`hkm4e<tag>`, `hkm4a<tag>`, `hkm4b<tag>`, `hkm4s<tag>`) and veth/bridge
interface names carry it, so concurrent runs never collide and a stale
pre-run object is detected rather than adopted. Control-plane, proxy, and
PostgreSQL ports are allocated dynamically at run time, so no fixed port is
required to be free.

Privileged resources are tracked in a per-run ownership registry
(`tests/lab/lab-resources.sh`):

- `lab_netns_add` refuses to create a namespace that already exists and
  registers each namespace it successfully creates;
- `lab_root_veth_add` registers both ends of each veth pair it creates;
- `lab_register_root_pid` records privileged background processes together
  with the run directory as an ownership marker;
- `lab_cleanup_scope` kills only registered PIDs whose `/proc` cmdline still
  carries the run-directory marker (protecting against PID reuse), deletes
  only registered links, and removes only registered namespaces — never
  anything a previous run or another user left behind.

Unprivileged children (agent, control plane, proxy, monitor loop) are killed
only while they remain children of the run's own shell (`kill_user_pid`),
so a reused PID can never turn cleanup into a foreign-process kill. Cleanup
is idempotent and also runs on partial-failure paths.

`tests/lab/session-e2e-safety.test.sh` exercises the registry offline:
partial-failure cleanup, preexisting-resource refusal, PID-reuse safety, and
concurrent-scope isolation. The quality gate runs it under `sudo`.

## Prerequisites

Run on a Linux host with:

- Rust (`cargo`), Go 1.18.x, .NET 10, Python 3, `jq`, `curl`, `openssl`,
  `xxd`, `iproute2`, and PostgreSQL server binaries;
- passwordless `sudo` for network namespaces and TUN creation;
- `/dev/net/tun`.

Ports and namespace/interface names are allocated per run; nothing needs to
be reserved in advance. The script starts a temporary PostgreSQL cluster
inside its own `mktemp` directory with a random SCRAM password that never
leaves the environment of the run. It does not require Docker. It creates a
lab-only CA, a node client certificate, and a loopback server certificate.
All keys are test material and are deleted with the run directory. The admin
token, heartbeat token, `VpnControl__SessionTokenSecret`, and database
password are random per run and never enter logs.

## Commands

From the repository root:

```sh
# normal revocation acceptance
tests/lab/session-e2e.sh

# independent gRPC interruption/reconciliation acceptance
tests/lab/session-e2e.sh --drop

# local expiry enforced across an agent restart (control plane unreachable)
tests/lab/session-e2e.sh --expire-restart

# milestone 5 backend restart, confirmed invalidation, fresh-session
# recovery, and client route rollback
tests/lab/session-e2e.sh --engine-restart

# repository quality gate, including the privileged labs and the
# ownership-registry safety test
tests/checks.sh --lab
```

The normal run verifies that both clients receive distinct leases, remain
`pending` until valid provisioning ACKs, pass HTTP through the tunnel, and
that revoking A blocks A while continuous requests from B keep succeeding.
The drop run closes a deliberately delayed TCP proxy while revocation is in
flight, checks that A remains `revoking` with its lease reserved, restores
the proxy, and waits for redelivery/reconciliation to finish removal before
the lease is released.

The expire-restart run gives session A a short TTL, then — after proving
both tunnels carry HTTP — drops the gRPC proxy, kills the agent process,
and restarts it (`-state-dir` preserved) while the Rust engine keeps
running and the control plane stays unreachable. The restarted agent
rehydrates the persisted expiry deadline and removes A locally when it
passes; the script proves A lost service access without any control plane
and that B kept working throughout. Restoring the proxy then lets the
fenced removal ACK release A's lease — the script asserts the lease stayed
reserved while removal was unconfirmed and that the expired session is
never resurrected during reconciliation.

The engine-restart run is the milestone-5 backend recovery acceptance.
After normal two-client HTTP connectivity, it terminates and restarts the
exit engine. The agent observes the changed `backend_instance_id`, exits
for supervised restart, and is started again. Registration with the fresh
instance enqueues an empty-state reconciliation that deliberately excludes
the pre-restart sessions. Only after that reconciliation is applied does
the control plane mark those sessions `failed` with
`backend_restart_requires_new_session` and release their leases. The script
then proves three separate things: client B cannot continue with stale
keys, killing client A with `SIGKILL` removes its transient `tun0` and the
connected tunnel route, and a fresh session in the same namespace receives
fresh keys and restores HTTP connectivity.

## Evidence ordering

`OBSERVED:` lines are emitted only after the assertion they describe. The
scenarios distinguish four distinct events: command dispatched (`sent` in
the outbox), command applied by the agent, node `CommandAck` received, and
lease released. Losing the gRPC channel is never reported as data-plane
loss, and infrastructure failures abort the run instead of counting as
functional success.

## Observed milestone-4 results

Observed on the Ubuntu development VM used for this change:

| Area | Result |
|---|---|
| Compilation | Rust release build, Go `vpn-agent` build, and C# `VpnControl` build completed before each laboratory run |
| Packet forwarding | final server metrics in the normal run: `rx_pkts=105`, `tx_pkts=105`, `dropped_pkts=4`, `peers=1`; drop run: `rx_pkts=152`, `tx_pkts=151`, `dropped_pkts=5`, `peers=1` |
| HTTP connectivity | both clients received exactly `hikablue-m4-ok` through the tunnel; the direct-path probe failed before tunnel setup |
| Normal revocation | `session-e2e (normal): PASS` — A lost access, B had no failed continuous responses, and A's lease count changed from 1 to 0 only after the node ACK |
| gRPC interruption | `session-e2e (drop): PASS` — A stayed `revoking` with lease count 1 while the channel was down; restoring the channel completed removal, marked the session `revoked`, released the lease, and A still had no access |
| Agent restart expiry | `session-e2e (expire-restart): PASS` — the restarted agent expired A from its persisted deadline with no control plane, B had zero failed responses, the reconnect reconciled A to `revoked` without resurrecting it, and the lease was released only after the fenced removal ACK |
| Engine restart recovery | `session-e2e (engine-restart): PASS` — the agent detected the fresh backend instance, pre-restart sessions became `failed` only after confirmed empty-state reconciliation, stale keys could not pass traffic, the killed client's TUN/route disappeared, and a fresh session restored HTTP connectivity |
| Key material hygiene | every client engine command line was verified free of key material; keys live only in `0600` files under the private run directory |

Exact counters vary because keepalives and continuous probe timing vary;
the acceptance output lines are the stable result.

## Contract notes and explicit deferrals

- `vpn-debian-specification.md` still names the Go↔Rust methods
  `AddPeer`, `RemovePeer`, `SyncPeers`, and `GetMetrics` on
  `/run/vpn/engine.sock`. This change deliberately reuses the control
  boundary already delivered for milestone 4 instead of renaming it: a
  separate control socket carries `StagePeer`, `Activate`, `Retire`,
  `RevokePeer`, and `GetSessions`, while the metrics socket remains
  read-only. That naming/socket divergence is documented here rather than
  silently redesigned during the acceptance work.
- Client keys are delivered through protected files, never arguments. The
  inline `--tx-key`/`--rx-key` flags remain only for compatibility, emit a
  warning when used, and must not be copied to production use — this is not
  the final client key-delivery design.
- Snapshots intentionally carry peer/key references, not raw keys, so a new
  backend instance cannot resurrect old epochs from `SyncState`. The
  milestone-5 recovery behavior is explicit invalidation plus a fresh API
  session; automatic rekeying of an existing client session remains a later
  architecture step.
- The expire-restart scenario restarts only the Go agent while the Rust
  backend keeps running; backend restart recovery is covered separately by
  `--engine-restart`.
- The agent persists expiry deadlines in `expiry.json` (`0600`, fsync'd
  write + rename + directory fsync) inside a private `-state-dir` (default
  `/run/vpn/agent-state`, validated `0700` and writable before the channel
  starts). Deadlines already past at startup are enforced immediately.

## Cleanup and failure artifacts

The script removes only namespaces, links, processes, PostgreSQL data,
sockets, keys, and temporary files that its own run registered. On failure
it copies the run directory to a unique `/tmp/hk-m4-session-failed-*`
path before cleanup so `control.log`, `agent.log`, engine logs, PostgreSQL
logs, and session JSON can be inspected. Delete that directory manually
after investigation. The copy preserves the owner-only permissions of the
run directory because it contains lab session keys.
It can contain lab key material; never commit it.
