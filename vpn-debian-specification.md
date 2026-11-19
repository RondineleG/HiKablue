# VPN on Debian — Implementation Specification and Learning Roadmap

**Revision 2 · Reference environment: Debian 13 · Compatibility to verify: Debian 12**

## 1. Goal and deliverable

Build a Linux client, an exit node, and a central service that can establish and revoke VPN sessions. The project teaches network programming in Rust, service operations in Go, and API development and persistence in C#/.NET 10.

The MVP is complete when an authenticated user can connect two Debian clients, reach a test service through the node, check session status, and revoke one session while the other stays connected. The lab should make it possible to repeat the demonstration from clean machines and observe how the system behaves after failures.

The custom UDP transport is restricted to an isolated lab with test data. Deployments intended to carry real traffic must use the WireGuard backend and meet separate operational acceptance criteria. The Rust client and Go agent still configure and operate the tunnel in that profile. A web dashboard, quotas, roaming, and performance targets come after the MVP. The first release carries IPv4 traffic only.

## 2. Responsibilities and topology

| Component | Responsibility | Main skill |
|---|---|---|
| `engine-rust` / `vpngine` | Experimental engine on the node. Reads TUN packets, validates their source against the peer, and forwards traffic between TUN and UDP. Exposes controls and metrics over a Unix socket. | Ownership, buffers, error handling, and asynchronous I/O with Tokio. |
| `engine-rust` / `vpn-cli` | Authenticates over HTTPS, requests a session, configures the client tunnel, checks status, and restores configuration on exit. Shares the codec with the engine. | CLI design, state machines, and Linux integration. |
| `agent-go` / `vpn-agent` | Opens the gRPC channel to C#, configures networking on the node, applies commands through the backend, and reports results. Owns the node's VPN network rules. | Interfaces, contexts, timeouts, concurrency, and reconciliation. |
| `control-dotnet` / `vpn-control` | Handles authentication, authorization, node selection, IP address management (IPAM), session lifecycle, auditing, and telemetry. PostgreSQL stores the desired state. | ASP.NET Core, EF Core, transactions, and asynchronous services. |

```mermaid
flowchart LR
    C["Rust client"] -->|HTTPS| P["C# control plane"]
    G["Go agent"] -->|gRPC with mTLS| P
    G -->|Unix socket| R["Rust engine on the node"]
    C -->|UDP| R
    R -->|TUN| K["Node kernel"]
```

Use a monorepo with `proto/`, `engine-rust/`, `agent-go/`, `control-dotnet/`, `deploy/debian/`, and `tests/e2e/`. Start with one ASP.NET Core project and one test project. Extract separate domain and data projects when the complexity warrants it. In Rust, start with binaries and a library in the same package. Pin the lab toolchain versions and document how to regenerate the Protobuf code.

## 3. Lab setup and bootstrap

- Set up two Debian client VMs and one Debian node VM on an isolated virtual network. Run the central service on another VM or on the development host, reachable over the management network. Put an HTTP test server on a separate network reachable from the node so you can verify forwarding without relying on the internet.
- Allocate one CIDR block per node. Keep VPN subnets, physical lab networks, and node allocations disjoint. For example, use `10.80.0.0/16` as the global pool, `10.80.1.0/24` for the first node, and `10.80.1.1` as its tunnel address. Reserve the network, broadcast, and gateway addresses.
- Run all three processes in the foreground for the first integration. Then move to dedicated service accounts. On the Rust/Go node, share the socket through a dedicated group, allow that group to traverse the directory, and set the socket mode to `0660`. Verify that a user outside the group cannot connect.
- Use a lab CA and a distinct identity for each node. The C# service maps the certificate to a registered node. Document credential issuance and rotation in the installation procedure. The client verifies the central service's HTTPS certificate. For the MVP, provision a test identity out of band and use short-lived tokens with session-level access control.
- Grant `CAP_NET_ADMIN` only for privileged network operations. Document which processes create the TUN interface, apply network rules, or need access to the interface. Limit packet captures and logs to test data.

