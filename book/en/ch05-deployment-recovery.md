<!-- chapter-id: ch05-deployment-recovery -->
# Chapter 5 — Restart, Recover, Roll Back: Making the Lab Operable

*The Journey of my VPN* · Chapter `ch05-deployment-recovery` · Milestone 5:
deployment and recovery

A tunnel that only works when started by hand is not yet a system. This
chapter makes the node installable, supervisable, and honest about failure.
It answers three operational questions: which process owns each setting,
what happens when a process restarts, and how a host returns to a clean
network state.

> **Lab boundary.** The systemd units and installer automate the isolated
> laboratory profile. They do not enable production routing, NAT, full
> tunneling, or the custom UDP transport for real traffic. WireGuard remains
> milestone 6.

## Ownership before automation

| Object | Owner | Lifetime |
|---|---|---|
| TUN interface and its connected route | one `vpn-engine` process | transient; both disappear when the descriptor closes |
| engine metrics/control sockets | `vpn-engine` | recreated by each engine process |
| node expiry deadlines | `vpn-agent` | persisted in a private agent state directory |
| gRPC stream and backend registration | `vpn-agent` | one channel at a time; reconnects are fenced |
| sessions, leases, revisions, outbox rows | `vpn-control` + PostgreSQL | durable until explicit state transitions |
| installed binaries and release symlinks | `deploy/debian/install.sh` | versioned under `/opt/hikablue` |
| routes beyond the connected TUN route, DNS, NAT | nobody yet | deferred to the full-tunnel phase |

This table is the safety model. The agent does not configure TUN. The engine
does not write sessions to PostgreSQL. The installer does not start services
or edit routes. When something breaks, the owner is the first place to look.

## A repeatable installation

`deploy/debian/build-release.sh` creates one versioned release tree:

```text
artifacts/release/hikablue-<version>/
├── VERSION
├── BUILD_INFO
├── bin/
│   ├── vpn-engine
│   ├── vpn-agent
│   ├── hikablue-engine-service
│   ├── hikablue-agent-service
│   └── hikablue-control-service
├── control/VpnControl.dll and runtime files
├── systemd/*.service
└── config/*.sample
```

`deploy/debian/install.sh --release <tree>` copies that tree to
`/opt/hikablue/releases/<version>`, installs units and secret-free sample
configuration, creates the service users on a real host, and atomically moves
`/opt/hikablue/current`. It does not start the services. Running it twice
with the same unchanged release is a no-op; running it with different content
under the same version fails instead of mutating a running release.

An upgrade is the same command with a new version. The old target is kept in
`/opt/hikablue/previous`. `deploy/debian/rollback.sh` swaps `current` back
to that target. Binary rollback is deliberately separate from database
rollback: PostgreSQL data is not versioned by these scripts, so an
irreversible migration requires a database backup or tested reverse
migration.

The offline proof is [`tests/deploy/install-rollback.test.sh`](../../tests/deploy/install-rollback.test.sh).
It uses a temporary root, proves reinstall idempotence, proves operator
configuration is preserved, upgrades from `v1` to `v2`, rolls back, and
rejects a missing release. It creates no users and touches no host routes,
firewall rules, or services.

## systemd as the supervisor

The node has two services:

- `hikablue-engine.service` runs as `hikablue-engine` with only
  `CAP_NET_ADMIN`. The wrapper resolves the numeric agent user ID and starts
  the engine with separate metrics and control sockets.
- `hikablue-agent.service` runs as `hikablue-agent`, requires the engine,
  and has `Restart=always`. `PartOf=hikablue-engine.service` also couples
  its lifecycle to the engine.

The control plane has `hikablue-control.service`, running as
`hikablue-control` without network-admin capability. All sample environment
files are placeholders or empty values. Certificates and tokens are provisioned
outside git, normally in root-owned files.

A subtle systemd detail: the engine runs as a dedicated user but needs the
agent's numeric UID for the control socket. User names are not stable inside
an `ExecStart` line, so the unit executes this shell wrapper:

```sh
agent_uid=$(id -u "$AGENT_USER")

exec /opt/hikablue/current/bin/vpn-engine server \
    --bind "$ENGINE_BIND" \
    --tun-cidr "$ENGINE_TUN_CIDR" \
    --tun-name "$ENGINE_TUN_NAME" \
    --mtu "$ENGINE_MTU" \
    --metrics-sock /run/hikablue-engine/engine.sock \
    --metrics-group "$AGENT_USER" \
    --control-sock /run/hikablue-engine/control.sock \
    --control-uid "$agent_uid"
```

Line by line:

