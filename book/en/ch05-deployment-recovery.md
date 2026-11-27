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
Release and parent directories are normalized to service-readable
permissions during installation; environment files remain root-owned mode
`0600` and live secrets never belong in the release tree.

An upgrade is the same command with a new version. The old target is kept in
`/opt/hikablue/previous`. `deploy/debian/rollback.sh` swaps `current` back
to that target. Binary rollback is deliberately separate from database
rollback: PostgreSQL data is not versioned by these scripts, so an
irreversible migration requires a database backup or tested reverse
migration.

The offline proof is [`tests/deploy/install-rollback.test.sh`](../../tests/deploy/install-rollback.test.sh).
It uses a temporary root, proves reinstall idempotence, proves operator
configuration is preserved, proves service-readable permissions under a
restrictive umask, upgrades from `v1` to `v2`, rolls back, and
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
    --control-uid "$agent_uid" \
    --control-group "$AGENT_USER"
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
  also accepted by the socket policy); `--control-group` gives the socket
  file group ownership and mode `0660`. This separation matters: group
  membership opens the filesystem door, but every accepted connection is
  still checked by `SO_PEERCRED` against the numeric UID. The unprivileged
  engine can group-chown a file it owns because systemd places it in the
  agent group as a supplementary group; it does not need the privileged
  `chown` operation that made the first version of the unit fail.

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
because the empty desired state matches the fresh backend. The database work
is atomic: the backend identity change, terminal invalidation of commands
from the dead generation, and the reset snapshot are committed in one
transaction. Registration takes the same node-row `FOR UPDATE` lock used by
session creation, so a concurrent create cannot slip between those writes.

If the stream drops before the reset ACK, the next registration for the same
backend requeues that same reset row. It does not enqueue a replacement
snapshot that would resurrect the old peer list. While a reset row with
backend-reset metadata remains `pending` or `sent`, the node is ineligible
for new sessions. After the valid applied ACK, reset processing may enqueue fresh
`ProvisionPeer` commands for the existing sessions whose new key material was
prepared in the registration transaction. New session creation is also
allowed; a new session's command belongs to the new generation and is never
part of the pre-registration invalidation sweep.

The registration transaction prepares new key material before the reset is
applied, but does not install it in the empty backend:

```csharp
var (senderIndex, peerIndex) =
    SessionProvisioning.AllocateWireIndexes(usedIndexes);
var (egressKey, ingressKey) = SessionProvisioning.CreateDirectionalKeys();
session.LabSessionId =
    SessionProvisioning.AllocateLabSessionId(usedLabSessionIds);
nextKeyEpoch = checked(nextKeyEpoch + 1);
session.KeyEpoch = nextKeyEpoch;
session.EgressKey = egressKey;
session.IngressKey = ingressKey;
session.SenderIndex = senderIndex;
session.PeerIndex = peerIndex;
session.Status = SessionStatus.Pending;
```

The control plane generates distinct 32-byte directional keys, allocates
unused wire indexes and a new lab wire-session ID, and advances the node's
key epoch. It keeps the API session ID, virtual IP, owner token, expiration,
and lease unchanged. An expired session is moved to `revoking` instead of
being rekeyed; one already `revoking` remains on the removal path.

Only after the empty `SyncState` receives a valid applied ACK does the control
plane enqueue a `ProvisionPeer` for each still-eligible session. That command
installs the fresh material in the new backend. A valid `ProvisionPeer` ACK
moves that session from `pending` back to `active`. Until then it stays
`pending` and its lease remains reserved.

Sessions already `revoking`, or that expire before the reset ACK, become
`revoked` and release their leases only after that confirmed empty-state ACK.
This keeps a failed or delayed reset from looking like cleanup confirmation.

Revocation can also arrive after registration but before that ACK. In this
window, `RevokeSessionAsync` records `revoking` without enqueueing a separate
`RevokePeer`: the empty reset is already the removal operation. It checks for
the pending reset while holding the node-row lock, so the reset ACK and
revocation cannot make conflicting decisions.