## 4. Session lifecycle

1. The agent opens the mTLS stream, registers its identity, and reports the current backend generation. The control plane reconciles the node before marking it eligible for new sessions.
2. The client calls `POST /v1/sessions` with an `Idempotency-Key`. With WireGuard, it sends its public key and keeps its private key locally. The control plane selects an eligible node and reserves an IP in a transaction that also stores the `pending` session and the pending command.
3. The control plane returns `202 Accepted` with the session ID and status URL. A dispatcher sends `ProvisionPeer`. The agent validates the command, applies it, verifies the backend state, and returns `CommandAck`.
4. A valid ACK moves the session to `active`. An authenticated status request returns the configuration needed to connect. Responses containing session material include `Cache-Control: no-store`. The client configures the interface and starts sending traffic. The `active` state means provisioning succeeded. The CLI reports connectivity based on an actual probe through the tunnel.
5. `DELETE /v1/sessions/{id}` starts revocation. The node removes the peer and acknowledges the change. The control plane releases the IP only after it confirms removal for the relevant generation. If the node is unavailable, revocation remains pending and the IP remains reserved. The node must enforce session expiration locally even when it loses contact with the control plane.
6. If provisioning partially succeeds, the control plane schedules cleanup and holds the reservation until it confirms the peer is absent. On reconnect, the system reconciles desired and applied state. Expired and revoked sessions are excluded from new peer lists.

Persist the following states: `pending`, `active`, `revoking`, `revoked`, and `failed`. Expiration moves a session to `revoking`. The `failed` state is terminal once cleanup is confirmed. Record the reason separately. Response timeouts leave commands pending for reconciliation. Reusing the same idempotency key for the same user and payload returns the original session ID. Reusing the key with a different payload returns `409`.

## 5. Integration contracts

| Boundary | Minimum contract |
|---|---|
| Control-plane REST API | `POST /v1/sessions`, `GET /v1/sessions/{id}`, `DELETE /v1/sessions/{id}`, `GET /v1/nodes` (admin), `GET /health/live`, and `GET /health/ready`. Only the session owner or an authorized administrator can read or revoke a session. |
| Go ↔ C# gRPC | `NodeChannel(stream AgentMessage) returns (stream ControlCommand)`. The agent sends `NodeRegister`, `NodeHeartbeat`, and `CommandAck`. The control plane sends `ProvisionPeer`, `RevokePeer`, or `SyncState`. |
| Go ↔ Rust Unix socket | `/run/vpn/engine.sock`. A big-endian `uint32` length prefix followed by UTF-8 JSON. Methods are `AddPeer`, `RemovePeer`, `SyncPeers`, and `GetMetrics`. |
| Node backend | A Go interface for applying and removing peers, reconciling state, and reading applied state. The lab backend calls the Rust socket. The WireGuard backend configures peers with system tools. |

**Command delivery.** Each command carries a `command_id`, a `node_id`, the stream `generation`, the desired node `revision`, an application deadline, and a payload. The ACK echoes those identifiers and reports the applied revision and outcome. Deliver commands at least once and apply them idempotently. The control plane maintains a sequence of revisions per node and accepts ACKs only from the current stream. The agent processes revisions in order, ignores stale revisions, and requests a snapshot if it detects a gap. A snapshot is authoritative. It applies a complete revision and removes peers absent from it. If application fails partway through, report the reconciliation failure and reread the applied state before issuing an acknowledgment of success. Define every message type in `edge_orchestrator.proto` and reserve removed fields.

**Socket framing.** Handle one operation per connection in the MVP. Include a `request_id`, version, operation, and payload. The response echoes the ID and returns a status with either a result or an error code. Cap frames at 1 MiB, validate the length before allocating, and set read and write deadlines. Cap peers per node so the largest snapshot fits. Test fragmented reads, premature EOF, oversized frames, and unknown operations.

