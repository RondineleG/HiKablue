<!-- chapter-id: ch04-data-plane-crypto -->
# Chapter 4 — The Sealed Wire: AEAD, Replay, and the Unforgiving Window

*The Journey of my VPN* · Chapter `ch04-data-plane-crypto` · Milestone 4,
phase 2: Rust data-plane crypto

Until now the tunnel spoke in cleartext: anyone between the two
namespaces could read — and rewrite — every datagram. This chapter puts
the wire under ChaCha20-Poly1305 and tells the three decisions that
separate "encrypted" from *secure*: one key per direction, a nonce that
never repeats, and a replay window that only advances after
authentication.

> **Lab boundary.** Keys come from `--key <64-hex>`, a lab pre-shared
> key passed on both ends. Real key delivery — `ProvisionPeer` and
> `key_epoch` from milestone 3 — arrives in a later phase. Restarting an
> engine with the same `--key` reuses key material and resets counter
> and window: outside the lab that is a vulnerability, not a detail.

## The wire format

Every datagram carries a 14-byte cleartext prefix, authenticated as
AAD, then ciphertext, then a 16-byte Poly1305 tag:

```text
[0]     u8   version (high nibble, v1) | type (low nibble: 0x01 DATA, 0x02 KEEPALIVE)
[1]     u8   reserved (0x00)
[2..6]  u32  session_id (big-endian)
[6..14] u64  counter (big-endian)
[14..]       ciphertext
[..-16]      AEAD tag
```

Total overhead: **58 bytes** (20 IPv4 + 8 UDP + 14 + 16). A 1400-byte
inner packet leaves as 1430 bytes of UDP payload — a 1458-byte outer
datagram, inside a 1500-byte path and a 1492-byte PPPoE one.

Two things the reader does not see on the wire:

- **The AEAD IV does not travel**: it is rebuilt as 4 zero bytes plus
  the 8-byte counter in *little-endian* (`frame::aead_nonce`). The
  header counter is big-endian — independent conventions.
- **AAD protects the header itself**: touching type, session_id or the
  counter breaks the tag before any other check runs.

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

## The counter that refuses to die

`SessionState::next_nonce` emits counters from 1 upward (0 is
reserved). The atomic `fetch_add` keeps them unique under concurrent
selects, and the first seal to touch `u64::MAX − 65536` trips a latch:
from then on *every* `seal` returns `NonceExhaustionNeedsRekey` — the
session would rather stop than repeat a nonce.

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

## Symmetric ingress

Server and client keep the same discipline as before: the inner packet
must be valid IPv4, come from the peer's VIP and target the local VIP —
now *after* authentication, which is what makes endpoint pinning
meaningful (a spoof cannot produce a valid tag without the key). Drops
count per cause: `invalid_inner_src_total`, `invalid_inner_dst_total`,
`dropped_pkts`.

## Lab

```bash
sudo tests/lab/netns-ping.sh   # real ping over the AEAD path
```

The script passes a fixed lab `--key` and complementary indexes on both
ends; `tests/lab/telemetry-demo.sh` stays plaintext, proving backward
compatibility.

## What was verified

- `cargo test`: 86 tests — window boundaries ±2047/2048 and a 5000-seq
  leap, RFC 8439 vector against the crate's own output, AAD bit-flip
  leaving the window untouched, exhaustion latch, forged + replay drops
  before TUN, sealed keepalives observed on the wire in both roles,
  cross-endpoint replay without window poisoning.
- `tests/checks.sh --lab`: ALL PASSED 15/15; Rust coverage ≥ 95%.
- **Not verified**: cross-process key derivation via control-plane
  delivery (the lab shares one PSK) — expected in the `key_epoch` phase.

## Reading the code

### `derive_key` — `engine-rust/src/crypto/session.rs`

```rust
pub(crate) fn derive_key(key: &[u8; 32], index: u32) -> [u8; 32] {
    use blake2::Digest;
    let mut h = blake2::Blake2s256::new();
    h.update(b"hikablue-session-key-v1");
    h.update(index.to_be_bytes());
    h.update(key);
    h.finalize().into()
}
```

- The function takes the PSK (`key`) and the direction index
  (`index`).
- `Blake2s256::new()` opens a hasher: a function that crushes any input
  into a fixed 32-byte digest.
- The `b"hikablue-session-key-v1"` label is *domain separation*: it
  guarantees this digest never collides with the same PSK used for
  another purpose.
- `index.to_be_bytes()` mixes the index (big-endian) into the digest —
  different index, different key. That is what splits the two
  directions.
- `h.update(key)` folds in the secret last.
- `finalize().into()` returns the 32-byte derived key.

`SessionState::new` calls this twice — with `local_index` for `tx_key`
and `peer_index` for `rx_key` — and `seal`/`open` never touch the raw
PSK.

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

## Glossary

- **AAD (Associated Authenticated Data)** — data authenticated but not
  encrypted; here, the 14-byte header. Touching it breaks the tag.
- **AEAD** — "Authenticated Encryption with Associated Data":
  encryption + integrity in one operation; here, ChaCha20-Poly1305
  (RFC 8439).
- **AtomicU64 / `fetch_add`** — atomic integer: increments and returns
  the old value in one indivisible step, lock-free — that is how the
  nonce counter stays unique under concurrency.
- **BLAKE2s-256** — the 32-byte hash function used to derive each
  direction's key from PSK + index.
- **`derive_key`** — the function above: turns (label, index, PSK) into
  one direction's key.
- **`fetch_add(1)` / latch** — the `tx_counter` counter; the *latch* is
  the tripwire that, once fired, rejects every `seal` forever.
- **Headroom / tailroom** — space reserved before/after the payload in
  the buffer: 14B up front for the header, 16B at the back for the tag
  — no reallocating.
- **`--key <64-hex>`** — the lab PSK: 64 hex digits = 32 bytes, passed
  on both ends. Lab only.
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
- **`--sender-index` / `--peer-index`** — the session identifier
  stamped by the sender / expected by the receiver; it also selects
  which derived key to use.
- **`split_at_mut`** — Rust's slicer that splits a buffer into two
  disjoint mutable regions in constant time.
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

## Revisions

- `r0` — AEAD, replay window, nonce latch, symmetric ingress,
  `--key`/`--sender-index`/`--peer-index`/`--keepalive-secs`, split
  ingress metrics.