The ACK handler must also refresh the session before acting on it:

```csharp
var resetSessionEntry = db.Entry(resetSession);
await resetSessionEntry.ReloadAsync(ct);
if (resetSessionEntry.State == EntityState.Detached)
{
    continue;
}
```

Line by line: `Entry` gets EF Core's tracked row; `ReloadAsync` replaces
possibly stale in-memory values with the committed database state; a detached
row no longer exists, so the loop skips it instead of acting on stale data.
The same refresh is used before applying provision and revoke ACK side
effects. `RevocationDuringBackendResetIsFinalizedByEmptyStateAck` proves that
the reset ACK sees the concurrent revocation, releases the lease, and queues
neither a duplicate revoke nor a fresh provision.

This is the important honesty boundary: `sent` is not application, and a
process restart is not cleanup confirmation. The control plane uses the
fresh backend's applied empty-state reconciliation as the confirmation that
old data-plane state is absent.

## Rekeying the same API session

The client manager follows the existing session instead of creating a
replacement. The state flow is:

```text
new backend ID
  -> registration stores fresh keys/indices/epoch and queues empty SyncState
  -> applied empty-state ACK
  -> fresh ProvisionPeer command
  -> applied provision ACK: session is active
  -> client observes the new key epoch and starts a replacement engine
```

`engine-rust/src/bin/vpn-cli.rs` implements this flow for the isolated lab:

- It creates a session over HTTPS using the configured CA, then keeps the
  session ID, owner token, virtual IP, and initial epoch for supervision.
- It polls that same session with `X-Subject` and `X-Session-Token`, and
  rejects a changed session ID or virtual IP. A temporary API failure is
  retried; a terminal session state is reported.
- If an active poll returns a `key_epoch` below the running engine's epoch,
  it treats the response as stale, keeps the current engine running, and
  polls again. A stale response is not a fatal supervision error that would
  cause `run()` to request session revocation. The unit test
  `stale_key_epoch_is_ignored_while_waiting_for_newer_epoch` covers the
  epoch decision.
- When the session becomes `pending`, it stops the existing client engine
  before waiting. Once the session is `active` at a higher epoch, it reads
  the new directional keys and wire indexes, writes protected key files, and
  starts a replacement engine. With `--netns`, the manager stays outside the
  client namespace to reach the loopback API while the child engine runs
  inside it.
- On shutdown it stops the child, removes its protected files, and requests
  session revocation. A failed cleanup or revocation request is surfaced.
- On Linux, it sets `PR_SET_PDEATHSIG` to `SIGKILL` on the child before
  `exec`. If `vpn-cli` is killed abruptly, the kernel terminates the engine
  too, closing the transient TUN. A parent-PID check closes the race where
  the manager exits before installing the signal.

The process hook is in `engine-rust/src/bin/vpn-cli.rs`:

```rust
let manager_pid = std::process::id() as libc::pid_t;
// Keep the transient TUN owned by the manager even after SIGKILL.
unsafe {
    command.pre_exec(move || {
        if libc::prctl(libc::PR_SET_PDEATHSIG, libc::SIGKILL as libc::c_ulong) != 0 {
            return Err(std::io::Error::last_os_error());
        }
        if libc::getppid() != manager_pid {
            return Err(std::io::Error::from_raw_os_error(libc::ESRCH));
        }
        Ok(())
    });
}
```

Line by line: the manager captures its PID before spawning; `pre_exec` runs
in the child before `exec`; `prctl` asks Linux to send `SIGKILL` when the
parent exits; `getppid` rejects the startup race. Normal shutdown still
removes the protected key files. A forced kill cannot run that cleanup, so
files may remain in the private state directory; the default `/run` location
is volatile, while a custom persistent location needs manual cleanup.

Client B intentionally keeps its old bootstrap keys in the restart exercise:
those keys must fail after the exit backend has restarted. Client A's HTTP
probe is expected to succeed only after the new provision ACK and engine
restart. This is lab-only provisioning, not a production client-authentication
flow.

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

