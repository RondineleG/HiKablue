# Initial TUN Implementation Task

This checklist preserves the repository's initial raw-tunnel task. It maps to the TUN milestone in `vpn-debian-specification.md`; the telemetry milestone remains separate. The task is not authorized by this document alone: proceed only when the user explicitly requests this implementation scope.

**Status: delivered (verified).** This task ran ahead of milestone 1 (Telemetry) by explicit request — an out-of-order execution, not a reordering; milestone identifiers are unchanged and Telemetry remains pending. The raw TUN ↔ UDP loop is lab-verified on Debian 12/13. Within-scope deferrals are recorded inline below.

The specification's milestone table and first-implementation-session text currently describe an earlier telemetry sequence. Do not silently treat that sequence as equivalent to this TUN task or reorder the roadmap; surface the conflict if the requested work depends on which milestone comes first.

## Scope and architecture

- Implement only the monorepo scaffold and a raw point-to-point IPv4 TUN/UDP tunnel on Debian 12/13. Raw UDP is restricted to test traffic in an isolated, authorized lab.
- Use `engine-rust/`, `agent-go/`, `control-dotnet/`, `proto/`, and `deploy/debian/` as defaults. Adapt to existing structure when useful and document it. Keep Go, C#, and Protobuf as placeholders for this task.
- Build `vpn-engine` in Rust edition 2021 with Tokio. Expose server and client modes, a UDP bind address, a virtual IPv4 CIDR, and the client's remote endpoint. Separate CLI, Linux TUN access, and forwarding into readable modules.

## Tunnel and lifecycle

- Open `/dev/net/tun` with `IFF_TUN | IFF_NO_PI`. Configure `tun0`, use a configurable inner MTU defaulting to 1400, set the supplied CIDR, and bring the interface up. Document privileges and support cancellable I/O.
- Forward raw IPv4 in both directions. The server learns one remote endpoint only after structural validation and a match against the configured peer virtual IP; explain that this does not authenticate the peer. The client uses a connected UDP socket.
- Reuse fixed 2048-byte buffers and bound the supported MTU accordingly. Reject malformed, truncated, and oversized packets. Handle SIGINT and SIGTERM by releasing the TUN descriptor and exiting successfully after orderly cleanup.
- Provide idempotent Debian setup and cleanup helpers. Scope IPv4 forwarding, NAT, and mandatory bidirectional TCP MSS clamping to lab configuration and project-owned rules. Default to split tunneling. For opt-in full tunneling, preserve the UDP endpoint's IPv4 host route through the original path and document route checks and restoration.
  - **Deferred by explicit user scope (Phase 1 delivery, PR #4):** forwarding, NAT, MSS clamping, split/full tunnel selection, and route checks/restoration are postponed to the full-tunnel/app-connectivity phase. The raw point-to-point delivery ships only the idempotent setup/cleanup helpers scoped to project-owned objects.
- In the engine README, give exact two-node lab commands for ping and an HTTP request to a controlled endpoint. For full tunneling, use a controlled lab endpoint that reports the observed source IP.
  - **Deferred with the items above:** the HTTP request needs forwarding/NAT and belongs to the full-tunnel phase; the README ships the ping commands.

## Verification

- Run `cargo check`, `cargo test`, and `cargo build --release`; aim for zero warnings. Test packet validation and peer selection.
- Where the environment permits, verify forwarding in both directions, repeat setup, and test cleanup on SIGINT and SIGTERM. Report results by Debian version. Do not imply live-network verification if it was not run in the authorized lab.
- Commands are not available until the relevant project scaffold exists. Do not add failing placeholder recipes to make this checklist appear executable.

## Chapter 1 acceptance

Write Chapter 1 in all three ebook editions. Trace a ping round trip and explain TUN, UDP, inner and outer addresses, MTU, peer learning, buffer reuse, the route to the UDP endpoint, forwarding, NAT, privileges, and cleanup. Include a requirement-to-test map, exact reproducible commands, and an exercise that predicts and verifies where inner packets and outer datagrams appear in an isolated lab capture. Add an appropriate historical note when a later chapter supersedes raw UDP.
