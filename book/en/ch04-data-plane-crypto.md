<!-- chapter-id: ch04-data-plane-crypto -->
# Chapter 4 — The Sealed Wire: AEAD, Replay, and the Unforgiving Window

*The Journey of my VPN* · Chapter `ch04-data-plane-crypto` · Milestone 4:
Rust data-plane crypto and session validation

Until now the tunnel spoke in cleartext: anyone between the two
namespaces could read — and rewrite — every datagram. This chapter puts
the wire under ChaCha20-Poly1305 and tells the three decisions that
separate "encrypted" from *secure*: one key per direction, a nonce that
never repeats, and a replay window that only advances after
authentication.

> **Lab boundary.** The phase-2 crypto exercises use `--key <64-hex>`, a
> lab pre-shared key passed on both ends. The end-to-end session laboratory
> instead receives per-session directional keys through `ProvisionPeer` and
> starts clients with `--tx-key-file`/`--rx-key-file` — hex keys in
> `0600` files, so no key material appears in the process command line.
> The inline `--tx-key`/`--rx-key` forms still exist, warn on use, and
> remain lab bootstrap, not final client key delivery. Restarting an
> engine with the same key reuses key material and resets counter and
> window: outside the lab that is a vulnerability, not a detail.

## The wire format

Every datagram carries a 14-byte cleartext prefix, authenticated as
AAD, then ciphertext, then a 16-byte Poly1305 tag:

```text
[0]     u8   version (high nibble, v1) | type (low nibble: 0x01 DATA, 0x02 KEEPALIVE)
[1]     u8   reserved (0x00)
[2..6]  u32  sender_index (node-local session identifier, big-endian)
[6..14] u64  counter (big-endian)
[14..len-16)  ciphertext
[len-16..len) AEAD tag
```

Total overhead: **58 bytes** (20 IPv4 + 8 UDP + 14 + 16). A 1400-byte
inner packet leaves as 1430 bytes of UDP payload — a 1458-byte outer
datagram, inside a 1500-byte path and a 1492-byte PPPoE one.

Two things the reader does not see on the wire:

- **The AEAD IV does not travel**: it is rebuilt as 4 zero bytes plus
  the 8-byte counter in *little-endian* (`frame::aead_nonce`). The
  header counter is big-endian — independent conventions.
- **AAD protects the entire header.** Before verifying the tag, `open`
  checks frame length, header structure, the expected sender index, and
  the replay window without modifying it. A modified header that passes
  these preliminary checks fails authentication.

## One key per direction

The spec demands "separate keys per direction" and the reason is
subtle: if both ends seal with the same key, both emit nonce 1 on their
first packet — and a repeated key/nonce pair is catastrophic keystream
reuse.

In the lab, `--sender-index` and `--peer-index` solve this without a
handshake: each side derives its direction key from the PSK and the
index it stamps on packets:

```text
tx_key = BLAKE2s-256("hikablue-session-key-v1" ‖ local_index ‖ psk)
rx_key = BLAKE2s-256("hikablue-session-key-v1" ‖ peer_index  ‖ psk)
```

A server with `--sender-index 2 --peer-index 1` and a client with
`--sender-index 1 --peer-index 2` derive the same two keys in swapped
roles. `Config::validate` requires distinct, nonzero indexes when
`--key` is set — equal indexes would collapse both directions onto one
key, recreating the problem.

`key_epoch` is a 64-bit key generation identifier. The engine stores
`egress_key` in `tx_key` (engine to peer) and `ingress_key` in `rx_key`
(peer to engine). `Zeroizing` wipes the owned keys when the session is
dropped. The static lab constructor uses epoch zero; process arguments
are not covered by this cleanup.

## The counter that refuses to die

`SessionState::next_nonce` emits counters from 1 upward (0 is
reserved). `try_update` atomically increments below the last allowed counter.
The counter stays at the ceiling and returns `NonceExhaustionNeedsRekey`
instead of issuing `u64::MAX - 65536` or wrapping.

## The replay window

