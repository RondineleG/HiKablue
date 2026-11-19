<!-- chapter-id: ch02-telemetry-loop -->
# Chapter 2 — The Engine Learns to Answer: Telemetry

*The Journey of my VPN* · Chapter `ch02-telemetry-loop` · Milestone 1: telemetry

Chapter 1 built a tunnel that forwards bytes. This chapter makes the engine
*accountable*: it counts every datagram it sees, answers `GetMetrics` on a
Unix socket, and a Go agent learns to ask — politely, with a deadline — while
a C# control plane keeps a list of who is alive.

> **Lab boundary.** As in Chapter 1, everything here runs in an isolated,
> authorized lab (`.agc/RULES.md` LAB-01). The metrics socket carries no
> authentication yet; the control-plane milestone replaces the temporary
> HTTP heartbeat with the gRPC `NodeChannel` stream.

## What we built, and why it is split this way

Three small pieces, one per language, each with a job the others cannot do:

- **Rust engine** — owns the counters. `rx_bytes` increments on *every*
  received datagram, even ones validation later drops: the milestone's
  acceptance check is "a 5-byte datagram raises the counter by five", and
  that only works if counting happens before filtering. The counters live in
  `Arc<Metrics>` of `AtomicU64` relaxed atomics, shared between the packet
  loop and a second tokio task that serves the socket — so telemetry can
  never block the dataplane.
- **Go agent** — owns the asking. `FetchMetrics(ctx)` dials the Unix socket,
  writes one request frame, reads one response frame. The `ctx` deadline is
  applied to the socket itself, so a peer that accepts and stays silent is
  cut off instead of stalling the collection loop forever.
- **C# control plane** — owns the bookkeeping. `POST /internal/heartbeats`
  records a node; `GET /v1/nodes` lists them. `lastSeen` (when the node last
  spoke) and `engineHealthy` (what the node *reported* about its engine) are
  separate fields, because "quiet" and "broken" are different failures.

## The frame: one length, one JSON blob

The protocol on `/run/vpn/engine.sock` is the smallest thing that works:

```text
┌───────────────┬──────────────────────┐
│ u32 big-endian│  UTF-8 JSON envelope │
│ frame length  │  (max 1 MiB)         │
└───────────────┴──────────────────────┘
```

Request `{"request_id":"req-1","version":1,"op":"GetMetrics"}` gets an
envelope back: the echoed `request_id`, a `status` of `"ok"` plus a `result`
object, or `"error"` plus `code`/`message`:

```json
{"request_id":"req-1","version":1,"status":"ok",
 "result":{"rx_bytes":5,"rx_pkts":0,"tx_bytes":0,"tx_pkts":0,
           "dropped_pkts":2,"peers":0,"uptime_secs":2}}
```

`AddPeer`/`RemovePeer`/`SyncPeers` share this socket in the spec; only
`GetMetrics` is implemented now — unknown ops get a `status:"error"` envelope.
The socket file is mode `0660` shared through a dedicated group
(`--metrics-group`), so users outside it cannot connect, and each connection
gets a read/write deadline plus a cap on concurrent connections — a silent
or idle peer cannot exhaust descriptors.

## Exercise — why one read can return half a frame

A stream socket is a *byte pipe*, not a message queue. If the writer sends
`LLLL{json}` in two `write()` calls — or the kernel chops it into segments —
one `read()` on the reader's side can return any prefix of the frame: the
length alone, the length plus three bytes of JSON, anything. The fix is
always the same shape: read the 4-byte length with `read_exact`, allocate,
then `read_exact` the body. In Rust, `tokio::io::AsyncReadExt::read_exact`
loops until the buffer is full; in Go, `io.ReadFull` does the same.

`engine-rust/src/metrics.rs` reproduces the pathology in a test: the client
writes the *request* in two halves with a delay between them, and the engine
still assembles the whole frame before answering. Delete the `read_exact`
calls and the test flakes exactly the way production would. The listener
side has the same lesson on the way out — replies are written with
`write_u32` + `write_all`, and both directions sit under a deadline.

## Exercise — the silent engine

What if the peer accepts the connection and then says nothing? Without a
deadline, the read blocks forever and every later collection stalls behind
it. `agent-go/metrics_test.go` runs a fake engine that accepts and stays
mute: `FetchMetrics` under a 150 ms context deadline returns an error in
~150 ms, and the *next* call against a healthy engine succeeds — proving the
deadline kills the read, not the loop.

## Exercise — missing heartbeat vs. dead engine

The C# `NodeRegistryTests` show the difference the data model exists to
capture: a node that stops beating keeps its old `LastSeen` (stale), while a
node that beats with `engineHealthy=false` has a fresh `LastSeen` *and* a
separate bad flag. "Last reported state" and "last contact" answer different
questions; conflating them is how dashboards lie.

## Observed, not promised

`tests/lab/telemetry-demo.sh` runs the whole milestone on localhost — engine
under `sudo` (the TUN still needs `CAP_NET_ADMIN`), agent and control plane
unprivileged — and prints `telemetry-demo: PASS` only after observing:

1. a 5-byte UDP datagram raises `rx_bytes` by exactly five;
2. `GET /v1/nodes` lists the node carrying those metrics;
3. after `SIGTERM`, the agent's fetch fails and the next heartbeat reports
   `engineHealthy: false`;
4. after restart, `GetMetrics` answers again.

