//! Per-peer session state: directional keys, send counter and receive
//! anti-replay window.
//!
//! Sealing and opening are zero-copy: `seal` writes header + ciphertext +
//! tag over a plaintext that already sits past `HEADER_LEN` of headroom;
//! `open` authenticates and decrypts in place and returns the plaintext
//! slice inside the same buffer.

use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Mutex;

use chacha20poly1305::aead::{AeadInPlace, KeyInit};
use chacha20poly1305::{ChaCha20Poly1305, Key, Tag};
use zeroize::{Zeroize, Zeroizing};

use crate::wire::{frame, Header, HEADER_LEN, TAG_LEN};

use super::replay::{AntiReplayWindow, ReplayError};

/// Safety margin before `u64` wraparound: long before the counter could
/// repeat a nonce the session must rekey — a repeated nonce under the same
/// key is catastrophic keystream reuse.
pub const NONCE_EXHAUSTION_MARGIN: u64 = 65536;

/// Derives the direction key bound to one session index. The party using
/// `index` as its session ID seals with `KDF(psk ‖ index)` and its peer
/// opens with the same derivation — so the two directions never share a
/// key and the same counter value under each direction is safe (spec:
/// "separate keys per direction"). With identical indexes both derivations
/// coincide; `Config::validate` requires distinct indexes when keyed.
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

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CryptoError {
    /// The send counter is about to wrap; reject every further encryption
    /// until the session is rekeyed.
    NonceExhaustionNeedsRekey,
    /// AEAD tag verification failed (forgery, corruption or wrong key).
    Authentication,
    /// The datagram failed replay filtering before authentication.
    Replay(ReplayError),
    /// The wire header itself is malformed or unexpected.
    BadHeader,
    /// The sender_index does not match the configured peer.
    WrongPeerIndex,
}

impl std::fmt::Display for CryptoError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::NonceExhaustionNeedsRekey => {
                write!(f, "nonce exhaustion: session must be rekeyed")
            }
            Self::Authentication => write!(f, "authentication tag mismatch"),
            Self::Replay(e) => write!(f, "replay rejected: {e:?}"),
            Self::BadHeader => write!(f, "malformed wire header"),
            Self::WrongPeerIndex => write!(f, "unexpected sender_index"),
        }
    }
}

impl std::error::Error for CryptoError {}

pub struct SessionState {
    key_epoch: u64,
    /// Engine → peer (the control contract's egress_key).
    tx_key: Zeroizing<[u8; 32]>,
    /// Peer → engine (the control contract's ingress_key).
    rx_key: Zeroizing<[u8; 32]>,
    /// Index we stamp on outbound packets.
    local_index: u32,
    /// Index we require on inbound packets.
    peer_index: u32,
    tx_counter: AtomicU64,
    rx_replay: Mutex<AntiReplayWindow>,
}

impl SessionState {
    /// Starts fresh counters for already validated directional material.
    /// A rekey must replace the session with a new epoch, fresh keys and
    /// fresh wire indexes, never recreate it on a control-stream reconnect.
    pub fn new(
        key_epoch: u64,
        tx_key: Zeroizing<[u8; 32]>,
        rx_key: Zeroizing<[u8; 32]>,
        local_index: u32,
        peer_index: u32,
    ) -> Self {
        Self {
            key_epoch,
            tx_key,
            rx_key,
            local_index,
            peer_index,
            tx_counter: AtomicU64::new(0),
            rx_replay: Mutex::new(AntiReplayWindow::new()),
        }
    }

    pub fn from_lab_psk(psk: Zeroizing<[u8; 32]>, local_index: u32, peer_index: u32) -> Self {
        // Epoch zero labels static lab keys, not control-plane provisioning.
        Self::new(
            0,
            derive_key(&psk, local_index),
            derive_key(&psk, peer_index),
            local_index,
            peer_index,
        )
    }

    pub fn key_epoch(&self) -> u64 {
        self.key_epoch
    }