## Encrypted systemd credentials

The sample `.env` files contain settings, not secrets. On Debian 12 or later
with `systemd-creds`, an operator supplies credential files out of band.
`deploy/debian/provision-credentials.sh` accepts a control source directory
containing `connection-string`, `heartbeat-token`, `admin-token`,
`session-token-secret`, `server.crt`, `server.key`, and `lab-ca.crt`; the node
directory contains `lab-ca.crt`, `node.crt`, and `node.key`. Each source
directory must be mode `0700`; each regular source file must be non-empty,
at most 1 MiB, and inaccessible to group and other. The control session-token
secret must contain at least 32 bytes.

For example:

```sh
sudo deploy/debian/provision-credentials.sh \
  --role control --source-dir /root/hikablue-control-credentials
sudo deploy/debian/provision-credentials.sh \
  --role node --source-dir /root/hikablue-node-credentials
```

The helper encrypts each file with `systemd-creds --with-key=host`, stores
versioned ciphertext under `/etc/hikablue/credentials/<role>/versions/`, and
atomically switches `current`, retaining `previous`. Plaintext source values
are not copied into that store or the release tree. systemd decrypts the
files into its private `CREDENTIALS_DIRECTORY`: the control-plane startup
maps control values into application configuration, and the agent wrapper
passes the node certificate paths to `vpn-agent`.

To rotate, supply a new protected source directory and rerun the command for
that role, then restart its service. To roll back, switch the links back and
restart:

```sh
sudo deploy/debian/provision-credentials.sh --role control --rollback
sudo systemctl restart hikablue-control.service
```

Repeat with `--role node` and restart `hikablue-agent.service` for node
credentials. Host-key encryption is machine-bound: ciphertext must be
reprovisioned on another host from the protected source values. The helper
stages and rotates supplied credentials; it does not issue certificates or
generate source secrets.

## PostgreSQL backup and restore

Before a control-plane upgrade or irreversible migration, create a custom
format backup in a new directory. Set `PGUSER`, `PGPASSFILE`, and, when
needed, `PGHOST` and `PGPORT`. Because the helpers run through `sudo`,
configure sudo policy to permit preserving these variables and pass them with
`--preserve-env`. If policy forbids that option, run the helper from an
authorized privileged environment with the same libpq settings.

```sh
sudo install -d -m 0700 /var/backups/hikablue
backup_dir="/var/backups/hikablue/before-upgrade-$(date -u +%Y%m%dT%H%M%SZ)"
sudo --preserve-env=PGUSER,PGPASSFILE,PGHOST,PGPORT \
  deploy/debian/backup-postgres.sh \
  --database vpn_control \
  --output-dir "$backup_dir"
```

The script uses libpq settings, such as a protected `PGPASSFILE`, rather than
putting a password in the command line. It creates a mode-`0700` directory
with a mode-`0600` `backup.dump`, metadata, and SHA-256 checksum. The checksum
detects accidental corruption; it is not an authenticity signature. Record
the exact `backup_dir` value for the later restore.

Restore into an existing, empty target database. The target can have a
different name from the source database recorded in the backup, which lets
the original database remain available until the restored copy is verified.
Create the target from `template0` using the database administrator identity
configured for libpq. `template0` provides a pristine schema but not the
source database's custom encoding or locale; if the source uses custom
settings, create the target with matching `--encoding`, `--lc-collate`, and
`--lc-ctype` values, or matching ICU provider/locale options. Because this
restore does not use `--create`, configure any required database owner,
privileges, comments, and database-level settings on the target separately.
Before creating or restoring the target, set `PGUSER` to the database
administrator role and configure protected authentication through `PGPASSFILE`
or an approved local identity; set `PGHOST` and `PGPORT` when needed. Do not
put passwords in command arguments.
Stop the control plane and every node's agent and engine first, and keep them
stopped until the restore is verified:

