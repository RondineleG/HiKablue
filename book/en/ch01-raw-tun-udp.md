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

> **Historical chapter.** The examples and observed results use the
> [Phase 1 revision](https://github.com/RondineleG/HiKablue/blob/909c31e18bb75db34be1511de2bbf667c1338906/book/en/ch01-raw-tun-udp.md).
> The namespace lab on `main` now uses AEAD; see [Chapter 4](ch04-data-plane-crypto.md)
> for the encrypted path and its separate evidence.

## The two stories every packet tells

Look at the lab we will use:

```text
   node A 192.168.56.11          node B 192.168.56.12
   ┌──────────────────┐          ┌──────────────────┐
   │  ping            │          │  ping            │
   │  tun0 10.80.1.1  │          │  tun0 10.80.1.2  │
   │  vpn-engine server  │          │  vpn-engine client  │
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

`ping` does not talk to `vpn-engine`. It talks to the kernel's IP stack, which
routes `10.80.1.0/24` out of `tun0`. `tun0` is a **TUN** device: a virtual
network interface whose other end is a file descriptor, `/dev/net/tun`.
`vpn-engine` opens it with `IFF_TUN | IFF_NO_PI` — layer 3, and *no packet
information* header — so every `read()` returns exactly one raw IPv4 packet
and every `write()` injects exactly one. Creating the interface and applying
its address and MTU need `CAP_NET_ADMIN`; that is why the lab runs `vpn-engine`
with `sudo`.

## Tracing one ping, B → A

1. On B, `ping 10.80.1.1` builds an ICMP echo request with source `10.80.1.2`.
   The kernel's connected route for `10.80.1.0/24` — installed when vpn-engine set
   `--tun-cidr 10.80.1.2/24` — sends it to `tun0`, where it becomes bytes on
   vpn-engine's descriptor.
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
> validation plus an inner source- and destination-IP match* — on **every**
> datagram, before and after learning, so the pinned endpoint cannot inject
> packets that claim other virtual IPs or are aimed at them. It is input
> validation, nothing more. Any sender that can reach A's UDP port and spoof
> `10.80.1.2` inside would still pass. Milestone 4's AEAD transport is what
> will make this safe.

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
  `iproute2`); `lab-node-cleanup.sh` only *verifies* that `tun0` is gone —
  our interface is transient and dies with its descriptor — and removes a
  surviving one only when you pass `--lab-owned` *and* it carries a
  `10.80.0.0/16` lab address, so another VPN's interface is never removed.
  For the same reason, the engine asks for `IFF_TUN_EXCL` so the kernel
  *atomically* refuses a TUN name that already exists instead of letting us
  reconfigure someone else's interface.

## Reading the code

Four excerpts — the exact code shipped in `engine-rust/src/` — with what each
line does. Rust reaches the kernel through C interfaces here, so the strange
names (`ifreq`, `ioctl`, `MSG_TRUNC`) are Linux's, not ours.

**1. Asking the kernel for a TUN** — `src/tun.rs`, `Tun::open`

```rust
let mut req: Ifreq = std::mem::zeroed();
for (slot, byte) in req.ifr_name.iter_mut().zip(name.bytes()) {
    *slot = byte as libc::c_char;
}
req.ifr_flags = IFF_TUN | IFF_NO_PI | IFF_TUN_EXCL;
if libc::ioctl(fd, TUNSETIFF, &req as *const Ifreq) < 0 {
    let err = io::Error::last_os_error();
    if err.raw_os_error() == Some(libc::EBUSY) {
        return Err(io::Error::new(
            io::ErrorKind::AlreadyExists,
            format!(
                "interface {name:?} already exists (foreign or persistent \
                 TUN); pick another --tun-name or remove it"
            ),
        ));
    }
    return Err(err);
}
```

- `Ifreq` ("interface request") is the fixed-size struct the kernel expects for
  interface ioctls: a 16-byte field for the name plus the flags. `zeroed()`
  fills it with zeros — a C struct must be fully initialized — and the loop
  copies our name (`tun0`) in byte by byte.
- `ifr_flags = IFF_TUN | IFF_NO_PI | IFF_TUN_EXCL` *is* the request: a
  layer-3 device, no extra header, and — `IFF_TUN_EXCL` — "fail if the name
  is taken" so the kernel refuses a foreign `tun0` *atomically*, with no
  race between our check and the create.
- `ioctl(fd, TUNSETIFF, &req)` performs the call: `ioctl` means "I/O control",
  the generic syscall for device-specific commands, and `TUNSETIFF` is the
  command number for "attach this descriptor to a TUN interface with these
  flags". A return value below 0 means the kernel refused (no `CAP_NET_ADMIN`,
  bad flags…) and we turn the kernel's error into a Rust `Err` — `EBUSY`
  (name already taken, raised by `IFF_TUN_EXCL`) gets the friendly
  "pick another name" message; anything else propagates.

**2. Deciding what counts as a packet** — `src/packet.rs`, `validate_ipv4`

```rust
if packet.len() < MIN_IPV4_HEADER_LEN {
    return Err(PacketError::Truncated);
}
if packet[0] >> 4 != IPV4_VERSION {
    return Err(PacketError::NotIpv4);
}
let ihl = (packet[0] & 0x0f) as usize;
if ihl < 5 {
    return Err(PacketError::BadHeaderLength);
}
let header_len = ihl * 4;
if header_len > packet.len() {
    return Err(PacketError::Truncated);
}
let total_len = u16::from_be_bytes([packet[2], packet[3]]) as usize;
if total_len < header_len || total_len != packet.len() {
    return Err(PacketError::BadTotalLength);
}
if !header_checksum_ok(&packet[..header_len]) {
    return Err(PacketError::BadChecksum);
}
```

Byte 0 of an IPv4 header carries two fields: the **version** in its high nibble
(`packet[0] >> 4`, must be 4) and the **IHL** in its low nibble (`& 0x0f`), the
header length in 32-bit words — `5` means 20 bytes. Bytes 2–3 are the *total
length* in big-endian order (`from_be_bytes` swaps them to host order). The
kernel hands us whole packets, so `total_len` must equal the number of bytes we
actually hold; anything else is corruption and dropped. The checksum check that
follows (see the file) folds the header into 16-bit sums — a valid header
always sums to `0xffff`.

**3. The heart of the program** — `src/forward.rs`, `run`

```rust
loop {
    tokio::select! {
        biased;
        result = tokio::signal::ctrl_c() => {
            result?;
            break;
        }
        _ = sigterm.recv() => {
            break;
        }
        result = tun.read(&mut tun_buf) => {
            let n = result?;
            if validate_ipv4(&tun_buf[..n], cfg.mtu as usize).is_err() {
                continue;
            }
            // A failed send for one datagram (e.g. ECONNREFUSED while the
            // peer is down, or EMSGSIZE) must not kill the tunnel.
            let sent = if is_server {
                match learned_peer {
                    Some(peer) => Some(socket.send_to(&tun_buf[..n], peer).await),
                    None => None,
                }
            } else {
                Some(socket.send(&tun_buf[..n]).await)
            };
            if let Some(Err(err)) = sent {
                eprintln!("vpn-engine: dropping outbound datagram: {err}");
            }
        }
        result = recv_datagram(&socket, &mut udp_buf) => {
            // Per-datagram receive errors (e.g. a queued ICMP
            // port-unreachable on a connected socket) are not fatal
            // either.
            let (real_len, from) = match result {
                Ok(v) => v,
                Err(err) => {
                    eprintln!("vpn-engine: dropping inbound datagram: {err}");
                    continue;
                }
            };
            if real_len > udp_buf.len() {
                // Truncated by the buffer: not a complete packet.
                continue;
            }
            let packet = &udp_buf[..real_len];
            if validate_ipv4(packet, cfg.mtu as usize).is_err() {
                continue;
            }
            if is_server {
                let peer_vip = cfg.peer_vip.expect("server sets peer_vip");
                match classify_server_packet(
                    learned_peer,
                    from,
                    source_ip(packet),
                    destination_ip(packet),
                    peer_vip,
                    cfg.tun_cidr.addr,
                ) {
                    // Learn the endpoint only after structural validation
                    // and an inner source-IP match. This pins the peer but
                    // is not authentication: any sender that can reach this
                    // UDP port and spoof the peer's virtual IP would pass.
                    ServerInbound::Learn => {
                        learned_peer = Some(from);
                        eprintln!("vpn-engine: learned peer endpoint {from}");
                    }
                    ServerInbound::Forward => {}
                    ServerInbound::Drop => continue,
                }
            } else if destination_ip(packet) != cfg.tun_cidr.addr {
                // Client: the connected socket already pins the remote,
                // but the peer must not inject packets addressed to
                // other tunnel IPs.
                continue;
            }
            tun.write(packet).await?;
        }
    }
}
```

`tokio::select!` waits on all four branches **at once** in a single thread:
whichever produces a value first runs its arm, then the loop starts over.
`biased` makes the branches be tried in order, so a pending `Ctrl+C` or
SIGTERM always wins over more traffic — the tunnel dies when asked, not when
idle. `tun_buf` and `udp_buf` are allocated once *before* the loop and reused
every iteration, which is why no packet ever triggers a heap allocation.

Reading the arms top to bottom: the two signal arms just `break` out of the
`loop`, which returns and drops the TUN descriptor. The **TUN arm** reads one
packet, drops it when `validate_ipv4` fails, then sends it — `send_to` toward
the learned peer on a server (or nowhere yet, when `learned_peer` is `None`),
`send` on the client whose socket is connected; a per-datagram failure is
logged and swallowed by the `if let Some(Err(err))` guard instead of
propagating with `?`. The **UDP arm** is the mirror: unwrap or `continue` on
error, drop anything the `MSG_TRUNC` read reported as longer than the buffer,
drop invalid packets, apply the peer-learning decision below, then write the
surviving packet into the TUN. Every `continue` is a dropped datagram; every
`?` is a fatal exit — those are the only two ways out.

**4. Learning the one peer** — `src/forward.rs`, inside the UDP arm

```rust
match classify_server_packet(
    learned_peer,
    from,
    source_ip(packet),
    destination_ip(packet),
    peer_vip,
    cfg.tun_cidr.addr,
) {
    ServerInbound::Learn => {
        learned_peer = Some(from);
        eprintln!("vpn-engine: learned peer endpoint {from}");
    }
    ServerInbound::Forward => {}
    ServerInbound::Drop => continue,
}
```

The decision is a pure function (no I/O) so it is unit-testable: given the
currently pinned peer, this datagram's sender, the packet's *inner* source
and destination IPs, the configured `--peer` and our own virtual IP, it
returns one of three outcomes. `Learn` pins `from` — the *outer* endpoint —
only after inner source *and* destination matched; `Forward` is "peer
already pinned, proceed" — and still requires both IPs to match, so the
pinned endpoint cannot inject packets from or toward other virtual IPs;
`Drop` discards everything else. On the client the socket is already
connected to `--remote`, so only the destination check applies (the
`else if` above).

## Every acronym, decoded

| Term | What it is | Why it is here |
|---|---|---|
| VPN | Virtual Private Network — software that makes remote machines behave as if they shared one private network. | The thing this book builds, piece by piece. |
| TUN | "network TUNnel": a Linux virtual interface whose other end is a file descriptor; it passes layer-3 (IP) packets. | Our tunnel's local end — where packets enter/leave the program. Its sibling TAP passes layer-2 Ethernet frames; we don't use it. |
| IFF_TUN / IFF_NO_PI | Flags in the kernel request: "a TUN device" / "don't prepend the 4-byte packet-information header". | Together they make every read/write exactly one raw IP packet. |
| IFF_TUN_EXCL | Flag meaning "fail if the interface name is already taken". | Lets the kernel refuse a foreign `tun0` atomically — no race between checking and creating. |
| ioctl | "I/O control" — the Unix syscall for device-specific commands that don't fit `read`/`write`. | Creates the interface via the `TUNSETIFF` command. |
| fd (file descriptor) | The integer handle the kernel gives a process for an open file or device. | `vpn-engine`'s end of `tun0` and of the UDP socket are both fds. |
| /dev/net/tun | The device file that manufactures TUN/TAP descriptors. | Opening it is step one of `Tun::open`. |
| CAP_NET_ADMIN | The Linux capability that permits network configuration. | Why the engine runs with sudo/root in the lab. |
| UDP | User Datagram Protocol: one datagram = one message, no connection, no retransmission. | The "wire" between nodes — a VPN does not want TCP re-sending packets that are already stale. |
| IPv4 | Internet Protocol version 4: the 32-bit-address packet format (`10.80.1.2`). | The format carried *inside* the tunnel in Phase 1. |
| IHL | Internet Header Length — IPv4 field giving the header size in 32-bit words (5 = 20 bytes). | One of the fields `validate_ipv4` checks. |
| MTU | Maximum Transmission Unit — largest packet an interface accepts. | Set to 1400 so later headers still fit a 1500-byte path. |
| CIDR | Classless Inter-Domain Routing — the `A.B.C.D/N` notation; `/24` = first 24 bits fixed. | `10.80.1.1/24` is how you tell the interface its address *and* its subnet. |
| ICMP | Internet Control Message Protocol — control/error messages inside IP. | `ping` is ICMP echo request + echo reply. |
| checksum | A small arithmetic digest in the IPv4 header that exposes corruption. | Recomputed on every packet by `header_checksum_ok`. |
| endpoint | An `IP:port` pair naming a UDP sender or receiver (`192.168.56.12:51820`). | What the server learns and pins as its peer. |
| peer | The single node on the other side of the tunnel in Phase 1. | `--peer` on the server, `--remote` on the client. |
| underlay / overlay | The real network carrying the tunnel (`192.168.56.0/24`) / the virtual network inside it (`10.80.1.0/24`). | The inner-vs-outer distinction the chapter trains. |
| NIC | Network Interface Card — the physical interface (`eth0`). | Carries only the *outer* datagrams; the inner packet is invisible to it. |
| SIGINT / SIGTERM | Unix signals: `Ctrl+C` and the polite "terminate" request. | Both break `select!` so the transient interface is removed. |
| Rust | The compiled, memory-safe language the engine is written in. | `engine-rust/` is the first of the project's three implementations. |
| Tokio | Rust's async runtime: lets one thread wait on socket + TUN + signals without blocking. | Powers the whole forwarding loop. |
| async / await | Rust syntax for "pause this task until the I/O is ready". | `tun.read(...)`, `socket.send(...)` are async calls. |
| select! | Tokio macro: wait on several async sources, run the first that is ready. | The loop's structure — two directions plus two shutdown signals. |
| libc | Rust's bindings to the C standard library. | How we reach `ioctl`, `recvmsg` and other kernel calls. |
| syscall | A call from a user program into the kernel (`read`, `write`, `ioctl`, `recvmsg`). | Everything interesting the engine does is a handful of syscalls. |
| recvmsg / MSG_TRUNC | Receive a datagram *with metadata*; `MSG_TRUNC` returns the datagram's true length even when it exceeded our buffer. | Detects oversized datagrams instead of injecting a truncated packet. |
| ECONNREFUSED | The error a connected UDP socket reports after an ICMP "port unreachable" (peer is down). | Logged and skipped — one dead peer must not kill the tunnel. |
| NAT | Network Address Translation — rewriting packet addresses. | Deferred: not needed for node-to-node ping. |
| AEAD | Authenticated Encryption with Associated Data — encryption + integrity in one step. | Phase 4's transport; its 16-byte tag is why the MTU is 1400. |
| `ping` | Command that sends ICMP echo requests and prints the replies. | The end-to-end acceptance test of the whole chapter. |
| `ip` | The iproute2 Swiss-army command for addresses, links and routes. | Sets MTU, address and link state in `configure_interface`. |
| `ip netns` | `ip` subcommand managing network namespaces — isolated copies of the kernel's network stack (own interfaces, routes, sockets). | `tests/lab/netns-ping.sh` puts each lab node in its own namespace. |
| `veth` | Virtual Ethernet pair: two linked interfaces acting as the ends of a virtual cable between namespaces. | `hkl-a`/`hkl-b` carry the tunnel's outer UDP traffic in the netns test. |
| `--lab-owned` | `lab-node-cleanup.sh` flag: "I assert this `tun0` belongs to the lab". | Without it the script only verifies cleanup — a `tun0` that survives gets deleted only on your explicit word plus a `10.80.x` address, so an interface that is not ours is never removed. |
| `tcpdump` | Command-line packet capture: prints packets as an interface sees them. | Proves inner vs. outer addresses (`tun0` vs. `eth0`). |
| `tcpdump -ni` | `-n` = don't resolve names to hostnames; `-i` = capture on this interface. | Keeps captures raw (`-ni tun0`, `-ni eth0`). |
| `tcpdump -c1` | `-c` = stop after N packets (here, 1). | Captures exactly one packet for the exercise. |
| `MIN_IPV4_HEADER_LEN` | Our constant `20` — smallest legal IPv4 header in bytes. | The first structural check: below this it cannot be IPv4. |
| `PacketError` | Our enum of validation outcomes (`Oversized`, `Truncated`, `NotIpv4`, `BadHeaderLength`, `BadTotalLength`, `BadChecksum`). | Names *why* a packet was dropped. |
| `Ifreq` / `TUNSETIFF` | The struct layout and command number the kernel expects for "create a TUN". | The two pieces of the `ioctl` that makes `tun0`. |
| `Option` / `Some` / `None` | Rust's "maybe a value" type: `Some(x)` or `None`. | `learned_peer` is `None` until the server pins its peer. |
| `match` | Rust's exhaustive branching on a value's cases. | The three `ServerInbound` outcomes are a `match`. |
| `eprintln!` | Macro that prints to stderr. | The engine's only log output — no telemetry in Phase 1. |
| `?` (Rust) | "On `Err`, return it now" — early-return operator. | Fatal errors (TUN read, `tun.write`) still propagate; `continue` and guarded `if let` handle the non-fatal ones. |

## Run it yourself

Build the engine from the pinned Phase 1 revision and follow its
[lab README](https://github.com/RondineleG/HiKablue/blob/909c31e18bb75db34be1511de2bbf667c1338906/engine-rust/README.md)
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
| Server peer learning by inner source/destination IP | `src/forward.rs` `classify_server_packet` | 7 deterministic cases: learn on match, drop divergent source, drop divergent destination, forward pinned peer, drop pinned endpoint with divergent inner source or destination, drop other endpoint; observed `learned peer endpoint` log |
| Clean shutdown on SIGINT/SIGTERM | `src/forward.rs` signal arms | observed exit 0 on Debian 13 |

## Observed results vs. expectations

| Check | Environment | Observed |
|---|---|---|
| `cargo check`, `cargo test`, `cargo build --release` | Ubuntu 22.04, Rust 1.99.1 | 0 warnings; 28/28 tests pass |
| [`tests/lab/netns-ping.sh`](https://github.com/RondineleG/HiKablue/blob/909c31e18bb75db34be1511de2bbf667c1338906/tests/lab/netns-ping.sh) (two `ip netns` nodes on a veth pair) | Ubuntu 22.04 | PASS: 3/3 pings each way, TUN gone after shutdown |
| Same script with a foreign `hik-lab-a` present | Ubuntu 22.04 | refused before touching it (LAB-02); namespace survived |
| Ping `10.80.1.1` from B and `10.80.1.2` from A | Debian 13 (trixie) containers, `hikablue-lab` net | 3/3 replies each direction |
| Same pings | Debian 12 (bookworm) containers | 3/3 replies each direction |
| SIGINT / `docker stop` (SIGTERM) | Debian 13 containers | exit code 0; `tun0` gone with the process |
| `tcpdump` on `eth0` | Debian 13 node A | `172.30.0.12.51820 > 172.30.0.11.51820: UDP, length 84` |
| `tcpdump` on `tun0` | Debian 13 node A | `10.80.1.2 > 10.80.1.1: ICMP echo request` |
| Ping while server down, then server returns | Debian 13 node B | client stayed up (per-datagram error dropped); ping recovered |
| `vpn-engine` on existing `tun0` | Debian 13 node B | refused (`already exists`); foreign interface untouched |
| 2500-byte datagram, forged header TL=2048 | Debian 13 node A | dropped by real length; nothing on `tun0` |

Everything in the Phase 1 scope was verified. Application-level connectivity
through a node (e.g. HTTP to a third host) is *out of scope*: it needs
forwarding and NAT, which are deferred.

## Exercise — predict the capture before you look

On node A, run both captures while B pings once:

```sh
sudo tcpdump -c1 -ni eth0 udp port 51820 &
sudo tcpdump -c1 -ni tun0 icmp &
```

In node B's terminal, run `ping -c1 10.80.1.1` after making your prediction.

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
- `r2` — The binary was renamed `vpngine` → `vpn-engine`, and the chapter
  gained the *Reading the code* walkthrough and the acronym glossary required
  by `.agc/RULES.md` DOC-03 for this and every later chapter.
- `r3` — `.agc/IMPLEMENTATION.md` was amended to record, in the checklist
  itself, the deferral of forwarding/NAT/MSS clamping, split-vs-full tunnel
  selection, and the HTTP endpoint check to the full-tunnel phase. The chapter
  text is unchanged: the deferral it already described is now written where
  the requirement lives.
- `r4` — Roadmap documents aligned with reality: the spec §8 gained an
  explicit *Execution status* (milestone 2 delivered out of order, milestone
  1 still pending, identifiers preserved), and the root README now separates
  the verified ICMP ping from the deferred HTTP check. No chapter content
  changed.
- `r5` — Pin the raw UDP lab and its evidence to Phase 1; link to Chapter 4.