`AntiReplayWindow` holds `last_seq` + a 2048-bit bitmap (`[u64; 32]`):

- `check()` is a pure pre-flight, run *before* decrypt: seq 0 rejected,
  `last_seq − seq ≥ 2048` expired, set bit = duplicate. Nothing mutates.
- `update()` runs *after* the tag verifies — unauthenticated packets
  never burn sequence numbers. A forger cannot advance the window.

There is a second, smarter poisoner: an *authentic* copy of a datagram
delivered from another endpoint. If `open()` authenticated before
checking the pinned endpoint, the stranger's copy would burn the nonce
and the legitimate copy would read as a duplicate. So the loop drops by
endpoint *before* decrypting — and endpoint learning itself only
happens after tag + IPv4 validation + inner VIP all pass.

## True zero copy

`seal` and `open` never allocate: the TUN reads straight into
`buf[14..]` (header headroom), the tag lands in the 16-byte tailroom,
and decryption rewrites plaintext where the ciphertext stood —
`split_at_mut` hands disjoint regions for AAD, body and tag without a
single `Vec`.

[`recv_datagram`](../../engine-rust/src/forward.rs) uses `recvmsg(MSG_TRUNC)` to get the
real UDP datagram length. If it exceeds the 2048-byte receive buffer,
the engine drops the datagram before parsing or decrypting it rather
than processing a truncated prefix.

## Symmetric ingress

Server and client apply the same symmetric rule: the inner packet must
be valid IPv4, come from the peer's VIP and target the local VIP — now
*after* authentication, which is what makes endpoint pinning meaningful
(a spoof cannot produce a valid tag without the key). The server knows
the peer by `--peer`; the client names the same expectation with its
own `--peer <server-vip>` — authentication proves who sent the
datagram, but only this rule decides which inner addresses it may
carry. Drops count per cause: `invalid_inner_src_total`,
`invalid_inner_dst_total`, `dropped_pkts`.