```sh
restore_database="vpn_control_restore"
: "${PGUSER:?set PGUSER to the database administrator role}"
createdb --username="$PGUSER" --template=template0 "$restore_database"
sudo systemctl stop hikablue-agent.service hikablue-engine.service
sudo systemctl stop hikablue-control.service
sudo --preserve-env=PGUSER,PGPASSFILE,PGHOST,PGPORT \
  deploy/debian/restore-postgres.sh \
  --database "$restore_database" \
  --backup-dir "$backup_dir" \
  --confirm-database "$restore_database"
# Verify the database, update the protected connection-string credential,
# then start the services.
sudo deploy/debian/provision-credentials.sh \
  --role control --source-dir /root/hikablue-control-credentials
sudo systemctl start hikablue-control.service
sudo systemctl start hikablue-engine.service hikablue-agent.service
```

Run each service command on the host that owns those units; all nodes must
remain stopped while restoring. The helper checks the confirmed target name,
private file modes, the single-entry checksum manifest, checksum, and archive
readability. It also checks PostgreSQL catalogs for objects that can survive
without belonging to a schema: large objects, subscriptions for this
database, foreign-data objects, nonstandard procedural languages, default
privileges, event triggers, publications, and database-specific settings.
Together with the schema and `public`-object checks, this rejects populated
targets before restore.
The helper does not use `--clean`: that option drops only objects present in
the archive. It restores in one transaction; PostgreSQL does not permit
`pg_restore --create` with `--single-transaction`. After verifying the target,
point the protected control-plane connection string at it and retain the old
database until the switch is confirmed. The offline test stubs PostgreSQL
clients, so it verifies guards and arguments, not a real database restore.

Restoring only PostgreSQL while agents or engines retain newer in-memory
state can make desired and applied state disagree. After the restore, start
the control plane before nodes. Each restarted engine reports a new backend
identity; after the empty-state ACK, eligible sessions receive fresh keys and
are provisioned again. The operator runbook does not automate backup
scheduling, remote replication, or certificate issuance.

## Historical deployment and recovery observations

The deployment and earlier restart results below were observed on the Ubuntu
development VM before the current same-session rekey, encrypted-credential,
and backup/restore changes. In particular, the restart result is from
baseline `c5772ccf7b60`, where recovery required a fresh API session; it is
historical evidence, not validation of current rekey, credential provisioning,
or real database restore behavior.

| Check | Observed result |
|---|---|
| `tests/deploy/install-rollback.test.sh` | PASS — reinstall idempotent, config preserved, v1→v2 upgrade and rollback correct |
| real release build + staged install | PASS — release tree contained the Rust, Go, and published .NET artifacts and installed into a temporary root |
| packaged systemd units in a clean Debian 12 VM | PASS — control/engine/agent active under systemd; PostgreSQL-backed REST session reached `active`; agent `GetSessions` saw the peer; revocation emptied the engine and released the lease |
| historical `tests/lab/session-e2e.sh --engine-restart` at `c5772ccf7b60` | PASS — agent exited on backend ID change; reset ACK failed old sessions and released leases; old keys failed; a fresh API session passed HTTP |
| route rollback in that historical scenario | PASS — `SIGKILL` removed client `tun0` and the connected `10.80.1.0/24` route |
| final server metrics in the historical restart run | `rx_pkts=5`, `tx_pkts=5`, `dropped_pkts=2`, `peers=1` |

In that historical run, compilation, packet forwarding, and application
connectivity were separate observations: the release built, the engine
dropped/forwarded packets, and the HTTP assertion returned the exact body
`hikablue-m4-ok` through the fresh session.

## Current milestone-5 verification status

These are observations from the review-fix changes on the Windows development
workstation. A check blocked before its assertions is not a pass.