    /// Allocates the next outbound nonce or latches the session dead when
    /// the counter approaches wraparound.
    fn next_nonce(&self) -> Result<u64, CryptoError> {
        const LAST_NONCE: u64 = u64::MAX - NONCE_EXHAUSTION_MARGIN - 1;
        // Keep the last issued nonce at the ceiling; contending callers
        // cannot advance it or wrap around after exhaustion.
        self.tx_counter
            .try_update(Ordering::Relaxed, Ordering::Relaxed, |seq| {
                (seq < LAST_NONCE).then(|| seq + 1)
            })
            .map(|prev| prev + 1)
            .map_err(|_| CryptoError::NonceExhaustionNeedsRekey)
    }
    /// Encrypts `buf[HEADER_LEN..HEADER_LEN+len]` in place, prepends the
    /// header and appends the tag. Returns the total wire length.
    ///
    /// `message_type` is `MSG_DATA` or `MSG_KEEPALIVE`.
    pub fn seal(&self, buf: &mut [u8], len: usize, message_type: u8) -> Result<usize, CryptoError> {
        if buf.len() < HEADER_LEN + len + TAG_LEN {
            return Err(CryptoError::BadHeader);
        }
        let nonce_counter = self.next_nonce()?;
        let header = Header {
            message_type,
            sender_index: self.local_index,
            nonce: nonce_counter,
        };
        frame::encode(&header, &mut buf[..HEADER_LEN]);
        let cipher = ChaCha20Poly1305::new(Key::from_slice(&*self.tx_key));
        // Split the buffer: the encoded header stays borrowed as AAD while
        // the plaintext region encrypts in place; the tag lands right after.
        let (head, rest) = buf.split_at_mut(HEADER_LEN);
        let tag = cipher
            .encrypt_in_place_detached(
                frame::aead_nonce(nonce_counter).as_slice().into(),
                &head[..],
                &mut rest[..len],
            )
            .map_err(|_| CryptoError::Authentication)?;
        rest[len..len + TAG_LEN].copy_from_slice(&tag);
        Ok(HEADER_LEN + len + TAG_LEN)
    }

