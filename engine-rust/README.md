# vpn-engine — Phase 1 raw TUN ↔ UDP forwarder

`vpn-engine` moves raw IPv4 packets between a Linux TUN interface and a UDP socket.
It is the experimental engine from `vpn-debian-specification.md` milestone 2 (TUN),
restricted to **test traffic in an isolated, authorized lab**. There is no
encryption, authentication, NAT, or default-route handling in this phase.

## Build

```sh
cd engine-rust
cargo check
cargo test
cargo build --release
```

The binary is `target/release/vpn-engine`. Rust edition 2021; dependencies are
Tokio, clap, and libc.

## Privileges

Creating the TUN interface and applying its address/MTU require
`CAP_NET_ADMIN`. Run `vpn-engine` as root (`sudo`) in the lab, or grant the
capability to the binary (`setcap cap_net_admin+ep`). It also calls `ip(8)`
from `iproute2` to configure the interface, so that package must be installed.

## Two-node lab (Debian 12/13 VMs)

Topology: an isolated underlay `192.168.56.0/24` (e.g. a VirtualBox host-only
network), node A at `192.168.56.11`, node B at `192.168.56.12`. Tunnel subnet
`10.80.1.0/24`: A is `10.80.1.1`, B is `10.80.1.2`. Each node gets one fixed
peer.

On **both** nodes:

```sh
sudo deploy/debian/lab-node-setup.sh   # checks tun module + iproute2
```

Node A (server — learns its single peer):

```sh
sudo ./vpn-engine server \
  --bind 192.168.56.11:51820 \
  --tun-cidr 10.80.1.1/24 \
  --peer 10.80.1.2
```

Node B (client — fixed remote endpoint):

```sh
sudo ./vpn-engine client \
  --bind 192.168.56.12:51820 \
  --remote 192.168.56.11:51820 \
  --tun-cidr 10.80.1.2/24 \
  --peer 10.80.1.1
```

Acceptance check — ping the virtual IPs in **both** directions. Ping the
client side **first**: the server has no remote endpoint until it receives a
valid packet from the configured `--peer`, so `ping 10.80.1.1` on B is what
lets A learn where to send replies.

```sh
# on B (first)                # on A (after B's ping)
ping 10.80.1.1                ping 10.80.1.2
```

Observe inner packets vs outer datagrams:

```sh
sudo tcpdump -ni tun0 icmp                 # inner IPv4: 10.80.1.x
sudo tcpdump -ni eth0 udp port 51820       # outer UDP:  192.168.56.x
```

Cleanup on each node — the interface is transient and dies with the
vpn-engine process, so the script normally only confirms it is gone:

```sh
sudo deploy/debian/lab-node-cleanup.sh               # verify only
sudo deploy/debian/lab-node-cleanup.sh --lab-owned   # remove a leftover
                                                     # lab interface you
                                                     # know is ours
```

## How it works

- `server` binds UDP and learns one remote endpoint only after a datagram
  passes IPv4 structural validation **and** its inner source IP equals
  `--peer` and its inner destination is this node's own virtual IP. Both
  checks are re-applied on **every** datagram, so the pinned endpoint
  cannot inject packets from or toward other virtual IPs.
  **This pins the peer; it is *not* authentication.** Any sender that can
  reach the UDP port and spoof `--peer`'s virtual IP would still pass —
  real authentication only arrives with the AEAD transport milestone.
  Acceptable for an isolated lab, not for production.
- `client` uses a connected UDP socket, so only the configured remote can
  inject datagrams — and each one is still dropped unless its inner
  destination is the client's own virtual IP.
- Buffers: two fixed 2048-byte buffers are reused for every packet;
  `--mtu` is bounded to 2048 and defaults to 1400.
- Packets failing structural validation (truncated, wrong version, bad IHL,
  total-length mismatch, bad header checksum, oversized) are dropped.
  Inbound datagrams are read via `recvmsg(MSG_TRUNC)`, so a datagram larger
  than the buffer is detected by its real length and dropped rather than
  injected as a truncated packet.
- Per-datagram UDP errors (e.g. `ECONNREFUSED` while the peer is down) are
  logged and dropped — they do not tear down the tunnel.
- The engine refuses to reuse a TUN interface that already exists — the
  kernel check is atomic via `IFF_TUN_EXCL` on `TUNSETIFF` (a friendly
  error is printed when the name is already taken) — so a foreign or
  persistent `tun0` is never reconfigured. Interface names are limited to
  `[A-Za-z0-9._-]{1,15}` and `ip(8)` is invoked by absolute path with
  `Command::arg` (no shell, no PATH lookup).
- `SIGINT`/`SIGTERM` break the `tokio::select!` loop, drop the TUN descriptor
  (removing the transient interface), and exit 0.
- Routes: assigning `--tun-cidr` installs only the connected route for the lab
  subnet. No default route, NAT, or firewall changes are made.

## Verified in this change

| Check | Where | Result |
|---|---|---|
| `cargo check` / `cargo test` / `cargo build --release` | Ubuntu 22.04 host, Rust 1.99.1 | 0 warnings, 28 tests pass |
| `tests/lab/netns-ping.sh` (two `ip netns` nodes, veth pair) | Ubuntu 22.04 host | PASS: 3/3 pings each way, TUN gone after shutdown |
| Ping both directions | Debian 13 (trixie) containers on a private Docker net | 3/3 replies each way |
| Ping both directions | Debian 12 (bookworm) containers, same topology | 3/3 replies each way |
| SIGINT / SIGTERM exit 0 + TUN cleanup | Debian 13 containers | observed |
| Client survives ping while server is down, then recovers | Debian 13 containers | process stayed up, ping resumed |
| Second `vpn-engine` on an existing `tun0` | Debian 13 container | refused with `already exists` error |
| 2500-byte datagram, forged IPv4 header TL=2048 | Debian 13 server | dropped; nothing written to `tun0` |

Unverified: nothing in the Phase 1 scope. The documented VM walkthrough is
equivalent to the container lab; only the underlay addresses differ.

Deferred acceptance: an HTTP request through the exit node stays unchecked —
it needs kernel forwarding and NAT, postponed to the full-tunnel phase
(recorded in `.agc/IMPLEMENTATION.md`). The ping checks above are the
delivered verification for this phase.
