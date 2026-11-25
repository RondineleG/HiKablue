# Debian deployment and rollback runbook

This directory automates the **isolated laboratory** deployment shape for
milestone 5. It installs versioned binaries, systemd units, and editable
sample configuration. It does not route real traffic, configure NAT, start
services, or provision private keys.

## Contents

- `build-release.sh` — builds the Rust engine, Go agent, and published .NET
  control plane into one versioned release tree.
- `install.sh` — installs that tree under `/opt/hikablue/releases/<version>`,
  copies units/samples, creates service users on a real host, and atomically
  moves `/opt/hikablue/current`.
- `rollback.sh` — atomically moves `current` back to the release recorded in
  `/opt/hikablue/previous`.
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
given. The directory contains `VERSION`, `BUILD_INFO`, `bin/`, `control/`,
`systemd/`, and `config/`. It must not contain private keys, tokens, or a
live database connection string.

## Install or upgrade

Copy the release directory to the target Debian host, then run:

```bash
sudo deploy/debian/install.sh --release /path/to/hikablue-<version>
```

Repeat installation is intentional and safe: an identical version is reused,
while the same version with different content is rejected instead of being
mutated in place. A different version becomes the new `current`; the former
`current` is recorded as `previous`. Services are installed but not started.

The real install creates these service users when absent:
`hikablue-engine`, `hikablue-agent`, and `hikablue-control`. The engine unit
runs with only `CAP_NET_ADMIN`; the agent and control plane receive no
network-admin capability. The agent's expiry state directory is
`/var/lib/hikablue/agent-state`, owner-only (`0700`).

For offline validation or tests, use a temporary root. This touches no host
users, services, routes, or firewall state:

```bash
deploy/debian/install.sh --release /path/to/release --root "$(mktemp -d)" --no-systemctl
bash tests/deploy/install-rollback.test.sh
```

## Configure secrets and certificates

The samples in `config/` are copied to `/etc/hikablue/*.env` only when those
files do not already exist. They contain placeholders and empty values, not
credentials. Provision real values out of band:

1. Issue the lab CA, node certificate, node private key, and HTTPS server
   certificate using the documented lab PKI process or your test CA.
2. Install them under `/etc/hikablue/pki/` with owner-only private-key
   permissions. Never commit these files.
3. Edit `/etc/hikablue/control.env` as root with the database connection
   string and random tokens. Keep it mode `0600`.
4. Edit `engine.env` and `agent.env` for the node's lab addresses and SPIFFE
   node identity.

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

Before upgrading the control plane, back up PostgreSQL with your normal lab
backup procedure. Apply EF Core migrations with `dotnet ef database update`
or the service startup path used by the deployment. Roll back the database
only from a backup or an explicitly tested reverse migration.

## Failure tracing order

When a tunnel carries no traffic, inspect in this order:

1. control-plane session status and failure reason;
2. outbox command state and ACK (`sent` is not application);
3. `journalctl -u hikablue-agent.service` for registration, fencing, and
   backend-instance messages;
4. `GetSessions`/`GetMetrics` through the engine Unix sockets;
5. client route and TUN existence;
6. node forwarding and return-path configuration.

A backend restart is not transparent to existing lab sessions. Old key epochs
and counters are gone with the process. After the fresh backend confirms its
empty state through reconciliation, pre-restart pending/active sessions are
marked `failed` with `backend_restart_requires_new_session`, already-revoking
sessions complete as `revoked`, leases are released, and clients must create
fresh sessions with fresh keys.

## Explicit limits

- NAT, MSS clamping, DNS, full tunneling, and production routing remain
  deferred to the full-tunnel/WireGuard phases.
- The custom UDP engine remains lab-only; do not use these units for real
  traffic.
- The installer does not manage PostgreSQL, issue certificates, or rotate
  secrets.
