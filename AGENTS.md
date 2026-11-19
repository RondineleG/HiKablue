# The Journey of my VPN

Build an educational VPN in Rust, Go, and C#. Treat learning and verification as deliverables alongside working software. These rules apply to every implementation milestone.

Before editing, inspect the repository and read the [architecture specification](vpn-debian-specification.md). Share a short plan, then proceed autonomously within the requested scope. Choose the layout, dependencies, testing tools, and publishing workflow that fit the project. Explain decisions that affect correctness, routing, privileges, or repeatability.

## Definition of done

1. **Maintain a living ebook titled The Journey of my VPN.** Use this exact title in English, Spanish, and Brazilian Portuguese. Version the editable sources alongside the code. Provide three complete, directly readable editions with tables of contents and stable chapter IDs. Any generated EPUB, PDF, or website must be reproducible from those sources.
2. **Develop the code and book together.** Update all three editions in the same change set as each implementation milestone. Keep chapter IDs, code references, commands, expected results, and revision notes aligned. Add a simple chapter parity check when creating the first chapter. A milestone is complete when the code, verification evidence, and all three editions agree.
3. **Teach every implemented requirement.** Map requirements to code, tests, and chapter sections. Explain the problem, prerequisites, packet or control flow, implementation choices, tradeoffs, and failure cases. Walk through the relevant code. Include reproducible commands, expected results, troubleshooting, and at least one hands-on exercise with a hint and a way to check the answer. Record deferrals and their reasons.
4. **Preserve the learning history.** When a later chapter replaces an earlier approach, preserve the original reasoning and add a revision note linking forward. In the later chapter, explain what changed, why, and what evidence supported it. Update these cross-links and notes in all three editions. Pin historical commands to the release or commit they apply to and point to current instructions.
5. **Distinguish expectations from observations.** Record what actually ran, where, and with what outcome. Run independent checks if a prerequisite is missing. Identify unverified behavior and give exact commands to reproduce the missing checks. Report compilation, packet forwarding, and application-level connectivity separately.

## Initial implementation task

When asked to implement Phase 1, implement only the monorepo scaffold and the raw point-to-point IPv4 TUN/UDP tunnel on Debian 12/13. This maps to the TUN milestone in the architecture specification. The telemetry milestone remains separate. Run raw UDP only in an isolated, authorized lab using test traffic.

- Use `engine-rust/`, `agent-go/`, `control-dotnet/`, `proto/`, and `deploy/debian/` as defaults. Adapt to existing structure when useful and document the layout. Keep Go, C#, and Protobuf as placeholders for this task.
- Build `vpngine` in Rust edition 2021 with Tokio. Expose server and client modes, a UDP bind address, a virtual IPv4 CIDR, and the client's remote endpoint. Separate CLI, Linux TUN access, and forwarding into readable modules.
- Open `/dev/net/tun` with `IFF_TUN | IFF_NO_PI`. Configure `tun0`, use a configurable inner MTU with a default of 1400, set the supplied CIDR, and bring it up. Document privileges and support cancellable I/O.
- Forward raw IPv4 both ways. The server learns one remote endpoint only after structural validation and a match against the configured peer virtual IP. Explain the limits of this validation. The client uses a connected UDP socket. Reuse fixed 2048-byte buffers and bound the supported MTU accordingly. Reject malformed, truncated, and oversized packets. Handle SIGINT and SIGTERM by releasing the TUN descriptor and exiting successfully after orderly cleanup.
- Provide idempotent Debian setup and cleanup helpers. Scope IPv4 forwarding changes, NAT, and mandatory bidirectional TCP MSS clamping to the lab configuration and project-owned rules. Default to split tunneling. For opt-in full tunneling, preserve the UDP endpoint's IPv4 host route through the original path and document route checks and restoration.
- In the engine README, give exact two-node lab commands for ping and an HTTP request to a controlled endpoint. For full tunneling, use a controlled lab endpoint that reports the observed source IP.
- Run `cargo check`, `cargo test`, and `cargo build --release` and aim for zero warnings. Test packet validation and peer selection. Where available, verify forwarding in both directions, repeat setup, and test cleanup on SIGINT and SIGTERM. Report results by Debian version.

Write Chapter 1 in all three editions. Trace a ping round trip and explain TUN, UDP, inner and outer addresses, MTU, peer learning, buffer reuse, the route to the UDP endpoint, forwarding, NAT, privileges, and cleanup. Include a requirement-to-test map, exact commands, and an exercise that predicts and then verifies where inner packets and outer datagrams appear in a lab capture. Add an appropriate historical note when a later chapter supersedes raw UDP.

Use Conventional Commits. In the completion report, link all three editions, summarize decisions worth studying, separate observed and unverified checks, and give a short code-reading and reproduction sequence.
