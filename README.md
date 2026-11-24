# HiKablue

**An educational VPN, built in public and explained in three languages.**

HiKablue follows one packet end to end: from a Linux client's TUN interface, through a tunnel, across an exit node, and back — then builds the services that provision, monitor, and revoke that connection. Every milestone ships working code *and* the chapter that explains it, so the repository doubles as a course on systems networking in Rust, Go, and C#.

- **Rust** — packet processing where latency and memory control matter (`vpn-engine`, `vpn-cli`)
- **Go** — node operations where concurrency stays simple (`vpn-agent`)
- **C#/.NET** — the control plane where identity and state live (`vpn-control`)
- **Lab target** — Debian 13 (reference), Debian 12 (compatibility check), isolated `netns` environments

## Architecture

```
┌────────────┐   HTTPS    ┌──────────────────┐
│  client    │───────────▶│   vpn-control    │  sessions, IPs,
│  vpn-cli   │            │    (C#/.NET)     │  revocation, state
└─────┬──────┘            └────────▲─────────┘
      │                          │ gRPC + mTLS NodeChannel
      │ UDP (test traffic)  ┌────┴──────┐
      │                     │ vpn-agent │
      ▼                     │   (Go)    │
┌────────────┐   Unix sock  └───────────┘
│ vpn-engine │◀──────────────────┘
│   (Rust)   │  GetMetrics + peer control
└─────┬──────┘
      │ TUN
      ▼
  node kernel
```

Tunneled traffic flows client → engine (UDP in the lab) — the agent stays on the control path.

| Component | Role | Status |
|---|---|---|
| `engine-rust/` | IPv4 TUN ↔ UDP, hardened `GetMetrics` Unix socket, multi-peer ChaCha20-Poly1305 sessions with per-direction keys, endpoint pinning and control socket lifecycle | M4 validated locally |
| `agent-go/` | Metrics-fetcher binary, `vpn-agent -serve` orchestration loop and `pkg/orchestrator` (mTLS channel, register/heartbeat/ACK, fencing) | M4 validated locally |
| `control-dotnet/` | PostgreSQL/EF Core, IPAM, session API, SPIFFE mTLS `NodeChannel`, outbox and lease reaper; loopback-only lab REST/legacy heartbeat remains | M4 session validation delivered locally |
| `proto/` | gRPC `NodeChannel` definitions and generated Go/C# bindings | Delivered in M3 |

The custom UDP transport exists **only for isolated lab exercises** with test traffic. The roadmap specifies a WireGuard backend before real traffic is carried.

## Milestones

Keep this table current: every milestone change set updates the status and links the PR that delivered it.