## Reading the code

The heart of `handle()` is three lines that look trivial and carry the whole
lesson of this chapter:

```rust
let read = async {
    let len = stream.read_u32().await?;          // 4-byte length, big-endian
    if len > MAX_FRAME {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            format!("metrics frame of {len} bytes exceeds {MAX_FRAME}"),
        ));
    }
    let mut frame = vec![0u8; len as usize];     // allocate exactly the frame
    stream.read_exact(&mut frame).await?;        // loop until it is complete
    Ok::<_, io::Error>(frame)
};
let frame = tokio::time::timeout(IO_DEADLINE, read).await??;
```

- `read_u32` is `AsyncReadExt::read_u32`: four bytes, big-endian — the same
  length the Go agent writes with `binary.Write(..., binary.BigEndian, ...)`.
- The length is checked *before* the `vec!` allocates: a hostile or broken
  peer cannot make the engine allocate 4 GiB for a "frame".
- The rejection builds a real error: `io::Error::new` wraps the
  `io::ErrorKind::InvalidData` kind plus a `format!` message naming the
  offender's size. The message reaches the engine's stderr — the socket
  loop logs the failure and closes the connection without a response frame,
  so the peer sees an EOF, not this text.
- `read_exact` is the fix for the partial-read exercise — it keeps reading
  until `frame` is full, no matter how the kernel chunked the bytes.
- `timeout(IO_DEADLINE, read)` is the fix for the silent-peer exercise: the
  deadline covers the *whole* read — ten seconds for the frame to complete,
  not ten seconds between bytes.

| File | What it does |
|---|---|
| `engine-rust/src/metrics.rs` | `Metrics` counters; `bind()` probes a stale-vs-live socket path, sets `0660`, optional `chgrp`; `serve()` accepts with a connection cap + I/O deadline; `handle()` reads/writes the JSON envelope |
| `engine-rust/src/forward.rs` | Increments counters on every dataplane path (`rx_bytes` before validation, `dropped_pkts` on each drop — including outbound packets with no learned peer); client sets `peers=1` for its configured remote |
| `engine-rust/src/cli.rs` | Flags `--metrics-sock` (default `/run/vpn/engine.sock`) and `--metrics-group` |
| `agent-go/metrics.go` | `FetchMetrics(ctx)` — dial, deadline + cancel watcher, writes the envelope, `io.ReadFull` the response, decodes `result` |
| `agent-go/cmd/vpn-agent/main.go` | One-shot CLI that prints the decoded metrics |
| `control-dotnet/VpnControl/NodeRegistry.cs` | In-memory node table: `lastSeen` and `engineHealthy` kept apart |
| `control-dotnet/VpnControl/Program.cs` | `POST /internal/heartbeats`, `GET /v1/nodes` |
| `tests/lab/telemetry-demo.sh` | End-to-end acceptance: +5 bytes, outage, recovery on both socket and control plane; refuses preexisting `hktelem0` |
| `io::Error::new` / `InvalidData` / `format!` | Constructs an I/O error of the "invalid data" kind with a formatted message — used to reject an oversized frame. |

## Glossary

- **`AF_UNIX` socket** — an address family for same-host IPC: the endpoint is
  a filesystem path, not an IP:port.
- **`AtomicU64` / relaxed ordering** — a 64-bit counter that can be updated
  concurrently without locks; `Relaxed` means "no extra synchronization
  guarantees", which is all independent counters need.
- **`CAP_NET_ADMIN`** — the Linux capability required to create/configure a
  TUN interface; why the engine runs under `sudo` while the agent does not.
- **`ctx` / context deadline** — Go's way of carrying "give up after T" into
  blocking calls; here applied to socket reads/writes.
- **`io.ReadFull` / `read_exact`** — read-until-buffer-full helpers that turn
  stream semantics into frame semantics.
- **`lastSeen`** — timestamp of the last heartbeat, kept separate from the
  node's last *reported* engine health.
- **Length-prefixed frame** — message framing that sends the byte count
  before the payload, so the reader knows how much to assemble.
- **`0660` + dedicated group** — Unix socket mode that gives the owner and
  the named group access; the lab procedure's way of sharing the engine
  socket with the unprivileged agent without opening it to every local user.
- **`request_id` / envelope** — the JSON wrapper the socket contract
  requires: request id + version + op + payload out, echoed id + status +
  result-or-error back, so replies can be matched to requests.
- **Heartbeat** — a periodic "I'm here" signal from a node; temporary HTTP
  POST until the gRPC channel lands in the control-plane milestone.

## Revision notes

- `r2` — Review fixes: the socket protocol now uses the spec's envelope
  (`request_id`, `version`, `op`, `status`/`result`/`error`, 1 MiB cap) with
  read/write deadlines and a connection cap; the socket file is `0660` with
  `--metrics-group`; `bind()` refuses to replace a live socket; the client
  reports its configured peer; unlearned drops count; the demo verifies
  control-plane recovery and refuses preexisting interfaces.
- `r1` — Milestone 1 telemetry delivered and verified by
  `tests/lab/telemetry-demo.sh` on a local lab: +5-byte counter, outage and
  recovery observed; 32 Rust tests, 3 Go tests, 3 C# tests. The HTTP
  heartbeat and in-memory registry are deliberately temporary — they are
  replaced by gRPC and PostgreSQL in the control-plane milestone.
