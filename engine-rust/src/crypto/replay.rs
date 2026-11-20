//! 2048-bit sliding anti-replay window over the wire nonce counter.
//!
//! `check` is a pure read-only pre-flight used before AEAD verification;
//! `update` runs strictly after the authentication tag verifies, so a
//! forged packet can never advance the window.

/// Number of sequence numbers the window tracks backwards from `last_seq`.
pub const WINDOW_SIZE: u64 = 2048;
const WORDS: usize = (WINDOW_SIZE / 64) as usize;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ReplayError {
    /// Sequence 0 is reserved and never valid.
    Reserved,
    /// The sequence was already authenticated once.
    Duplicate,
    /// The sequence is older than the replay horizon.
    Expired,
}

/// Sliding window: bit `i` of `bitmap` records whether the sequence
/// `last_seq - i` has already been accepted.
pub struct AntiReplayWindow {
    last_seq: u64,
    bitmap: [u64; WORDS],
}

impl Default for AntiReplayWindow {
    fn default() -> Self {
        Self::new()
    }
}

impl AntiReplayWindow {
    pub fn new() -> Self {
        Self {
            last_seq: 0,
            bitmap: [0; WORDS],
        }
    }

    /// Read-only verdict for `seq`. A `true` result only means the slot is
    /// free — the bit is set by `update` after authentication.
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

    /// Records `seq` as accepted. Call only after the packet's tag verified.
    pub fn update(&mut self, seq: u64) {
        if seq == 0 {
            return;
        }
        if seq > self.last_seq {
            self.slide(seq - self.last_seq);
            self.last_seq = seq;
            self.set_bit(0);
        } else {
            let diff = self.last_seq - seq;
            if diff < WINDOW_SIZE {
                self.set_bit(diff);
            }
        }
    }

    /// Clears the whole window — required on session rekey.
    #[allow(dead_code)] // spec'd API; exercised by tests, wired to rekey later
    pub fn reset(&mut self) {
        self.last_seq = 0;
        self.bitmap = [0; WORDS];
    }

    #[allow(dead_code)] // spec'd API; exercised by tests
    pub fn last_seq(&self) -> u64 {
        self.last_seq
    }

    fn bit(&self, idx: u64) -> bool {
        (self.bitmap[(idx / 64) as usize] >> (idx % 64)) & 1 == 1
    }

    fn set_bit(&mut self, idx: u64) {
        self.bitmap[(idx / 64) as usize] |= 1u64 << (idx % 64);
    }

    /// Shifts every recorded bit `delta` positions older. Bit 0 is the
    /// newest sequence, so a forward jump ages all previous entries.
    fn slide(&mut self, delta: u64) {
        if delta >= WINDOW_SIZE {
            self.bitmap = [0; WORDS];
            return;
        }
        let word_shift = (delta / 64) as usize;
        let bit_shift = (delta % 64) as u32;
        for i in (0..WORDS).rev() {
            let mut word = 0u64;
            if i >= word_shift {
                word = self.bitmap[i - word_shift] << bit_shift;
                if bit_shift != 0 && i > word_shift {
                    word |= self.bitmap[i - word_shift - 1] >> (64 - bit_shift);
                }
            }
            self.bitmap[i] = word;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn accepted(w: &mut AntiReplayWindow, seq: u64) {
        w.check(seq)
            .unwrap_or_else(|e| panic!("seq {seq} rejected: {e:?}"));
        w.update(seq);
    }

    #[test]
    fn in_order_progression_accepts_full_window() {
        let mut w = AntiReplayWindow::new();
        for seq in 1..=2048 {
            accepted(&mut w, seq);
        }
        assert_eq!(w.last_seq(), 2048);
    }

    #[test]
    fn out_of_order_inside_window_accepts_once_rejects_twice() {
        let mut w = AntiReplayWindow::new();
        accepted(&mut w, 100);
        accepted(&mut w, 95);
        accepted(&mut w, 98);
        assert_eq!(w.check(95), Err(ReplayError::Duplicate));
        assert_eq!(w.check(97), Ok(()));
        assert_eq!(w.check(100), Err(ReplayError::Duplicate));
    }

    #[test]
    fn boundary_minus_2047_passes_minus_2048_fails() {
        let mut w = AntiReplayWindow::new();
        accepted(&mut w, 3000);
        // Oldest in-window sequence: 3000 - 2047 = 953.
        assert_eq!(w.check(953), Ok(()));
        assert_eq!(w.check(952), Err(ReplayError::Expired));
    }

    #[test]
    fn leap_forward_invalidates_pre_jump_sequences() {
        let mut w = AntiReplayWindow::new();
        accepted(&mut w, 10);
        accepted(&mut w, 5010); // leap of 5000 > window size
        assert_eq!(w.check(10), Err(ReplayError::Expired));
        assert_eq!(w.check(5009), Ok(()));
    }

    #[test]
    fn partial_leap_ages_but_keeps_recent_entries() {
        let mut w = AntiReplayWindow::new();
        accepted(&mut w, 100);
        accepted(&mut w, 200); // slide 100 — seq 100 is now at bit 100
        assert_eq!(w.check(100), Err(ReplayError::Duplicate));
        assert_eq!(w.check(199), Ok(()));
    }

    #[test]
    fn sequence_zero_is_always_rejected() {
        let mut w = AntiReplayWindow::new();
        assert_eq!(w.check(0), Err(ReplayError::Reserved));
        accepted(&mut w, 50);
        assert_eq!(w.check(0), Err(ReplayError::Reserved));
        w.update(0); // update(0) is a no-op, never flips the newest bit
        assert_eq!(w.check(49), Ok(()));
    }

    #[test]
    fn reset_clears_everything() {
        let mut w = AntiReplayWindow::new();
        accepted(&mut w, 100);
        w.reset();
        assert_eq!(w.last_seq(), 0);
        accepted(&mut w, 5); // a pre-reset sequence is acceptable again
    }

    #[test]
    fn default_is_an_empty_window() {
        let w = AntiReplayWindow::default();
        assert_eq!(w.last_seq(), 0);
        assert_eq!(w.check(1), Ok(()));
    }

    #[test]
    fn check_does_not_consume_the_slot() {
        let mut w = AntiReplayWindow::new();
        accepted(&mut w, 7);
        // check() must be pure: two checks of the same free slot both pass.
        assert_eq!(w.check(6), Ok(()));
        assert_eq!(w.check(6), Ok(()));
        w.update(6);
        assert_eq!(w.check(6), Err(ReplayError::Duplicate));
    }
}