| Check | Current result |
|---|---|
| `cargo fmt --all -- --check` from `engine-rust` | PASS — invoked with the user-local Cargo executable |
| `cargo check --all-targets --locked` from `engine-rust` | BLOCKED before project compilation because MSVC `link.exe` is missing |
| `SystemdCredentialConfigurationTests` | PASS — 3 tests, using the repository-pinned .NET 10 SDK |
| `BackendRestartRekeysSessionsAfterConfirmedEmptyStateAndKeepsTheirLeases`, `RevocationDuringBackendResetIsFinalizedByEmptyStateAck`, `ReaperDefersExpiredRevokeUntilPendingResetAck`, `UnicodeSessionTokenSecretUsesUtf8ByteLength` | BLOCKED before assertions because Testcontainers cannot reach Docker at `npipe://./pipe/docker_engine` |
| `cargo test --locked --bin vpn-cli stale_key_epoch_is_ignored_while_waiting_for_newer_epoch` | BLOCKED before project compilation because MSVC `link.exe` is missing |
| `bash -n` on `deploy/debian/restore-postgres.sh`, `tests/deploy/postgres-backup-restore.test.sh`, and `deploy/debian/backup-postgres.sh` | PASS |
| `bash tests/coverage.test.sh` | PASS on Git Bash with a temporary shell function mapping `python3` to Python 3.13 because the Windows Store alias failed; verifies disjoint lab-only spans and both Rust binaries |
| `bash tests/deploy/credentials.test.sh` and `bash tests/deploy/postgres-backup-restore.test.sh` | BLOCKED — Git Bash on NTFS could not enforce the required private POSIX modes |
| `bash tests/book/check-parity.sh` | PASS |
| `git diff --check` | PASS |
| `parent_death_signal_terminates_engine_child` and `tests/lab/session-e2e.sh --engine-restart` | NOT RUN — the test requires Linux and WSL is not installed |

The .NET tests were run with these PowerShell commands:

```powershell
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
& $dotnet test control-dotnet\VpnControl.Tests\VpnControl.Tests.csproj --no-restore --filter FullyQualifiedName~SystemdCredentialConfigurationTests
& $dotnet test control-dotnet\VpnControl.Tests\VpnControl.Tests.csproj --no-restore --filter 'FullyQualifiedName~BackendRestartRekeysSessionsAfterConfirmedEmptyStateAndKeepsTheirLeases|FullyQualifiedName~RevocationDuringBackendResetIsFinalizedByEmptyStateAck|FullyQualifiedName~ReaperDefersExpiredRevokeUntilPendingResetAck|FullyQualifiedName~UnicodeSessionTokenSecretUsesUtf8ByteLength'
```

Rust CLI compilation is unverified. Neither packet forwarding nor application
connectivity has been observed for same-session rekey. Reproduce the flow in
the authorized isolated Linux lab with
`tests/lab/session-e2e.sh --engine-restart`; do not direct test UDP at
external or production endpoints.

## Requirement map

