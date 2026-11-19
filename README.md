# HiKablue

**An educational VPN, built in public and explained in three languages.**

HiKablue follows one packet end to end: from a Linux client's TUN interface, through a tunnel, across an exit node, and back — then builds the services that provision, monitor, and revoke that connection. Every milestone ships working code *and* the chapter that explains it, so the repository doubles as a course on systems networking in Rust, Go, and C#.

- **Rust** — packet processing where latency and memory control matter (`vpn-engine`, `vpn-cli`)
- **Go** — node operations where concurrency stays simple (`vpn-agent`)
- **C#/.NET** — the control plane where identity and state live (`vpn-control`)
- **Lab target** — Debian 13 (reference), Debian 12 (compatibility check), isolated `netns` environments

## Architecture

```
        ┌──────────────┐   HTTPS    ┌─────────────────┐
        │   client     │───────────▶│  vpn-control    │  sessions, IPs,
        │  vpn-cli     │            │   (C#/.NET)     │  revocation, state
        └──────┬───────┘            └────────┬────────┘
               │                             │ gRPC + mTLS (planned)
               │                      ┌──────▼─────────┐
               │   tunneled traffic   │   vpn-agent    │
               └─────────────────────▶│     (Go)       │
                    client → exit     └──────┬─────────┘
                                             │ Unix socket
                                      ┌──────▼─────────┐
                                      │  vpn-engine    │  TUN ↔ UDP
                                      │    (Rust)      │  + GetMetrics
                                      └────────────────┘
```

| Component | Role | Status |
|---|---|---|
| `engine-rust/` | Forwards IPv4 packets between TUN and UDP; serves `GetMetrics` on a hardened Unix socket (`0660` + `--metrics-group`, length-prefixed JSON envelope, deadlines, connection cap) | Implemented |
| `agent-go/` | Fetches engine metrics with context deadlines; will apply node configuration and report applied state | Metrics fetcher implemented |
| `control-dotnet/` | In-memory node registry behind loopback-only HTTP (`POST /internal/heartbeats`, `GET /v1/nodes`); real auth and PostgreSQL arrive with the gRPC control plane | Lab service implemented |
| `proto/` | gRPC `NodeChannel` definitions | Placeholder |

The custom UDP transport exists **only for isolated lab exercises** with test traffic. The roadmap specifies a WireGuard backend before real traffic is carried.

## Milestones

Keep this table current: every milestone change set updates the status and links the PR that delivered it.

| # | Milestone | Status |
|---|---|---|
| 0 | Foundation — monorepo + toolchains | **Delivered** — Rust, Go, and C# all run real components |
| 1 | **Telemetry** — metrics socket, Go fetcher, C# heartbeat registry | **Implemented on `feat/telemetry` ([PR #6](https://github.com/RondineleG/HiKablue/pull/6)), merge pending** |
| 2 | Raw TUN ↔ UDP tunnel | **Delivered** in [PR #4](https://github.com/RondineleG/HiKablue/pull/4) — ping verified both ways on Debian 12/13 (delivered out of order, ahead of milestone 1) |
| 3 | Control plane — registration, IP allocation, sessions (gRPC `NodeChannel` + mTLS + PostgreSQL) | Pending |
| 4 | Protected transport — AEAD/WG, concurrent clients, revocation, key rotation | Pending |
| 5 | Deployment, recovery, WireGuard profile | Pending |

Deferred inside milestone 2: IPv4 forwarding, NAT, MSS clamping, split/full-tunnel routing, and the HTTP-through-the-exit-node acceptance — they belong to the full-tunnel phase.

## Validate locally

One command runs the whole gate — commit conventions, diff safety, shellcheck, fmt/clippy/tests/build for Rust, Go, and .NET, book parity, and the lab integration tests:

```bash
bash tests/checks.sh          # everything except privileged lab steps
bash tests/checks.sh --lab    # full gate: TUN/UDP ping + telemetry E2E + resource safety
```

The lab steps use `sudo` only where TUN creation and namespaces require it, run in `mktemp`-isolated directories, refuse to touch resources they did not create, and clean up after themselves.

## The Journey of my VPN

The living ebook ships in **English, Spanish, and Brazilian Portuguese**, updated in the same change set as each milestone. Chapters map requirements to code and tests, explain the decisions and tradeoffs, and end with a reproducible lab exercise. Revision notes preserve the history when later chapters replace earlier approaches.

| Chapter | en | es | pt-BR |
|---|---|---|---|
| 1 — Raw TUN ↔ UDP | [read](book/en/ch01-raw-tun-udp.md) | [leer](book/es/ch01-raw-tun-udp.md) | [ler](book/pt-BR/ch01-raw-tun-udp.md) |
| 2 — Telemetry loop | [read](book/en/ch02-telemetry-loop.md) | [leer](book/es/ch02-telemetry-loop.md) | [ler](book/pt-BR/ch02-telemetry-loop.md) |

## Project documents

- [Architecture specification and learning roadmap](vpn-debian-specification.md)
- [Repository instructions and ebook requirements](AGENTS.md)
- [Agent workflow and project rules](.agc/README.md)
- [Reusable agent skills](docs/skills/README.md)
- [License](LICENSE)
