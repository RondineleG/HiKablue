# Debian deployment and rollback runbook

This directory automates the **isolated laboratory** deployment shape for
milestone 5. It installs versioned binaries, systemd units, and editable
sample configuration. It does not route real traffic, configure NAT, start
services, issue certificates, or generate private keys.

## Contents

- `build-release.sh` — builds the Rust engine, Go agent, and published .NET
  control plane and client CLI into one versioned release tree.
- `install.sh` — installs that tree under `/opt/hikablue/releases/<version>`,
  copies units/samples, creates service users on a real host, and atomically
  moves `/opt/hikablue/current`.
- `rollback.sh` — atomically moves `current` back to the release recorded in
  `/opt/hikablue/previous`.
- `backup-postgres.sh` / `restore-postgres.sh` — create and transactionally
  restore private, checksummed PostgreSQL custom-format backups.
- `provision-credentials.sh` — encrypts operator-issued credentials with
  `systemd-creds`, rotates versioned credential sets, and rolls them back.
- `systemd/` — service units for the engine, node agent, and control plane.
- `config/` — secret-free samples. Copy them once, then edit the root-owned
  live files outside git.
- `lab-node-setup.sh` / `lab-node-cleanup.sh` — the older milestone-2 lab
  helpers; they do not install the milestone-5 services.

## Build

From a clean checkout with the pinned Rust, Go, and .NET toolchains:

```bash
deploy/debian/build-release.sh --force
```

The output is `artifacts/release/hikablue-<commit>` unless `--output` is
given. The directory contains `VERSION`, `BUILD_INFO`, `bin/` (including
`vpn-engine`, `vpn-cli`, and the backup/credential tools), `control/`,
`systemd/`, and `config/`. It must not contain private keys, tokens, or a live
database connection string.

## Install or upgrade

Copy the release directory to the target Debian host, then run:

```bash
sudo deploy/debian/install.sh --release /path/to/hikablue-<version>
```

Repeat installation is intentional and safe: an identical version is reused,
while the same version with different content is rejected instead of being
mutated in place. A different version becomes the new `current`; the former
`current` is recorded as `previous`. Release and parent directories are
normalized to service-readable permissions even under a restrictive umask;
live `.env` files remain root-owned mode `0600`. Services are installed but
not started.

The real install creates these service users when absent:
`hikablue-engine`, `hikablue-agent`, and `hikablue-control`. The engine unit
runs with only `CAP_NET_ADMIN`; the agent and control plane receive no
network-admin capability. The engine unit also joins `hikablue-agent` as a
supplementary group only so the unprivileged engine can group-own its local
Unix sockets. Both sockets are mode `0660`; socket ownership grants the IPC
connection, while the control socket still authorizes every peer with
`SO_PEERCRED` and the numeric agent UID. No unprivileged `chown` is needed.
The agent's expiry state directory is `/var/lib/hikablue/agent-state`,
owner-only (`0700`).

For offline validation or tests, use a temporary root. This touches no host
users, services, routes, or firewall state:

```bash
deploy/debian/install.sh --release /path/to/release --root "$(mktemp -d)" --no-systemctl
bash tests/deploy/install-rollback.test.sh
```

## Encrypted systemd credentials

The samples in `config/` are copied to `/etc/hikablue/*.env` only when those
files do not already exist. They contain non-secret settings only. Issue
certificates and create the source credential directories out of band; each
directory must be mode `0700`, and each credential file mode `0600`.

The control source directory must contain exactly the required inputs:
`connection-string`, `heartbeat-token`, `admin-token`,
`session-token-secret`, `server.crt`, `server.key`, and `lab-ca.crt`.
The node source directory contains `lab-ca.crt`, `node.crt`, and `node.key`.
The installer never creates these values. The control token secret must be
at least 32 bytes.

On Debian 12 or later with `systemd-creds` available, encrypt and stage each
role on the host that will run it:

```bash
sudo deploy/debian/provision-credentials.sh \
  --role control --source-dir /root/hikablue-control-credentials
sudo deploy/debian/provision-credentials.sh \
  --role node --source-dir /root/hikablue-node-credentials
```

`--with-key=host` makes each encrypted credential set specific to that
machine. The script keeps encrypted versions under
`/etc/hikablue/credentials/<role>/versions/` and atomically switches the
`current` symlink; it does not copy plaintext inputs into the store. systemd
decrypts credentials into its private runtime credential directory.
`hikablue-control.service` loads the control files and the .NET startup code
maps them into configuration; the agent wrapper passes the node certificate
paths from `CREDENTIALS_DIRECTORY` to `vpn-agent`.

To rotate credentials, prepare a new private source directory and rerun the
same command for that role, then restart its service. The previous encrypted
version is retained. Roll back by switching the links back and restarting:

```bash
sudo deploy/debian/provision-credentials.sh --role control --rollback
sudo systemctl restart hikablue-control.service
```

Repeat with `--role node` and restart `hikablue-agent.service` for node
credentials. Host-key-encrypted files cannot be moved to another host; issue
or copy the protected source values through the approved secret process and
reprovision there. Never commit source credentials or the encrypted store.

## Start, inspect, and stop

After configuration:

```bash
sudo systemctl enable --now hikablue-engine.service hikablue-agent.service
# on the control-plane host:
sudo systemctl enable --now hikablue-control.service

journalctl -u hikablue-engine.service -f
journalctl -u hikablue-agent.service -f
journalctl -u hikablue-control.service -f
```