- `id -u` resolves the service account after the host administrator chose
  its name; the numeric UID is what the engine's `SO_PEERCRED` check needs.
- `exec` replaces the shell, so systemd tracks the engine itself rather
  than a wrapper process.
- `--bind` is the outer UDP endpoint in the isolated lab.
- `--tun-cidr` assigns the node-side tunnel address and creates the
  connected route while the process lives; `--tun-name` and `--mtu` select the
  interface name and packet size, and `--metrics-group` shares telemetry with
  the agent group.
- The metrics socket is telemetry; the control socket is separate because
  provisioning commands can carry key material.
- `--control-uid` admits the agent account (root and the engine user are
  also accepted by the socket policy).

## Backend restart is a new identity

Every running engine mints a random `backend_instance_id`. It identifies one
live data-plane instance, not a node in general. A process restart loses
all in-memory sessions, counters, replay windows, key epochs, and learned
endpoints. Reusing the old identity would therefore be a lie.

The agent now watches the control socket. When the engine comes back with a
different ID, the agent exits deliberately:

```go
sessions, err := control.Sessions(ctx)
if err != nil {
    continue
}
if sessions.BackendInstanceID != "" && sessions.BackendInstanceID != want {
    fmt.Fprintln(stderr, "vpn-agent: engine backend instance changed; exiting for supervised restart")
    cancel()
    return
}
```

Line by line:

- `GetSessions` is the authoritative local read. Failure means the engine is
  down or the socket is not ready; it does **not** prove a restart, so the
  loop continues.
- An empty ID is ignored because it cannot be fenced safely.
- A different non-empty ID is positive evidence of a fresh backend. The
  agent prints the reason, cancels its own context, and exits.
- systemd's `Restart=always` starts a new agent process. That process reads
  the fresh backend ID before opening the next `NodeChannel`.

Why exit instead of mutating the live configuration? The channel's fencing
state is bound to the original backend identity. A fresh process is smaller
and safer than carefully replacing immutable state in every in-flight path.

## Invalidating without resurrecting

A new engine is empty, but the control plane must not simply forget active
sessions: their leases are still reserved and clients still hold old keys. Sent but
unacknowledged commands from the dead backend generation are terminal rather
than replayed, because replay could reinstall state before the reset snapshot.
Registration with a changed backend therefore creates a special empty-state
`SyncState`. It excludes pre-restart pending/active sessions from the desired
peer list and carries their IDs plus already-`revoking` IDs as metadata. The agent can apply it
because the empty desired state matches the fresh backend.

Only an applied ACK has a side effect:

```csharp
resetSession.Status = SessionStatus.Failed;
resetSession.FailureReason = "backend_restart_requires_new_session";
resetSession.UpdatedAt = DateTimeOffset.UtcNow;
```

Line by line:

- the session does not become `revoked`: nobody revoked it;
- `failed` is terminal and records why the client must create a new session;
- the timestamp preserves operational ordering;
- in the surrounding code, the lease row is deleted in the same ACK side
  effect. Before that confirmation, the lease remains reserved.

A session already in `revoking` follows the other terminal branch: it becomes
`revoked`, because removal was already desired and the confirmed empty backend
is the removal confirmation the dead backend can never send. Its lease is
released in the same ACK side effect.

This is the important honesty boundary: `sent` is not application, and a
process restart is not cleanup confirmation. The control plane uses the
fresh backend's applied empty-state reconciliation as the confirmation that
old data-plane state is absent.

## Route rollback

The client engine currently installs no default route and no DNS setting.
It creates a TUN interface and assigns the tunnel CIDR; the kernel creates
the connected route. Because the interface is transient, even `SIGKILL`
cannot leave it behind: closing the last descriptor destroys the interface
and the kernel removes the connected route.

The milestone-5 laboratory proves this rather than assuming it. It first
observes `10.80.1.0/24` in client A, kills the client engine with `SIGKILL`,
then asserts both facts separately:

```text
tun0 is absent
10.80.1.0/24 is absent
```

This is not full-tunnel rollback. DNS, default routes, IPv6 policy, NAT,
and TCP MSS clamping remain deferred. The verified statement is narrower:
every network object created by the current client engine is rolled back by
process death.

## Observed validation

Observed on the Ubuntu development VM during this milestone implementation:

| Check | Observed result |
|---|---|
| `tests/deploy/install-rollback.test.sh` | PASS — reinstall idempotent, config preserved, v1→v2 upgrade and rollback correct |
| real release build + staged install | PASS — release tree contained the Rust, Go, and published .NET artifacts and installed into a temporary root |
| `tests/lab/session-e2e.sh --engine-restart` | PASS — agent exited on backend ID change; fresh registration reconciled empty state; pre-restart sessions became `failed`; leases released; old keys failed; fresh session passed HTTP |
| route rollback in that scenario | PASS — `SIGKILL` removed client `tun0` and the connected `10.80.1.0/24` route |
| final server metrics in the recorded restart run | `rx_pkts=5`, `tx_pkts=5`, `dropped_pkts=2`, `peers=1` |