| Requirement | Code / document | Verification |
|---|---|---|
| repeatable Debian installation | `deploy/debian/build-release.sh`, `install.sh` | `tests/deploy/install-rollback.test.sh` |
| upgrade and binary rollback | `deploy/debian/rollback.sh` | same offline test |
| service ownership | `deploy/debian/systemd/`, `service-scripts/` | shell syntax/static review + real systemd socket check |
| same-session rekey after backend restart | `control-dotnet/VpnControl/Services/NodeChannelService.cs`, `control-dotnet/VpnControl/Services/SessionProvisioning.cs`, `engine-rust/src/bin/vpn-cli.rs` | rekey integration test blocked before assertions by unavailable Docker; current isolated `--engine-restart` run is pending |
| revoke during a pending backend reset | `NodeChannelService.cs`, `SessionService.cs` | `RevocationDuringBackendResetIsFinalizedByEmptyStateAck` (blocked before assertions by unavailable Docker) |
| expiry during a pending backend reset | `LeaseReaperWorker.cs`, `NodeChannelService.cs` | `ReaperDefersExpiredRevokeUntilPendingResetAck` (blocked before assertions by unavailable Docker) |
| stale client key epoch | `vpn-cli.rs:compare_epoch` | `stale_key_epoch_is_ignored_while_waiting_for_newer_epoch` (blocked before project compilation because MSVC `link.exe` is missing) |
| stream loss still reconciles | milestone-4 outbox/NodeChannel | `tests/lab/session-e2e.sh --drop` |
| network restoration | transient TUN ownership and `vpn-cli` supervision | historical TUN cleanup observed; current rekey/route recreation awaits the Linux lab |
| engine lifetime follows the client manager | `engine-rust/src/bin/vpn-cli.rs` | Linux-only `parent_death_signal_terminates_engine_child` test; not run on this Windows workstation |
| encrypted systemd credentials | `deploy/debian/provision-credentials.sh`, systemd units, `control-dotnet/VpnControl/Security/SystemdCredentialConfiguration.cs` | 3 C# configuration tests pass; offline shell test blocked by NTFS mode semantics; no current real-systemd credential test |
| UTF-8 session-token secret length | `SystemdCredentialConfiguration.cs`, `SessionService.cs` | config test accepts a multibyte secret; `UnicodeSessionTokenSecretUsesUtf8ByteLength` is blocked before assertions by unavailable Docker |
| PostgreSQL backup and exact restore target | `deploy/debian/backup-postgres.sh`, `deploy/debian/restore-postgres.sh` | offline test checks helper executable bits, the complete CASE query, archive-list separation, and rejection of non-empty targets; blocked by NTFS mode semantics; no real PostgreSQL restore observed |

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
- **`--control-group`** — engine flag choosing the local group that owns the control socket file at mode `0660`; it does not replace UID authorization.
- **`key_epoch`** — monotonically advanced identifier for a session's current directional keys and wire state; a backend restart requires a fresh epoch.
- **`vpn-cli`** — lab-only client manager that polls an API session and replaces its engine after a new key epoch is active.
- **UTF-8** — byte encoding used to measure the minimum session-token secret; .NET `string.Length` counts UTF-16 code units instead.
- **`ReloadAsync`** — EF Core method that reloads a tracked entity from the database, replacing cached values.
- **`EntityState.Detached`** — EF Core state meaning an entity is no longer tracked; here it signals that the row disappeared before the ACK.
- **`CREDENTIALS_DIRECTORY`** — systemd-provided runtime directory containing decrypted credentials for one service invocation.
- **`systemd-creds`** — systemd utility used here to encrypt versioned credential files with a host-bound key.
- **checksum manifest** — SHA-256 file used to detect accidental archive corruption; it does not authenticate who produced the backup.
- **`psql`** — PostgreSQL command-line client used here for a read-only check that the restore target has no user objects.
- **`createdb`** — PostgreSQL command that creates the empty restore target.
- **`template0`** — pristine PostgreSQL template used to create a database without application objects copied from another template.
- **`pg_restore`** — PostgreSQL utility that reconstructs objects and data from a custom-format `pg_dump` archive.
- **`--single-transaction`** — `pg_restore` option that commits the restore as one transaction or rolls it back on error.
- **`--clean`** — `pg_restore` option that drops only archive-listed objects; it does not remove objects absent from the archive.
- **database locale** — the encoding and collation/character-classification settings of a database; a fresh restore target must match the source when those settings are customized.
- **large object** — PostgreSQL data stored outside a schema and tracked in `pg_largeobject_metadata`.
- **logical subscription** — a database's subscription to a logical-replication publication; the restore preflight rejects subscriptions attached to its target.
- **foreign-data objects** — foreign-data wrappers, servers, and user mappings configured in a database.
- **default privileges** — PostgreSQL rules that grant permissions automatically to newly created objects.
- **`PR_SET_PDEATHSIG`** — Linux `prctl` option that sends a selected signal to a process when its parent exits.
- **`prctl`** — Linux syscall used to configure process-specific behavior such as the parent-death signal.
- **`pre_exec`** — Rust process hook that runs in the child immediately before `exec`.
- **`getppid`** — Linux call that returns the current process's parent PID; the CLI uses it to detect a startup race.
- **`libc`** — Rust crate exposing C-library and Linux system-call interfaces used by the process hook.
- **systemd** — Linux service manager responsible for starting, restarting,
  and logging services.
- **transient TUN** — TUN interface owned by an open file descriptor; it is
  destroyed when the last descriptor closes.