**Known gap.** The [specification (§6)](../../vpn-debian-specification.md#6-experimental-rust-transport) requires an empty
`KEEPALIVE`. The engine sends empty keepalives but does not enforce that
length on receive. An authenticated nonempty keepalive is not written to TUN.

## Rotating without a drop: the control boundary

A fixed `--key` cannot rotate. The engine now exposes a second Unix
socket — `--control-sock`, mode 0600 — that accepts key-lifecycle ops
from the node agent in the same u32-BE length-prefixed JSON envelope
the metrics socket speaks, gated by `SO_PEERCRED` (root, the engine's
own uid, or the `--control-uid` agent). Key material never crosses the
telemetry socket.

The ops implement a two-phase commit with a drain window:

- `StagePeer` allocates a new epoch in a staging slot; rx immediately
  accepts datagrams stamped with the new `peer_index`.
- `Activate` flips egress to the staged epoch at a packet boundary and
  counts `rotations_total`; the old epoch keeps decrypting — it is now
  retiring.
- `Retire` ends the drain: the epoch leaves the table and its keys are
  zeroized.
- `RevokePeer` wipes every epoch; the engine stays keyed and drops
  traffic until reprovisioning.

[`SessionTable`](../../engine-rust/src/sessions.rs) holds one `tx`
epoch, one `staged` slot, and an `rx` map keyed by the wire
`sender_index` — readers take a shared `RwLock` and clone an `Arc`, so
the packet path never stalls on a rotation. `GetSessions` and the
metrics reply report per-epoch `tx_pkts`/`rx_pkts` and
`rx_unknown_sender_index_total`.

`PeerKeyMaterial` in the proto gained explicit `sender_index` and
`peer_index` fields: `lab_session_id` identifies the lab session across
rekeys, while each new epoch gets fresh wire indexes.

## Lab

```bash
sudo tests/lab/netns-ping.sh   # real ping over the AEAD path
```

The script passes a fixed lab `--key` and complementary indexes on both
ends; `tests/lab/telemetry-demo.sh` stays plaintext, proving backward
compatibility.

## What was verified

**Historical baseline (`r0`, [PR #14](https://github.com/RondineleG/HiKablue/pull/14)).**
The recorded run
passed 86 Rust tests and the full lab gate (15/15). Those results describe
the original implementation, not network verification of `r1`.

**Hardening revision (`r1`, [PR #17](https://github.com/RondineleG/HiKablue/pull/17)).**
On the Ubuntu
development VM, 91 Rust tests and the release build passed. The local
gate passed 12 checks, with the privileged lab skipped. GitHub Actions
did not start because of a billing lock. Bidirectional lab ping was not
rerun for this revision. Node forwarding, application connectivity and
cross-process key provisioning were not verified.

From the repository root, run
`cargo test --locked --manifest-path engine-rust/Cargo.toml`. The table
shows the checks behind the claims. TCP MSS clamping remains deferred to
the full-tunnel phase: 1360 at MTU 1400 for inner IPv4.

| Requirement | Code | Test |
|---|---|---|
| IV and 58-byte IPv4 overhead | [`wire/frame.rs`](../../engine-rust/src/wire/frame.rs) | `aead_nonce_maps_counter_to_last_eight_bytes`, `overhead_constants_match_spec` |
| Counters at the edge of the 2048-packet replay window | [`crypto/replay.rs`](../../engine-rust/src/crypto/replay.rs) | `boundary_minus_2047_passes_minus_2048_fails` |
| Published [RFC 8439 A.5](https://www.rfc-editor.org/rfc/rfc8439.html#appendix-A.5) vector, replacing the earlier library self-comparison | [`crypto/session.rs`](../../engine-rust/src/crypto/session.rs) | `rfc8439_vector_matches_iv_mapping` |
| Header modification leaves replay untouched; concurrent duplicate accepted once | [`crypto/session.rs`](../../engine-rust/src/crypto/session.rs) | `bitflip_in_header_fails_auth_and_leaves_replay_untouched`, `concurrent_open_accepts_each_nonce_once` |
| Atomic TX ceiling; replacement with a 64-bit epoch and new keys | [`crypto/session.rs`](../../engine-rust/src/crypto/session.rs) | `concurrent_nonce_allocation_stops_at_the_ceiling`, `replacement_keeps_full_epoch_and_uses_fresh_directional_keys` |
| UDP truncation detection; symmetric ingress in the test loop | [`forward.rs`](../../engine-rust/src/forward.rs) | `recv_datagram_reports_real_length_and_source`, `run_loop_client_and_server_scenarios` |

## Reading the code

### `derive_key` — `engine-rust/src/crypto/session.rs`

```rust
fn derive_key(key: &[u8; 32], index: u32) -> Zeroizing<[u8; 32]> {
    use blake2::Digest;
    let mut h = blake2::Blake2s256::new();
    h.update(b"hikablue-session-key-v1");
    h.update(index.to_be_bytes());
    h.update(key);
    let mut digest = h.finalize();
    let mut derived = Zeroizing::new([0; 32]);
    derived.copy_from_slice(&digest);
    digest.as_mut_slice().zeroize();
    derived
}
```

- The function takes the PSK (`key`) and the direction index
  (`index`).
- `Blake2s256::new()` opens a hasher: a function that crushes any input
  into a fixed 32-byte digest.
- The `b"hikablue-session-key-v1"` label is *domain separation*: it
  makes this derivation's inputs different from any other use of the
  same PSK — a cross-purpose collision would need the same label. It
  reduces the risk, it doesn't make collisions impossible.
- `index.to_be_bytes()` mixes the index (big-endian) into the digest —
  different index, different key. That is what splits the two
  directions.
- `h.update(key)` folds in the secret last.
- `finalize()` produces the digest; `copy_from_slice` copies it to the
  guarded buffer and `zeroize()` wipes the temporary bytes.

`SessionState::from_lab_psk` derives keys from the local and peer indexes.

### `check` — `engine-rust/src/crypto/replay.rs`

```rust
    pub fn check(&self, seq: u64) -> Result<(), ReplayError> {
        if seq == 0 {
            return Err(ReplayError::Reserved);
        }
        if seq > self.last_seq {
            return Ok(());
        }
        let diff = self.last_seq - seq;
        if diff >= WINDOW_SIZE {
            return Err(ReplayError::Expired);
        }
        if self.bit(diff) {
            Err(ReplayError::Duplicate)
        } else {
            Ok(())
        }
    }
```

- `seq == 0` is always rejected: 0 is reserved, the first valid counter
  is 1.
- `seq > last_seq` passes immediately — it is ahead of what we saw; the
  later `update()` slides the window.
- `diff = last_seq - seq` is the distance behind; `diff >= 2048`
  (`WINDOW_SIZE`) expired beyond the window horizon.
- `self.bit(diff)` queries the bitmap: bit 1 = already seen →
  `Duplicate`; bit 0 = legitimate gap → `Ok`.
- Note: `check` only reads. Marking happens in `update()`, called only
  after the AEAD tag verifies.

### `activate` — `engine-rust/src/sessions.rs`

```rust
    pub fn activate(&self, key_epoch: u64) -> Result<u64, TableError> {
        let mut state = self.state.write().unwrap();
        if state
            .tx
            .as_ref()
            .is_some_and(|active| active.key_epoch() == key_epoch)
        {
            return Ok(key_epoch);
        }
        if state.staged.as_ref().map(|s| s.key_epoch()) != Some(key_epoch) {
            return Err(TableError::UnknownEpoch(key_epoch));
        }
        let staged = state.staged.take().unwrap();
        let previous = state.tx.replace(staged);
        Ok(previous.map(|s| s.key_epoch()).unwrap_or(0))
    }
```

- The `RwLock` `write()` holds writers and readers together for a few
  instructions — mutations are rare (one per rotation), so the cost
  disappears next to the benefit of carrying no `ArcSwap` dependency.
- The first check makes `Activate` idempotent: a redelivered op is a
  confirmed no-op, not an error.
- The second stops an out-of-order `Activate` from consuming the
  staging slot — the requested epoch must be exactly the staged one.
- `state.tx.replace(staged)` is the atomic cut-over: the new epoch
  takes over egress and the old one lands back in the `Option` — it
  stays in the `rx` map as *retiring* until the agent's `Retire` ends
  the drain window.

## Glossary

- **AAD (Associated Authenticated Data)** — data authenticated but not
  encrypted; here, the 14-byte header. Touching it breaks the tag.
- **AEAD** — "Authenticated Encryption with Associated Data":
  encryption + integrity in one operation; here, ChaCha20-Poly1305
  (RFC 8439).
- **`Arc`** — an atomically reference-counted shared pointer: each `rx`
  map reader clones an `Arc` and holds the epoch without blocking
  writers.
- **AtomicU64 / `try_update`** — Atomic conditional update; concurrent callers cannot share a nonce.
- **BLAKE2s-256** — the 32-byte hash function used to derive each
  direction's key from PSK + index.
- **`derive_key`** — the function above: turns (label, index, PSK) into
  one direction's key.
- **Latch** — Exhausted counter stays at its ceiling and rejects further sends.
- **Headroom / tailroom** — space reserved before/after the payload in
  the buffer: 14B up front for the header, 16B at the back for the tag
  — no reallocating.
- **`--control-sock`** — the Unix control socket (mode 0600) through
  which the agent sends StagePeer/Activate/Retire/RevokePeer; kept
  separate from the metrics socket so keys never cross telemetry.
- **Epoch (`key_epoch`)** — the number of one key generation; each
  rotation creates a new epoch with fresh indexes and keys, never
  reusing any of them.
- **`--key <64-hex>`** — the lab PSK: 64 hex digits = 32 bytes, passed
  on both ends. Lab only.
- **`backend_instance_id`** — random identifier minted by one running
  engine instance; ACKs for another instance are fenced.
- **Endpoint pinning** — associating a keyed peer with the first outer
  address that authenticates valid traffic and dropping copies from other
  endpoints before AEAD verification.
- **`lab_session_id`** — the control-plane numeric session identifier used
  as the session-table key; distinct from the one-shot wire indexes.
- **Keepalive (0x02)** — a sealed datagram with no payload, sent when
  the tunnel is idle ≥ 25s to keep the NAT mapping open.
- **MAC / tag** — the 16 Poly1305 authentication bytes at the end of
  the datagram; proof the writer knew the key.
- **Nonce** — "number used once": the per-direction counter feeding the
  AEAD IV; repeating (key, nonce) is catastrophic.
- **PSK** — "pre-shared key": a 32-byte secret known by both sides
  before the conversation — in the lab, it comes from `--key`.
- **Replay window** — the 2048-bit bitmap remembering which recent
  counters were already authenticated.
- **`RwLock`** — a read-write lock: many readers (the packet path) or
  one writer (a rotation) at a time — readers never wait on each other.
- **`SO_PEERCRED`** — a Unix socket option that reveals the connecting
  peer's uid; the control socket only accepts root, the engine's uid or
  `--control-uid`.
- **Drain window** — the span between `Activate` and `Retire` during
  which the old epoch keeps decrypting in-flight packets; its duration
  is the agent's call.
- **`--sender-index` / `--peer-index`** — the session identifier
  stamped by the sender / expected by the receiver; it also selects
  which derived key to use.
- **`split_at_mut`** — Rust's slicer that splits a buffer into two
  disjoint mutable regions in constant time.
- **Tombstone** — the record of what is dead: epoch number, wire
  indexes and key *fingerprints* of a retired epoch, so none of them
  can ever be provisioned again.
- **TUN** — the virtual interface that hands inner IP packets in and
  out.
- **`u64::MAX − 65536`** — the counter ceiling: headroom before
  overflow to raise `NonceExhaustionNeedsRekey`.
- **Zeroizing** — a wrapper that overwrites memory with zeros on drop,
  so the key does not linger in RAM.

## Exercise — predict the wire before looking

A 900-byte inner packet leaves over the UDP socket. How many bytes is
the UDP payload, and how many is the outer IPv4 datagram? Then check:

- UDP payload = 900 + 14 (header) + 16 (tag) = **930**;
- outer datagram = 930 + 8 (UDP) + 20 (IPv4) = **958** — well under the
  1442-byte inner cap that keeps the outer ≤ 1500.

And a challenge: `last_seq = 4100`. Does a packet with `seq = 2051`
pass `check()`? Hint: compute `diff` and compare with `WINDOW_SIZE`
(2048). `diff = 2049 ≥ 2048` → `Expired`, rejected.

## Exercise — drive a rotation on the control socket

Start the engine with `--control-sock /tmp/ctl.sock` and, with the lab
passing continuous traffic, send in order:

1. `StagePeer` with `key_epoch=1`, `sender_index=33`, `peer_index=44`
   and two different 64-hex keys for egress/ingress;
2. `Activate` `key_epoch=1`;
3. after a few seconds, `Retire` `key_epoch=0`.

At each step, ask which `sender_index` the rx path accepts. Answers:
after (1) it accepts the old `peer_index` **and** the new 44; after (2)
egress already seals with 33 while ingress still accepts both; after (3)
only 44 remains — `rx_unknown_sender_index` starts counting stragglers
from the dead epoch and `rotations_total` reads 1. A repeated `Retire`
succeeds as a no-op, and reusing epoch 1, index 33 or the same keys is
refused by the tombstone.


## Milestone 4 integration — two peers, one exit engine

The rotation boundary from the earlier sections was single-peer. Milestone
4 turns it into the data plane the control plane actually needs: several
clients can share one exit engine, and removing one of them must not touch
packets or endpoints belonging to another. The table key is now the numeric
`lab_session_id`; the wire `peer_index` remains a global, one-shot lookup
key:

```rust
rx: HashMap<u32, (u32, Arc<SessionState>)>,
retired: Vec<Tombstone>,
used_indexes: HashSet<u32>,
```

Line by line: `rx` maps the index stamped by a client to its owning lab
session and epoch, so staged and retiring epochs still decrypt without a
scan. `retired` preserves fingerprints of dead key material. `used_indexes`
is separate because an index must never be reissued even after its epoch is
gone. A repeated `StagePeer` or `RevokePeer` is idempotent; a conflicting
index, epoch or key fingerprint is rejected.

Endpoint pinning also moved into the table. The server drops a datagram
whose resolved epoch belongs to a different learned endpoint *before*
calling `open` — otherwise a cross-endpoint replay would burn the real
packet's nonce in the replay window. After authentication, the inner source
must be that peer's virtual IP. The forwarding policy is deliberately
narrow for this milestone:

- service traffic may originate behind the exit node, so the client does
  not require the node VIP as inner source;
- every packet accepted by a client must be addressed to that client's
  assigned VIP;
- a packet from one client VIP to another client VIP is dropped — no
  client-to-client hairpin in milestone 4;
- server egress selects the active epoch by destination client VIP, not by
  a single learned endpoint.

`GetSessions` now returns `backend_instance_id` plus one entry per peer
(VIP, learned endpoint and epoch roles). The agent reads that instance id
before opening `NodeChannel`; a restart of the engine produces a different
backend identity, so ACKs for commands sent to the old data plane are
fenced instead of being mistaken for confirmations.

The clients in the laboratory do not use the derived lab `--key`: the
control plane issues exact directional keys and the client engine receives
them through `--tx-key-file`/`--rx-key-file` pointing at `0600` files
inside the private run directory; the laboratory asserts that no key
material leaks into `/proc/<pid>/cmdline`. The client direction is
inverted — engine `ingress_key` is client transmit key, engine
`sender_index` is the client's expected receive index. Key-file bootstrap
is still lab-only key transport, not the final client provisioning
design.

### Observed end-to-end data-plane result

`tests/lab/session-e2e.sh` and `tests/lab/session-e2e.sh --drop` passed on
the Ubuntu development VM. Both clients received `hikablue-m4-ok` through
the encrypted tunnel; the direct probe did not reach the service. During
revocation of A, B's continuous requests had no failure. Final server
metrics in the recorded normal run were `rx_pkts=105`, `tx_pkts=105`,
`dropped_pkts=4`, `peers=1`; the drop run reported `rx_pkts=152`,
`tx_pkts=151`, `dropped_pkts=5`, `peers=1`. The laboratory also provides
`tests/lab/session-e2e.sh --expire-restart`, which kills and restarts the
Go agent while the backend stays up and the control plane is unreachable:
the restarted agent enforces A's persisted deadline locally while B keeps
passing traffic. Counts vary with keepalives and
probe timing; the stable result is the PASS line. Compilation, forwarding
and HTTP connectivity are reported separately in
`tests/lab/session-e2e.md`.

## Revisions

- `r1` — 64-bit epoch, zeroizing keys, concurrent nonce ceiling and RFC vector.
- `r5` — Review hardening (PR #21): client lab keys now arrive through
  `--tx-key-file`/`--rx-key-file` (`0600` files; inline forms warn and
  conflict), the laboratory registers and cleans only the resources it
  created, and `--expire-restart` proves local expiry enforcement across
  an agent restart.
- `r3` — Verified on the dev VM: 108 Rust tests, local gate 12/12
  (privileged lab skipped); GitHub Actions blocked by billing.
- `r0` — AEAD, replay window, nonce latch, symmetric ingress,
  `--key`/`--sender-index`/`--peer-index`/`--keepalive-secs`, split
  ingress metrics.
- `r2` — Clarify frame validation and separate historical lab evidence from the local `r1` run.
- `r4` — Multi-peer session table, per-peer endpoints, service forwarding policy, control-plane-issued exact keys, backend instance reporting, and the two passing end-to-end laboratory scenarios.
- `r3` — Control socket (`--control-sock`, `SO_PEERCRED`), `SessionTable`
  multi-epoch rx, StagePeer/Activate/Retire/RevokePeer, per-epoch counters,
  proto `sender_index`/`peer_index`.
