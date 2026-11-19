<!-- chapter-id: ch01-raw-tun-udp -->
# Chapter 1 — A Ping Crosses the Tunnel

*The Journey of my VPN* · Chapter `ch01-raw-tun-udp` · Phase 1: raw TUN ↔ UDP

We now have a tunnel that carries a single raw IPv4 packet per UDP datagram
between two lab nodes. No encryption, no handshake, no peers beyond the one we
configured. That is the point: before we protect anything, we watch an entire
ping travel through the machine and learn to tell the two addresses it carries
apart.

> **Lab boundary.** Raw UDP is for test traffic inside an isolated,
> authorized lab only (`.agc/RULES.md` LAB-01). Nothing in this chapter
> belongs on a production path.

## The two stories every packet tells

Look at the lab we will use:

```text
   node A 192.168.56.11          node B 192.168.56.12
   ┌──────────────────┐          ┌──────────────────┐
   │  ping            │          │  ping            │
   │  tun0 10.80.1.1  │          │  tun0 10.80.1.2  │
   │  vpngine server  │          │  vpngine client  │
   │  UDP :51820 ◄────┼──UDP/51820──► UDP :51820    │
   └──────────────────┘          └──────────────────┘
      underlay: 192.168.56.0/24 · tunnel: 10.80.1.0/24
```

Every ping is wrapped twice in the address sense:

- The **inner packet** is the IPv4 packet the applications write — source
  `10.80.1.2`, destination `10.80.1.1`. It exists only as bytes: inside `tun0`
  on each node and as the *payload* of our UDP datagrams on the wire.
- The **outer datagram** is the UDP packet the kernels route — source
  `192.168.56.12:51820`, destination `192.168.56.11:51820`. It is what the lab
  switches and `eth0` actually see.

Confusing these two is the classic beginner mistake with tunnels, so Chapter 1
trains your eye to keep them apart.

## TUN: a network card that ends in a file

`ping` does not talk to `vpngine`. It talks to the kernel's IP stack, which
routes `10.80.1.0/24` out of `tun0`. `tun0` is a **TUN** device: a virtual
network interface whose other end is a file descriptor, `/dev/net/tun`.
`vpngine` opens it with `IFF_TUN | IFF_NO_PI` — layer 3, and *no packet
information* header — so every `read()` returns exactly one raw IPv4 packet
and every `write()` injects exactly one. Creating the interface and applying
its address and MTU need `CAP_NET_ADMIN`; that is why the lab runs `vpngine`
with `sudo`.

## Tracing one ping, B → A

1. On B, `ping 10.80.1.1` builds an ICMP echo request with source `10.80.1.2`.
   The kernel's connected route for `10.80.1.0/24` — installed when vpngine set
   `--tun-cidr 10.80.1.2/24` — sends it to `tun0`, where it becomes bytes on
   vpngine's descriptor.
2. B's loop reads the 84-byte IPv4 packet, validates it (version, IHL, total
   length, header checksum, size ≤ MTU), and sends it as the payload of one
   UDP datagram to the fixed remote `192.168.56.11:51820`. The client socket is
   *connected*, so nothing but that endpoint can ever arrive.
3. On A, the server receives the datagram, re-runs the same validation, then
   checks the **inner** source IP against `--peer 10.80.1.2`. It matches, so A
   *learns* B's outer endpoint `192.168.56.12:51820` and writes the inner
   packet to `tun0`. The kernel delivers it to the local stack, which answers
   the echo request.
4. The reply, `10.80.1.1 → 10.80.1.2`, enters A's `tun0`. This time the server
   already knows its peer, so the packet goes back in a UDP datagram to
   `192.168.56.12:51820`, B validates it, writes it to `tun0`, and `ping`
   reports one round trip.

> **Peer learning is not authentication.** The check is *structural
> validation plus an inner-source-IP match* — input validation, nothing more.
> Any sender that can reach A's UDP port and spoof `10.80.1.2` inside would
> pass. Milestone 4's AEAD transport is what will make this safe.

## MTU and reusable buffers

The TUN MTU defaults to 1400 (`--mtu`), bounded by the two fixed 2048-byte
buffers the loop allocates once and reuses forever — the hot path does no
per-packet heap allocation. A malformed read can never exceed the buffer, and
any packet larger than the configured MTU is dropped as oversized. Two
hardening details live here: inbound datagrams are read with
`recvmsg(MSG_TRUNC)` so a datagram bigger than the buffer is detected by its
real length and dropped instead of injected as a truncated prefix, and a
per-datagram send/receive error (like `ECONNREFUSED` while the peer is down)
is logged and skipped rather than tearing down the tunnel. Why 1400 and
not the usual 1500? Later phases wrap the inner packet in a 14-byte header and
a 16-byte AEAD tag inside the outer UDP/IPv4 datagram (58 bytes of overhead);
1400 was chosen so the *outer* packet still fits a 1500-byte path. Phase 1 omits
those bytes, but keeping the MTU now means no retuning later.

## Routes, forwarding, and what we deliberately did not do

The only route Phase 1 adds is the **connected route** the kernel installs from
the TUN address: `10.80.1.0/24 dev tun0`. Two consequences:

- The outer datagrams travel the **underlay's own route** — node A is reached
  because `192.168.56.11` is on the LAN, not because anything was rerouted.