**Persistence.** Start with `nodes`, `sessions`, `ip_leases`, and `outbox`. Reserve IPs under a uniqueness constraint on the address, consistent with the disjoint pools. Store the session, lease, and command in one transaction. If two transactions compete for the last IP, one reserves it and the other receives an unavailable response. Use a UUID for the API session and a separate numeric ID for the lab protocol, unique among active sessions on the node.

## 6. Experimental Rust transport

For the first TUN ↔ UDP test, use one fixed peer inside the isolated network. Use `IFF_TUN | IFF_NO_PI`, a starting MTU of 1400, and explicit routes for lab IPs. Reuse buffers and measure copying and allocations before adopting a zero-copy target.

| Offset | Size | Field |
|---:|---:|---|
| 0 | 1 byte | Version in the high nibble and type in the low nibble. |
| 1 | 1 byte | Reserved, zero in version 1. |
| 2 | 4 bytes | Node-local session ID, big-endian. |
| 6 | 8 bytes | Per-direction counter, big-endian. |
| 14 | Variable | Encrypted payload. |
| End minus 16 | 16 bytes | AEAD tag. |

The header is **14 bytes**, all used as AAD. With an outer IPv4 header and no IP options, overhead is **58 bytes** (20 IP + 8 UDP + 14 custom header + 16 tag). With a 1400-byte inner MTU, the outer IP datagram reaches 1458 bytes. Verify the path MTU and test packets at the limit in the lab.

- Version 1 message types are `DATA` (inner IPv4) and `KEEPALIVE` (empty payload). Use the control plane to revoke or end sessions.
- Use an existing ChaCha20-Poly1305 library with a separate key for each direction. Form the 96-bit nonce from four zero bytes followed by the 64-bit counter. Each counter increases monotonically for its key and direction. End the session before the counter wraps.
- Advance the 64-position replay window only after successful authentication. The receiver state belongs to the same generation as the keys. Before injecting a packet into TUN, check that its inner source IP matches the IP assigned to the peer. Validate the version, bounds, message type, and IPv4 format.
- After an engine restart, invalidate lab sessions from the previous generation and reprovision them with fresh material. A restarted client requests a new session. Prevent `SyncPeers` from reinstalling old keys after the loss of counters or replay state. Keep ephemeral keys out of logs and track the generation explicitly during reconnection.
- With WireGuard, associate each peer's public key with its virtual IP. The control plane determines session authorization and duration. WireGuard handles its own transport keys.

## 7. Networking, deployment, and diagnostics

- Start with a split tunnel and route only the test network through it. For the full-tunnel profile, preserve a route to the UDP endpoint and access to the control API. Decide how to handle IPv6 and DNS before installing a default route. For an IPv4-only profile, verify that IPv6 traffic outside the tunnel is blocked while full tunneling is active.
- The agent enables forwarding and applies specific forward rules and NAT scoped to the node's IP pool and configured egress interface. Block client-to-client traffic by default in the MVP environment. Restrict changes to agent-owned rules. Validate syntax and apply updates transactionally without flushing the entire firewall.
- The client tracks the network objects it changes and restores routes and DNS on exit. Test recovery after abrupt process termination and ensure that one instance cannot remove another instance's objects. Test MSS clamping if TCP is affected by MTU problems.
- Deliver systemd units, a sample configuration with no secrets, database migrations, and an installation, upgrade, and rollback runbook. Use structured logs with `node_id`, `session_id`, and `command_id`. Expose byte counters, active peer counts, backend failures, and time since the last heartbeat.
- When a tunnel carries no traffic, check the active session and matching ACK, the client route, UDP and TUN counters, node forwarding, and the return path, in that order. Measure recovery and throughput in the lab before setting targets.

## 8. Implementation and learning roadmap