## Exercise — trace same-session rekey

On the isolated Linux lab, run `tests/lab/session-e2e.sh --engine-restart`.
Before running it, predict the API session ID, virtual IP, owner token, and
lease before and after the backend restart. Then trace the new backend
registration, empty `SyncState` ACK, fresh `ProvisionPeer` ACK, and the
client's engine transition. The expected result is the same API session and
lease, a higher `key_epoch`, rejected stale keys on client B, and a successful
HTTP probe from client A only after the new provision is active. These are
exercise expectations, not an observed result for the current unverified
implementation.

## Exercise — predict rollback

In a temporary root, install release `v1`, edit the copied `engine.env`,
install `v1` again, then install `v2` and roll back. Predict the target of
`current` and `previous` after every command before reading the symlinks.
Check your answer with `tests/deploy/install-rollback.test.sh`; the script's
pass line means those transitions were observed in the test root.

## Exercise — rotate credentials and restore a database

Run `bash tests/deploy/credentials.test.sh` and
`bash tests/deploy/postgres-backup-restore.test.sh` in a Linux environment
with POSIX file modes. They stub `systemd-creds`, `pg_dump`, `pg_restore`,
and `psql`; they verify atomic version rotation/rollback, private-file checks,
database confirmation, checksum validation, rejection of a populated restore
target, and transactional restore arguments without using a host key or
database.

For an authorized isolated Debian lab, create a backup before a migration,
create a separate empty target from `template0`, stop the control plane and
every node service, and restore with the exact target confirmation. Verify
the copy before updating the protected connection-string credential and
starting the control plane before the nodes. Predict why an engine restart
triggers empty-state reconciliation and same-session rekey. Never use the
exercise against a production database or with credentials copied into the
repository.

## Explicit deferrals

- The same-session rekey code and lab client manager are implemented, but the
  current isolated Linux acceptance test has not run on this workstation.
- `vpn-cli` and raw UDP remain isolated-lab tools; production client
  authentication and production traffic are not supported.
- DNS, default-route/full-tunnel rollback, IPv6 policy, NAT, MSS clamping,
  and WireGuard remain deferred to the milestone-6/full-tunnel phase.
- Backup/restore and encrypted credential rotation are operator-invoked;
  automated backup scheduling, remote replication, certificate issuance,
  and source-secret generation remain out of scope.

## Revisions

- `r0` — milestone-5 implementation: repeatable Debian release/install/upgrade/rollback automation, supervised backend restart detection, confirmed invalidation of pre-restart sessions, fresh-session recovery, and observed transient-TUN/route rollback in the isolated laboratory.
- `r1` — review hardening: unprivileged group-owned control socket with UID authorization preserved; atomic backend-reset registration; reset reuse across pre-ACK reconnects; node exclusion from provisioning until the reset ACK.
- `r2` — installation hardening: service-readable release/directory permissions are normalized even under a restrictive umask.
- `r3` — validation hardening: clean Debian systemd evidence now covers control-plane startup plus REST-to-engine session provisioning and revocation.
- `r4` — same-session rekey after confirmed empty-state reconciliation, lab client epoch supervision, PostgreSQL backup/restore, and host-key-encrypted systemd credential staging. Current verification blockers and the pending isolated Linux acceptance run are recorded separately from historical results.
- `r5` — review fixes: serialize revocation with backend reset and reload session state on ACKs; require an empty PostgreSQL restore target; use UTF-8 byte length consistently for session secrets; terminate the Linux engine when its `vpn-cli` manager dies. Linux/PostgreSQL integration checks remain unverified in this Windows environment.
- `r6` — review fixes: ignore stale client key epochs without triggering session revocation; defer expiration revocation until a pending backend-reset ACK; complete the restore-target CASE expression and distinguish archive listing from restore attempts; mark deployment helpers executable; preserve libpq settings through `sudo`; clean root-owned lab state as root; include `vpn-cli` in coverage and test multiple lab-only spans. Current platform blockers remain listed above.