- *Forwarding* (the kernel relaying a packet between interfaces) and *NAT*
  (rewriting addresses) are absent by design: each node only originates and
  terminates tunnel traffic. They, along with default-route changes, firewall
  automation, encryption, and dynamic peers, are deferred to later milestones —
  see `vpn-debian-specification.md` sections 6–7.

## Privileges and cleanup

- `CAP_NET_ADMIN` is needed to open `/dev/net/tun`, set the MTU, and assign the
  address. Run the engine as root in the lab.
- `SIGINT` and `SIGTERM` break the `tokio::select!` loop, drop the TUN
  descriptor — which removes the transient interface — and exit with code 0.
- `deploy/debian/lab-node-setup.sh` prepares a node idempotently (tun module,
  `iproute2`); `lab-node-cleanup.sh` deletes `tun0` only when it carries a
  `10.80.0.0/16` lab address, so another VPN's interface is never removed.
  For the same reason, the engine refuses to open a TUN name that already
  exists instead of reconfiguring someone else's interface.

## Run it yourself

Build and run the two-node lab exactly as in `engine-rust/README.md`
(server on A, client on B), then:

```sh
ping 10.80.1.1        # on B — expect replies
ping 10.80.1.2        # on A — expect replies
sudo tcpdump -ni tun0 icmp                # inner: 10.80.1.x
sudo tcpdump -ni eth0 udp port 51820      # outer: 192.168.56.x
```

A containerized equivalent of this lab (two Debian containers on a private
Docker network, `NET_ADMIN` plus `/dev/net/tun`) is what was used for the
verification below; the commands are identical except for the underlay
addresses (`172.30.0.11/12` instead of `192.168.56.11/12`).

## Requirement → test map

| Requirement | Code | Check |
|---|---|---|
| `IFF_TUN \| IFF_NO_PI`, configurable CIDR, MTU 1400 default | `src/tun.rs` (`Tun::open`, `configure_interface`) | `ip addr show tun0` in lab; CLI defaults in `src/cli.rs` |
| Forward IPv4 both directions, fixed peer per node | `src/forward.rs` `run()` | ping both ways (verified, below) |
| Reusable buffers, bound MTU | `src/forward.rs` `BUFFER_SIZE`, `MAX_MTU` | `rejects_zero_and_huge_mtu` |
| Drop malformed IPv4 | `src/packet.rs` `validate_ipv4`; `src/forward.rs` `recv_datagram` (MSG_TRUNC) | 8 unit tests: truncation, version, IHL, length, checksum, oversize; forged oversized datagram dropped in lab |
| Server peer learning by inner source IP | `src/forward.rs` `classify_server_packet` | 4 deterministic cases: learn on match, drop divergent source, forward pinned peer, drop other endpoint; observed `learned peer endpoint` log |
| Clean shutdown on SIGINT/SIGTERM | `src/forward.rs` signal arms | observed exit 0 on Debian 13 |

## Observed results vs. expectations

| Check | Environment | Observed |
|---|---|---|
| `cargo check`, `cargo test`, `cargo build --release` | Ubuntu 22.04, Rust 1.99.1 | 0 warnings; 23/23 tests pass |
| Ping `10.80.1.1` from B and `10.80.1.2` from A | Debian 13 (trixie) containers, `hikablue-lab` net | 3/3 replies each direction |
| Same pings | Debian 12 (bookworm) containers | 3/3 replies each direction |
| SIGINT / `docker stop` (SIGTERM) | Debian 13 containers | exit code 0; `tun0` gone with the process |
| `tcpdump` on `eth0` | Debian 13 node A | `172.30.0.12.51820 > 172.30.0.11.51820: UDP, length 84` |
| `tcpdump` on `tun0` | Debian 13 node A | `10.80.1.2 > 10.80.1.1: ICMP echo request` |
| Ping while server down, then server returns | Debian 13 node B | client stayed up (per-datagram error dropped); ping recovered |
| `vpngine` on existing `tun0` | Debian 13 node B | refused (`already exists`); foreign interface untouched |
| 2500-byte datagram, forged header TL=2048 | Debian 13 node A | dropped by real length; nothing on `tun0` |

Everything in the Phase 1 scope was verified. Application-level connectivity
through a node (e.g. HTTP to a third host) is *out of scope*: it needs
forwarding and NAT, which are deferred.

## Exercise — predict the capture before you look

On node A, run both captures while B pings once:

```sh
sudo tcpdump -c1 -ni eth0 udp port 51820 &
sudo tcpdump -c1 -ni tun0 icmp &
ping -c1 10.80.1.2        # run this on B? No — predict first.
```

1. Which line will `eth0` print: addresses `10.80.1.x` or `192.168.56.x`?
2. Which line will `tun0` print?
3. How many UDP datagrams does one `ping -c1` produce, and why?

*Hint:* the TUN device carries IP packets; the NIC carries whatever IP decided
to put on the wire. *Check:* if `eth0` shows only the underlay addresses and
`tun0` only the `10.80.1.x` ones, you have the inner/outer distinction right.
One `ping -c1` yields two datagrams — request out, reply back.

## Revision notes

- `r1` — Phase 1 raw loop delivered and verified on Debian 12/13 containers.
  When the protected transport (14-byte header + AEAD, spec §6) lands, this
  chapter gains a historical note pointing at the chapter that supersedes raw
  UDP; the commands here will then be pinned to this change set.
