# HiKablue

HiKablue is an educational VPN project for Debian. It follows a packet from a Linux client through a tunnel and an exit node, then builds the services that provision, monitor, and revoke that connection. The project uses Rust for packet processing, Go for node operations, and C# for the control plane.

**Status.** The architecture and learning roadmap are documented. The first implementation task is a raw IPv4 TUN/UDP tunnel between two Debian nodes in an isolated lab. Debian 13 is the reference environment, and Debian 12 compatibility is a verification target.

## Architecture

| Component | Role |
|---|---|
| Rust · `vpn-engine` and `vpn-cli` | Forward packets between TUN and UDP in the lab, configure the client tunnel, and expose engine metrics. |
| Go · `vpn-agent` | Configure networking on each exit node, apply peer changes, and report the applied state to the control plane. |
| C#/.NET · `vpn-control` | Authenticate clients, allocate tunnel IPs, manage session lifecycles, and store desired state in PostgreSQL. |

The client uses HTTPS to request a session. The agent and control plane communicate over gRPC with mutual TLS. On the lab node, the agent uses a Unix socket to manage the Rust engine. User traffic flows directly between the client and the exit node.

The custom UDP transport is for isolated lab exercises with test traffic. The roadmap specifies a WireGuard backend for deployments carrying real traffic.

## Implementation roadmap

1. Establish the monorepo and verify the toolchains. Use telemetry to practice integrating Rust, Go, and C#.
2. Build the raw TUN/UDP tunnel and verify a ping between virtual IPs and an HTTP request through the exit node. This is the **initial TUN implementation task**, which maps to the TUN milestone in the specification; it is separate from the telemetry milestone.
3. Add node registration, IP allocation, and session provisioning. Confirm that retrying a command leaves the same applied state.
4. Integrate the protected lab transport and verify two concurrent clients, individual session revocation, and fresh keys after a restart.
5. Verify deployment and recovery on Debian, then implement and test the WireGuard profile.

## The Journey of my VPN

The project will be accompanied by a living ebook in **English, Spanish, and Brazilian Portuguese**. Each implementation milestone updates all three editions in the same change set as the code. Chapters connect requirements to the implementation and tests, explain the decisions, and include a lab exercise. When a later chapter replaces an earlier approach, linked revision notes preserve the original reasoning and explain what changed.

Chapter 1 will trace a ping through TUN and UDP, explain the routing and privileges involved, and show how to reproduce the lab and verify cleanup.

## Project documents

- [Architecture specification and learning roadmap](vpn-debian-specification.md)
- [Repository instructions and ebook requirements](AGENTS.md)
- [Agent workflow and project rules](.agc/README.md)
- [Reusable agent skills](docs/skills/README.md)
- [License](LICENSE)