| Milestone | What to build in order | What you can demonstrate | What you should be able to explain |
|---|---|---|---|
| 0 · Foundation | Set up the monorepo, pin toolchain versions, and add a build and a minimal test in each language. | `cargo test`, `go test ./...`, and `dotnet test` pass from a clean checkout. | Which binary runs on each machine and why. |
| 1 · Telemetry | Rust counts UDP bytes and exposes `GetMetrics`. Go reads the metric with a deadline. C# receives a local HTTP heartbeat and lists nodes from memory. | A 5-byte datagram increases the counter by five. Stopping the engine changes the observed state, and restarting it restores metrics collection. | Buffer ownership in Rust, cancellation in Go, and the lifetime of an injected dependency in C#. |
| 2 · TUN | Add the Rust interface, test routes, and forwarding for one peer. | Ping works both ways between virtual IPs, and packet captures show traffic on the expected interface. | The difference between an inner IP packet and an outer UDP datagram. |
| 3 · Control plane | Add PostgreSQL, IPAM, session states, and an outbox in C#. Replace Go's HTTP heartbeat with gRPC over mTLS and apply commands to a test backend. | Two concurrent requests for the last IP produce one reservation. Resending an applied command leaves the same state. | What the database transaction protects and what requires acknowledgment across processes. |
| 4 · End-to-end session | Integrate the encrypted Rust backend, CLI, peer operations, local expiration, and reconciliation. | Two clients can reach the test service. The engine drops tampered packets, replays, and packets with invalid source IPs. Revoking one client leaves the other connected. | AAD, counters, state after a crash, and when an IP can be released. |
| 5 · Deployment and recovery | Automate Debian installation and test process restarts, stream loss, and network restoration. | Installation is repeatable. Engine restart requires a new session. Revocation takes effect after reconnect, and route changes roll back. | Which process owns each setting and how to trace failures. |
| 6 · WireGuard | Implement the agent backend and client configuration. Verify split tunneling. Address DNS and IPv6 before enabling full tunneling. | Repeat provisioning, traffic forwarding, and revocation with WireGuard on two machines. Verify that the client's private key stays local. | Which responsibilities belong to your code and which WireGuard handles. |

### First implementation session

1. **Rust.** Make the UDP receiver listen on loopback and accumulate `rx_bytes`. Send a datagram of known size. Then expose `GetMetrics` over the Unix socket. Exercise — explain why a single socket read can return only part of a frame, and write a test that reproduces it.
2. **Go.** Implement `FetchMetrics(ctx)` and decode the engine response. Exercise — simulate a socket that accepts the connection and then stays silent. Confirm that the deadline stops the read and the next collection still runs.
3. **C#.** Implement `POST /internal/heartbeats` on the local interface and `GET /v1/nodes`. Track `lastSeen` and engine health separately. Exercise — simulate a missing heartbeat and an engine failure, then compare the results. Remove the HTTP heartbeat once the gRPC stream is in place.
4. **Integration.** Run all three processes and record the 5-byte increment, outage, and recovery in a commit. For each milestone, write the acceptance criterion before coding, implement the smallest observable change, and reproduce one failure before closing the milestone.

## 9. Final MVP acceptance checklist

- [ ] A clean checkout builds and tests all three languages using reproducible instructions.
- [ ] The control plane rejects a stream with an invalid node identity. Users cannot access sessions owned by other users.
- [ ] Two clients connect through the CLI and reach the private test service. The service sees their traffic arrive through the node.
- [ ] Repeated requests and commands preserve the expected state. A lost ACK followed by reconnection converges to the expected revision.
- [ ] Revocation and expiration remove access. The control plane reuses the IP only after the node confirms removal. The other client stays connected.
- [ ] Restarting the lab engine forces new provisioning. Packets from an earlier generation never reach TUN.
- [ ] Disconnect and recovery procedures restore client network objects. Logs trace the flow without exposing credentials.

## References

- [Linux kernel TUN/TAP documentation](https://kernel.org/doc/html/latest/networking/tuntap.html)
- [RFC 8439, ChaCha20 and Poly1305](https://www.rfc-editor.org/rfc/rfc8439.html)
- [WireGuard protocol and cryptography](https://www.wireguard.com/protocol/)