    /// Verifies and decrypts `buf[..wire_len]` in place. On success returns
    /// `(header, plaintext_len)` — plaintext occupies
    /// `buf[HEADER_LEN..HEADER_LEN+plaintext_len]`.
    ///
    /// Order matters: the replay slot is only claimed *after* the tag
    /// verifies, so a forgery cannot burn sequence numbers. The final
    /// check and the mark run under one lock — two concurrent `open`
    /// calls cannot both accept the same nonce.
    pub fn open(&self, buf: &mut [u8], wire_len: usize) -> Result<(Header, usize), CryptoError> {
        if wire_len > buf.len() || wire_len < HEADER_LEN + TAG_LEN {
            return Err(CryptoError::BadHeader);
        }
        let header = frame::parse(&buf[..HEADER_LEN]).map_err(|_| CryptoError::BadHeader)?;
        if header.sender_index != self.peer_index {
            return Err(CryptoError::WrongPeerIndex);
        }
        {
            let replay = self.rx_replay.lock().unwrap();
            replay.check(header.nonce).map_err(CryptoError::Replay)?;
        }
        let ct_len = wire_len - HEADER_LEN - TAG_LEN;
        // Borrow halves of the buffer separately: header+ciphertext for the
        // in-place decrypt (header doubles as AAD), tag region read-only.
        let (aad_and_ct, tag_region) = buf.split_at_mut(HEADER_LEN + ct_len);
        let (aad, ct_buf) = aad_and_ct.split_at_mut(HEADER_LEN);
        let tag = Tag::from_slice(&tag_region[..TAG_LEN]);
        let cipher = ChaCha20Poly1305::new(Key::from_slice(&*self.rx_key));
        cipher
            .decrypt_in_place_detached(
                frame::aead_nonce(header.nonce).as_slice().into(),
                aad,
                ct_buf,
                tag,
            )
            .map_err(|_| CryptoError::Authentication)?;
        // Post-auth acceptance is atomic: a second authenticated copy that
        // raced through the preliminary check re-fails here before the
        // bit can be marked, so each nonce is accepted exactly once.
        {
            let mut replay = self.rx_replay.lock().unwrap();
            replay.check(header.nonce).map_err(CryptoError::Replay)?;
            replay.update(header.nonce);
        }
        Ok((header, ct_len))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::wire::{MSG_DATA, MSG_KEEPALIVE};

    const KEY: [u8; 32] = [7u8; 32];

    fn lab_session(local_index: u32, peer_index: u32) -> SessionState {
        SessionState::from_lab_psk(Zeroizing::new(KEY), local_index, peer_index)
    }

    fn session() -> SessionState {
        lab_session(1, 1)
    }

    /// Plaintext sitting past the 14-byte headroom, as run_loop stages it.
    fn staged(msg: &[u8]) -> Vec<u8> {
        let mut buf = vec![0u8; HEADER_LEN + msg.len() + TAG_LEN + 64];
        buf[HEADER_LEN..HEADER_LEN + msg.len()].copy_from_slice(msg);
        buf
    }

    #[test]
    fn seal_then_open_roundtrips_in_place() {
        let a = session();
        let b = session();
        let msg = b"hello tunnel";
        let mut buf = staged(msg);
        let wire_len = a.seal(&mut buf, msg.len(), MSG_DATA).unwrap();
        assert_eq!(wire_len, HEADER_LEN + msg.len() + TAG_LEN);
        // Wire layout: version|type, reserved, session_id BE, counter BE.
        assert_eq!(buf[0], (crate::wire::frame::VERSION << 4) | MSG_DATA);
        assert_eq!(buf[1], 0);
        assert_eq!(&buf[2..6], &1u32.to_be_bytes());
        assert_eq!(&buf[6..14], &1u64.to_be_bytes()); // first nonce is 1

        let (header, pt_len) = b.open(&mut buf, wire_len).unwrap();
        assert_eq!(header.nonce, 1);
        assert_eq!(pt_len, msg.len());
        assert_eq!(&buf[HEADER_LEN..HEADER_LEN + pt_len], msg);
    }

    #[test]
    fn keepalive_seals_empty_payload() {
        let a = session();
        let b = session();
        let mut buf = staged(b"");
        let wire_len = a.seal(&mut buf, 0, MSG_KEEPALIVE).unwrap();
        assert_eq!(wire_len, HEADER_LEN + TAG_LEN);
        let (header, pt_len) = b.open(&mut buf, wire_len).unwrap();
        assert_eq!(header.message_type, MSG_KEEPALIVE);
        assert_eq!(pt_len, 0);
    }

    #[test]
    fn rfc8439_vector_matches_iv_mapping() {
        fn hex(s: &str) -> Vec<u8> {
            s.as_bytes()
                .chunks_exact(2)
                .map(|pair| u8::from_str_radix(std::str::from_utf8(pair).unwrap(), 16).unwrap())
                .collect()
        }
        // RFC 8439 Appendix A.5 uses its own AAD, not a HiKablue header.
        let key = hex("1c9240a5eb55d38af333888604f6b5f0473917c1402b80099dca5cbc207075c0");
        let aad = hex("f33388860000000000004e91");
        let tag = hex("eead9d67890cbb22392336fea1851f38");
        let mut ciphertext = hex(concat!(
            "64a0861575861af460f062c79be643bd5e805cfd345cf389f108670ac76c8cb2",
            "4c6cfc18755d43eea09ee94e382d26b0bdb7b73c321b0100d4f03b7f355894cf",
            "332f830e710b97ce98c8a84abd0b948114ad176e008d33bd60f982b1ff37c855",
            "9797a06ef4f0ef61c186324e2b3506383606907b6a7c02b0f9f6157b53c867e4",
            "b9166c767b804d46a59b5216cde7a4e99040c5a40433225ee282a1b0a06c523e",
            "af4534d7f83fa1155b0047718cbc546a0d072b04b3564eea1b422273f548271a",
            "0bb2316053fa76991955ebd63159434ecebb4e466dae5a1073a6727627097a10",
            "49e617d91d361094fa68f0ff77987130305beaba2eda04df997b714d6c6f2c29",
            "a6ad5cb4022b02709b"
        ));
        let iv = frame::aead_nonce(0x0807_0605_0403_0201);
        assert_eq!(iv, [0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]);
        let cipher = ChaCha20Poly1305::new(Key::from_slice(&key));
        cipher
            .decrypt_in_place_detached((&iv).into(), &aad, &mut ciphertext, Tag::from_slice(&tag))
            .unwrap();
        assert_eq!(
            ciphertext,
            b"Internet-Drafts are draft documents valid for a maximum of six months \
              and may be updated, replaced, or obsoleted by other documents at any time. \
              It is inappropriate to use Internet-Drafts as reference material or to cite \
              them other than as /\xe2\x80\x9cwork in progress./\xe2\x80\x9d"
        );
    }

    #[test]
    fn bitflip_in_header_fails_auth_and_leaves_replay_untouched() {
        let a = session();
        let b = session();
        let mut buf = staged(b"payload");
        let wire_len = a.seal(&mut buf, 7, MSG_DATA).unwrap();
        // A valid type flip changes AAD but not the nonce; a flip in the
        // high counter byte changes both. Neither may advance the window.
        for (offset, bit) in [(0, MSG_DATA ^ MSG_KEEPALIVE), (6, 0x80)] {
            let mut tampered = buf.clone();
            tampered[offset] ^= bit;
            assert_eq!(
                b.open(&mut tampered, wire_len),
                Err(CryptoError::Authentication)
            );
            assert_eq!(b.rx_replay.lock().unwrap().last_seq(), 0);
        }
        assert_eq!(b.open(&mut buf, wire_len).unwrap().0.nonce, 1);
    }

    #[test]
    fn replayed_ciphertext_is_rejected_as_duplicate() {
        let a = session();
        let b = session();
        let mut buf = staged(b"x");
        let wire_len = a.seal(&mut buf, 1, MSG_DATA).unwrap();
        let copy = buf[..wire_len].to_vec();
        b.open(&mut buf, wire_len).unwrap();
        let mut replayed = copy;
        assert!(matches!(
            b.open(&mut replayed, wire_len),
            Err(CryptoError::Replay(ReplayError::Duplicate))
        ));
    }

    #[test]
    fn nonce_exhaustion_latches_all_further_seals() {
        let sess = session();
        sess.tx_counter
            .store(u64::MAX - NONCE_EXHAUSTION_MARGIN - 2, Ordering::Relaxed);
        let mut buf = staged(b"z");
        // This seal consumes the last allowed nonce.
        assert!(sess.seal(&mut buf, 1, MSG_DATA).is_ok());
        // The next one trips the latch — and it never opens again.
        assert_eq!(
            sess.seal(&mut buf, 1, MSG_DATA),
            Err(CryptoError::NonceExhaustionNeedsRekey)
        );
        assert_eq!(
            sess.seal(&mut buf, 1, MSG_DATA),
            Err(CryptoError::NonceExhaustionNeedsRekey)
        );
    }

    #[test]
    fn concurrent_nonce_allocation_stops_at_the_ceiling() {
        let session = session();
        let last = u64::MAX - NONCE_EXHAUSTION_MARGIN - 1;
        session.tx_counter.store(last - 2, Ordering::Relaxed);
        let barrier = std::sync::Barrier::new(8);
        let results = std::thread::scope(|scope| {
            let (session, barrier) = (&session, &barrier);
            let handles: Vec<_> = (0..8)
                .map(|_| {
                    scope.spawn(|| {
                        barrier.wait();
                        session.next_nonce()
                    })
                })
                .collect();
            handles
                .into_iter()
                .map(|handle| handle.join().unwrap())
                .collect::<Vec<_>>()
        });
        let mut issued: Vec<_> = results.iter().filter_map(|result| result.ok()).collect();
        issued.sort_unstable();
        assert_eq!(issued, [last - 1, last]);
        assert!(results
            .into_iter()
            .all(|result| result.is_ok() || result == Err(CryptoError::NonceExhaustionNeedsRekey)));
        assert_eq!(session.tx_counter.load(Ordering::Relaxed), last);
    }

    #[test]
    fn seal_rejects_buffer_without_headroom_or_tag_room() {
        let sess = session();
        let mut short = [0u8; HEADER_LEN + 4 + TAG_LEN - 1];
        assert_eq!(
            sess.seal(&mut short, 4, MSG_DATA),
            Err(CryptoError::BadHeader)
        );
    }

    #[test]
    fn errors_have_distinct_display_texts() {
        let variants = [
            CryptoError::NonceExhaustionNeedsRekey,
            CryptoError::Authentication,
            CryptoError::Replay(ReplayError::Duplicate),
            CryptoError::BadHeader,
            CryptoError::WrongPeerIndex,
        ];
        let texts: Vec<String> = variants.iter().map(|v| v.to_string()).collect();
        for t in &texts {
            assert!(!t.is_empty());
        }
        assert!(texts[0].contains("rekey"));
        assert!(texts[2].contains("replay"));
        // CryptoError is a std::error::Error.
        let _: &dyn std::error::Error = &CryptoError::BadHeader;
    }

    #[test]
    fn wrong_peer_index_is_rejected_before_decrypt() {
        let a = lab_session(9, 1);
        let b = session(); // expects peer_index 1
        let mut buf = staged(b"m");
        let wire_len = a.seal(&mut buf, 1, MSG_DATA).unwrap();
        assert_eq!(b.open(&mut buf, wire_len), Err(CryptoError::WrongPeerIndex));
    }

    #[test]
    fn short_and_oversized_wire_inputs_are_rejected() {
        let b = session();
        let mut small = [0u8; HEADER_LEN + TAG_LEN - 1];
        let small_len = small.len();
        assert_eq!(b.open(&mut small, small_len), Err(CryptoError::BadHeader));
        let mut buf = [0u8; 64];
        assert_eq!(b.open(&mut buf, 200), Err(CryptoError::BadHeader));
    }

    #[test]
    fn direction_keys_differ_per_index() {
        // The two directions must not share a key: the derivation binds
        // the key to the sender's index, so sealing under index 1 opens
        // for the peer expecting 1 — and both directions roundtrip.
        assert_ne!(*derive_key(&KEY, 1), *derive_key(&KEY, 2));
        let a = lab_session(1, 2);
        let b = lab_session(2, 1);
        let mut buf = staged(b"dir");
        let wire_len = a.seal(&mut buf, 3, MSG_DATA).unwrap();
        let (header, pt) = b.open(&mut buf, wire_len).unwrap();
        assert_eq!(header.sender_index, 1);
        assert_eq!(pt, 3);
        // …and back the other way under the second derived key.
        let mut buf2 = staged(b"rid");
        let w2 = b.seal(&mut buf2, 3, MSG_DATA).unwrap();
        let (header2, pt2) = a.open(&mut buf2, w2).unwrap();
        assert_eq!(header2.sender_index, 2);
        assert_eq!(pt2, 3);
    }

    #[test]
    fn concurrent_open_accepts_each_nonce_once() {
        // Two copies of the same authenticated datagram raced through
        // open(): exactly one succeeds, the other gets Duplicate — the
        // post-authentication check+update runs under one lock.
        let sealer = lab_session(2, 1);
        let rx = lab_session(1, 2);
        let mut buf = staged(b"race");
        let wire_len = sealer.seal(&mut buf, 4, MSG_DATA).unwrap();
        let wire: Vec<u8> = buf[..wire_len].to_vec();
        let barrier = std::sync::Barrier::new(2);
        let results: Vec<Result<usize, CryptoError>> = std::thread::scope(|s| {
            let mut handles = Vec::new();
            for _ in 0..2 {
                let (rx, barrier, wire) = (&rx, &barrier, &wire);
                handles.push(s.spawn(move || {
                    let mut local = wire.clone();
                    barrier.wait();
                    rx.open(&mut local, wire.len()).map(|(h, l)| {
                        assert_eq!(h.nonce, 1);
                        l
                    })
                }));
            }
            handles.into_iter().map(|h| h.join().unwrap()).collect()
        });
        assert_eq!(results.iter().filter(|r| r.is_ok()).count(), 1);
        assert_eq!(
            results
                .iter()
                .filter(|r| **r == Err(CryptoError::Replay(ReplayError::Duplicate)))
                .count(),
            1
        );
        // Distinct nonces opened concurrently all pass — no deadlock and
        // no false positives from the shared window lock.
        let wins: Vec<Vec<u8>> = (0..8)
            .map(|_| {
                let mut b = staged(b"n");
                let l = sealer.seal(&mut b, 1, MSG_DATA).unwrap();
                b[..l].to_vec()
            })
            .collect();
        std::thread::scope(|s| {
            let mut handles = Vec::new();
            for w in &wins {
                let rx = &rx;
                handles.push(s.spawn(move || {
                    let mut local = w.clone();
                    rx.open(&mut local, w.len())
                }));
            }
            for h in handles {
                assert!(h.join().unwrap().is_ok());
            }
        });
    }

    #[test]
    fn replacement_keeps_full_epoch_and_uses_fresh_directional_keys() {
        let epoch = u64::from(u32::MAX) + 1;
        let make = |epoch, tx, rx, local, peer| {
            SessionState::new(
                epoch,
                Zeroizing::new([tx; 32]),
                Zeroizing::new([rx; 32]),
                local,
                peer,
            )
        };
        let mut sender = make(epoch, 3, 4, 1, 2);
        let mut receiver = make(epoch, 4, 3, 2, 1);
        let mut old_wire = staged(b"x");
        let old_len = sender.seal(&mut old_wire, 1, MSG_DATA).unwrap();
        assert!(receiver.open(&mut old_wire.clone(), old_len).is_ok());

        sender = make(epoch + 1, 5, 6, 3, 4);
        receiver = make(epoch + 1, 6, 5, 4, 3);
        assert_eq!(sender.key_epoch(), epoch + 1);
        assert_eq!(receiver.key_epoch(), epoch + 1);
        assert_eq!(
            receiver.open(&mut old_wire, old_len),
            Err(CryptoError::WrongPeerIndex)
        );

        for (tx, rx) in [(&sender, &receiver), (&receiver, &sender)] {
            let mut fresh = staged(b"y");
            let len = tx.seal(&mut fresh, 1, MSG_DATA).unwrap();
            assert_eq!(rx.open(&mut fresh, len).unwrap().0.nonce, 1);
            assert_eq!(fresh[HEADER_LEN], b'y');
        }
    }
}