Compilation, packet forwarding, and application connectivity are separate:
the release build compiled, packets were dropped/forwarded by the engine,
and the HTTP assertion was the exact body `hikablue-m4-ok` through the
post-restart session.

## Requirement map

| Requirement | Code / document | Verification |
|---|---|---|
| repeatable Debian installation | `deploy/debian/build-release.sh`, `install.sh` | `tests/deploy/install-rollback.test.sh` |
| upgrade and binary rollback | `deploy/debian/rollback.sh` | same offline test |
| service ownership | `deploy/debian/systemd/`, `service-scripts/` | shell syntax/static review + runbook |
| backend restart requires new session | `agent-go/cmd/vpn-agent/main.go`, `control-dotnet/VpnControl/Services/NodeChannelService.cs` | Go/.NET tests + `--engine-restart` lab |
| stream loss still reconciles | milestone-4 outbox/NodeChannel | `session-e2e.sh --drop` |
| network restoration | transient TUN ownership | `session-e2e.sh --engine-restart` |

## Glossary

- **AmbientCapabilities** — systemd setting that gives a non-root process a
  listed Linux capability; here the engine receives `CAP_NET_ADMIN`.
- **`backend_instance_id`** — random identifier of one running data-plane
  process. A restart must produce a new one.
- **CAP_NET_ADMIN** — Linux capability required for network-interface and
  routing administration.
- **connected route** — route the kernel creates automatically for the
  subnet assigned to an interface.
- **EnvironmentFile** — systemd file of `NAME=value` lines read before a
  service starts. The samples carry no secrets.
- **fencing** — rejecting commands or ACKs that belong to an old stream or
  backend instance.
- **idempotent install** — repeating the same installation leaves the same
  result instead of stacking duplicate changes.
- **`PartOf`** — systemd dependency linking a unit's stop/restart lifecycle
  to another unit.
- **release symlink** — `current`/`previous` indirection that makes upgrade
  and rollback atomic at the filesystem level.
- **`SIGKILL`** — uncatchable Unix termination signal; a good test of whether
  cleanup depends on careful shutdown or kernel ownership.
- **`--tun-name`** — engine flag selecting the TUN interface name.
- **`--mtu`** — engine flag selecting the tunnel interface maximum transmission unit.
- **`--metrics-group`** — engine flag choosing the local group allowed to read telemetry.
- **`GetSessions`** — engine control operation that reports the backend ID and live peer/key references.
- **`SO_PEERCRED`** — Unix-socket credential check used to identify the local peer process.
- **`SyncState`** — control-plane snapshot command carrying the desired peer set plus validation metadata.
- **systemd** — Linux service manager responsible for starting, restarting,
  and logging services.
- **transient TUN** — TUN interface owned by an open file descriptor; it is
  destroyed when the last descriptor closes.

## Exercise — trace ownership

A client reports that HTTP stopped after the exit engine restarted. Before
looking at packets, write the expected state of each row in the ownership
table. Then check: is the agent still running with the old backend identity?
Is the session `active` or `failed`? Is the lease still present? Did the
client create a fresh session?

Hint: an old client key cannot become valid merely because the process came
back. Check the failure reason `backend_restart_requires_new_session`.

## Exercise — predict rollback

In a temporary root, install release `v1`, edit the copied `engine.env`,
install `v1` again, then install `v2` and roll back. Predict the target of
`current` and `previous` after every command before reading the symlinks.
Check your answer with `tests/deploy/install-rollback.test.sh`; the script's
pass line means those transitions were observed in the test root.

## Explicit deferrals

- Automatic rekey/reprovisioning of the *same* client session after a
  backend restart is not implemented; a fresh API session is required.
- The client has no VPN CLI provisioning flow yet; the laboratory still
  bootstraps keys through protected files.
- DNS, default-route/full-tunnel rollback, IPv6 policy, NAT, MSS clamping,
  and WireGuard are milestone-6/full-tunnel scope.
- The installer does not manage PostgreSQL backups, certificate issuance,
  or secret rotation.

## Revisions

- `r0` — milestone-5 implementation: repeatable Debian release/install/upgrade/rollback automation, supervised backend restart detection, confirmed invalidation of pre-restart sessions, fresh-session recovery, and observed transient-TUN/route rollback in the isolated laboratory.