| # | Milestone | Status |
|---|---|---|
| 0 | Foundation — monorepo + toolchains | **Delivered** — Rust, Go, and C# all run real components; versions pinned (`rust-toolchain.toml`, `global.json`, `go.mod` + `.go-version`) |
| 1 | **Telemetry** — metrics socket, Go fetcher, C# heartbeat registry | **Delivered** — [PR #6](https://github.com/RondineleG/HiKablue/pull/6); hardened in [PR #8](https://github.com/RondineleG/HiKablue/pull/8) (registry snapshots, heartbeat token auth, payload limits, node expiry, coverage-gate self-tests) |
| 2 | Raw TUN ↔ UDP tunnel | **Delivered** in [PR #4](https://github.com/RondineleG/HiKablue/pull/4) — ping verified both ways on Debian 12/13 (delivered out of order, ahead of milestone 1) |
| 3 | Control plane — PostgreSQL, IPAM, session states, outbox; gRPC `NodeChannel` over mTLS | **Components delivered** — contracts + lab CA + IPAM design in [PR #11](https://github.com/RondineleG/HiKablue/pull/11); EF Core persistence, IPAM and `/v1/sessions` in [PR #12](https://github.com/RondineleG/HiKablue/pull/12); `NodeChannel` with SPIFFE mTLS, outbox dispatcher, lease reaper and the Go `pkg/orchestrator` client in [PR #13](https://github.com/RondineleG/HiKablue/pull/13). Binary wiring, HTTP heartbeat retirement and cross-process gRPC acceptance remain deferred to M4 |
| 4 | End-to-end session — encrypted Rust backend, CLI, peer operations, local expiration, reconciliation | **Validation delivered locally** — Phase 2 data-plane crypto in [PR #14](https://github.com/RondineleG/HiKablue/pull/14); this change set wires `vpn-agent` to mTLS gRPC and the Rust control socket, provisions two concurrent clients, preserves `revoking` leases across channel loss, and passes both [session laboratory](tests/lab/session-e2e.md) scenarios |
| 5 | Deployment and recovery — Debian install automation, restarts, stream loss, route rollback | Pending |
| 6 | WireGuard — agent backend + client configuration, split tunneling | Pending |

Deferred inside milestone 2: IPv4 forwarding, NAT, MSS clamping, split/full-tunnel routing, and the HTTP-through-the-exit-node acceptance — they belong to the full-tunnel phase.

## Validate locally

One command runs the whole gate — commit conventions, diff safety, shellcheck, Rust fmt + clippy + tests + release build, Go gofmt + vet + tests, .NET build + tests, book parity, line coverage ≥ 90% per component (`tests/coverage.sh`, rule TEST-02 — Rust lines in `src/` excluding test code and `// coverage: lab-only` spans, Go statements via `-coverpkg=./...`, C# lines via coverlet), and the lab integration tests (`--lab` also builds and runs the agent binary):

```bash
bash tests/checks.sh          # everything except privileged lab steps
bash tests/checks.sh --lab    # full gate: TUN/UDP ping + telemetry E2E + resource safety + session E2E
```

The lab steps use `sudo` only where TUN creation and namespaces require it, run in `mktemp`-isolated directories, refuse to touch resources they did not create, and clean up after themselves.

Run the gate on a `fix/<slug>` or `feat/<slug>` branch. On `main`, the
branch validator deliberately fails; it does not indicate a compilation or
test failure. Do not override `BRANCH_NAME` to bypass this guard. The
`Quality gate` GitHub Actions workflow is configured for PRs targeting
`main`: it checks Go formatting and vet, builds .NET, and runs tests with
coverage ≥90% per component (Rust, Go, and .NET with PostgreSQL via
Testcontainers/Docker). Existing workflows handle Rust fmt/clippy/tests/build,
shell lint, ebook parity, commit validation, and diff safety separately.
For [PR #15](https://github.com/RondineleG/HiKablue/pull/15), GitHub Actions
jobs did not start due to a billing lock; verification was local. The
workflow does not validate privileged forwarding or application connectivity.

## The Journey of my VPN

The living ebook ships in **English, Spanish, and Brazilian Portuguese**, updated in the same change set as each milestone. Chapters map requirements to code and tests, explain the decisions and tradeoffs, and end with a reproducible lab exercise. Revision notes preserve the history when later chapters replace earlier approaches.

| Chapter | en | es | pt-BR |
|---|---|---|---|
| 1 — Raw TUN ↔ UDP | [read](book/en/ch01-raw-tun-udp.md) | [leer](book/es/ch01-raw-tun-udp.md) | [ler](book/pt-BR/ch01-raw-tun-udp.md) |
| 2 — Telemetry loop | [read](book/en/ch02-telemetry-loop.md) | [leer](book/es/ch02-telemetry-loop.md) | [ler](book/pt-BR/ch02-telemetry-loop.md) |
| 3 — Control plane | [read](book/en/ch03-control-plane.md) | [leer](book/es/ch03-control-plane.md) | [ler](book/pt-BR/ch03-control-plane.md) |
| 4 — Data-plane crypto | [read](book/en/ch04-data-plane-crypto.md) | [leer](book/es/ch04-data-plane-crypto.md) | [ler](book/pt-BR/ch04-data-plane-crypto.md) |

## Project documents

- [Architecture specification and learning roadmap](vpn-debian-specification.md)
- [Repository instructions and ebook requirements](AGENTS.md)
- [Agent workflow and project rules](.agc/README.md)
- [Reusable agent skills](docs/skills/README.md)
- [License](LICENSE)