The agent unit requires and is part of the engine unit. An engine restart
therefore restarts the agent; the agent also exits by itself when it observes
a changed engine `backend_instance_id`, so `Restart=always` starts it with
the fresh identity even if the engine is replaced outside systemd.

Offline installation and real service startup are separate verifications.
`tests/deploy/install-rollback.test.sh` proves file layout and symlink
transitions in a temporary root; it does not prove that systemd can start
the units. On a real Debian host, verify startup separately: both units
must be `active`, the engine and agent sockets must be group-owned by
`hikablue-agent` with mode `0660`, and a shell running as `hikablue-agent`
must receive successful `GetMetrics` and `GetSessions` responses.

Stop services explicitly:

```bash
sudo systemctl stop hikablue-agent.service hikablue-engine.service
sudo systemctl stop hikablue-control.service   # control-plane host
```

## Rollback

```bash
sudo deploy/debian/rollback.sh
sudo systemctl restart hikablue-engine.service hikablue-agent.service
# or, on the control-plane host:
sudo systemctl restart hikablue-control.service
```

`rollback.sh` only moves the release symlink and records the displaced
release as the new rollback target. It does not downgrade PostgreSQL data.
Database migrations must therefore be backward-compatible within the supported
rollback window; if a migration is irreversible, restore the database backup
taken before upgrade instead of only rolling back binaries.

Before upgrading the control plane, create a private custom-format backup.
The commands use libpq connection settings and a protected `PGPASSFILE` (or
an equivalent local service identity); do not put database passwords in
arguments. Create the parent once and record the unique directory path for
the later restore:

```bash
sudo install -d -m 0700 /var/backups/hikablue
backup_dir="/var/backups/hikablue/before-upgrade-$(date -u +%Y%m%dT%H%M%SZ)"
sudo deploy/debian/backup-postgres.sh \
  --database vpn_control \
  --output-dir "$backup_dir"
```

The output directory must not already exist. The script writes a mode-`0600`
archive, metadata, and SHA-256 checksum in a mode-`0700` directory. Restore
requires an existing target database, the exact `--confirm-database` value,
and a valid archive/checksum:

```bash
sudo systemctl stop hikablue-agent.service hikablue-engine.service
sudo systemctl stop hikablue-control.service
sudo deploy/debian/restore-postgres.sh \
  --database vpn_control \
  --backup-dir "$backup_dir" \
  --confirm-database vpn_control
sudo systemctl start hikablue-control.service
sudo systemctl start hikablue-engine.service hikablue-agent.service
```

Run the stop/start commands on the hosts that own those services; keep all
nodes stopped while restoring the database. Reuse the exact `backup_dir`
value recorded when creating the backup. `pg_restore` validates the archive
before applying it and uses one transaction with `--clean --if-exists`.
Restoring only PostgreSQL while agents or engines retain newer in-memory
state can make desired and applied state disagree. Starting a node after a
restore changes its backend identity; the control plane reconciles the empty
backend and rekeys eligible sessions before they become active again. A
checksum detects accidental corruption, not a maliciously replaced archive
and manifest. Apply EF Core migrations with `dotnet ef database update` or
the service startup path used by the deployment. Roll back the database only
from a backup or an explicitly tested reverse migration.

## Backend-reset ordering

Backend registration, old-generation command invalidation, and reset snapshot
enqueue are one PostgreSQL transaction under the same node-row `FOR UPDATE`
lock used by session creation. Reconnecting the same backend before the reset
ACK reuses the pending reset row rather than enqueueing another snapshot that
contains old sessions. While that row is `pending` or `sent`, the node is
ineligible for new sessions. The reset snapshot is empty; it carries the
pre-restart session IDs only as completion metadata. After its valid applied
ACK, each non-expired pending/active session receives new directional keys,
wire indexes, `key_epoch`, and lab wire-session ID. Its API session ID,
virtual IP, owner token, and lease remain unchanged. A new `ProvisionPeer`
command is then ACKed before the session returns to `active`. Sessions already
revoking or expired become `revoked` and release their lease only after this
confirmed empty-state ACK.

## Failure tracing order

When a tunnel carries no traffic, inspect in this order:

1. control-plane session status and failure reason;
2. outbox command state and ACK (`sent` is not application);
3. `journalctl -u hikablue-agent.service` for registration, fencing, and
   backend-instance messages;
4. `GetSessions`/`GetMetrics` through the engine Unix sockets;
5. client route and TUN existence;
6. node forwarding and return-path configuration.

A backend restart is not a transparent continuation of the old data plane:
old key epochs and counters are gone with the process. After the fresh backend
confirms its empty state, eligible API sessions are reprovisioned with fresh
keys while retaining their IDs and leases. The `vpn-cli` client polls the
owner-authenticated status endpoint, stops its old engine when it sees a
pending/new epoch, and starts a new engine only after the session is active.
Clients not managed by `vpn-cli` must obtain and install the new keys through
their authorized client flow; stale keys cannot authenticate packets.

## Explicit limits

- NAT, MSS clamping, DNS, full tunneling, and production routing remain
  deferred to the full-tunnel/WireGuard phases.
- The custom UDP engine remains lab-only; do not use these units for real
  traffic.
- The installer does not manage PostgreSQL, issue certificates, or generate
  plaintext source credentials. PostgreSQL backup/restore and encrypted
  credential staging are explicit operator commands.
