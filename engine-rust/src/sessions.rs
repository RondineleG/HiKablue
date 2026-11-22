//! Multi-epoch session table shared between the forwarding loop and the
//! control socket.
//!
//! The table holds at most three roles of `SessionState` for the single lab
//! peer: the active epoch (seals outbound, decrypts inbound), one staged
//! epoch (decrypts inbound only, waiting for `Activate`) and any number of
//! retiring epochs (decrypt inbound only, waiting for `Retire`). All three
//! live in one `rx` map keyed by the `sender_index` the peer stamps, so the
//! packet path is a single lookup: late in-flight datagrams from the old
//! epoch and early datagrams from the staged epoch both find their keys.
//!
//! Mutations happen only through the control socket and are rare; readers
//! take a shared `RwLock` read and clone an `Arc`, so the hot path never
//! blocks on a writer and never allocates per packet.

use std::collections::HashMap;
use std::fmt;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, RwLock};

use crate::crypto::SessionState;

/// Role an epoch plays in the rotation protocol, reported by GetMetrics.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EpochRole {
    /// Seals outbound datagrams and decrypts inbound ones.
    Active,
    /// Decrypts inbound only, waiting for Activate.
    Staged,
    /// Decrypts inbound only, waiting for Retire.
    Retiring,
}

/// One row of the epoch snapshot exposed by GetMetrics and GetSessions.
pub struct EpochInfo {
    pub key_epoch: u64,
    /// Index this node stamps on outbound packets under this epoch.
    pub sender_index: u32,
    /// Index the peer stamps, i.e. the `rx` map key.
    pub peer_index: u32,
    pub role: EpochRole,
    pub tx_pkts: u64,
    pub rx_pkts: u64,
}

/// Why a control op was refused, mapped to an error code by the socket
/// layer.
#[derive(Debug)]
pub enum TableError {
    /// A payload field failed validation (zero index, equal indexes, wrong
    /// key length).
    BadInput(String),
    /// StagePeer while a different epoch is already staged. Retire it or
    /// Activate it first — there is only one staging slot.
    StagedPending(u64),
    /// Activate/Retire named an epoch the table does not hold, or the
    /// staged epoch does not match.
    UnknownEpoch(u64),
    /// Retire named the active epoch: only staged and retiring epochs may
    /// be retired.
    ActiveEpoch(u64),
    /// The requested peer_index already maps to another epoch — two epochs
    /// sharing an index could not be told apart on rx.
    IndexInUse(u32),
    /// RevokePeer named a lab_session_id the table does not hold.
    UnknownLabSession(u32),
}

impl fmt::Display for TableError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::BadInput(m) => write!(f, "{m}"),
            Self::StagedPending(e) => {
                write!(f, "epoch {e} already staged: activate or retire it first")
            }
            Self::UnknownEpoch(e) => write!(f, "no staged or retiring epoch {e}"),
            Self::ActiveEpoch(e) => write!(f, "epoch {e} is active and cannot be retired"),
            Self::IndexInUse(i) => write!(f, "peer_index {i} already maps to another epoch"),
            Self::UnknownLabSession(i) => {
                write!(f, "no peer with lab_session_id {i}")
            }
        }
    }
}

struct TableState {
    /// Seals outbound datagrams. `None` before the first Activate or after
    /// RevokePeer — the spec then blocks traffic until reprovisioning.
    tx: Option<Arc<SessionState>>,
    /// Epoch staged for the next Activate; already decrypts inbound so
    /// packets the peer starts sending under the new index land before
    /// the cut-over.
    staged: Option<Arc<SessionState>>,
    /// Every epoch still allowed on rx — active, staged and each one not
    /// yet retired — keyed by the sender_index the peer stamps.
    rx: HashMap<u32, Arc<SessionState>>,
    /// Numeric session id from the control contract; informational on the
    /// single-peer engine, checked by RevokePeer when present.
    lab_session_id: Option<u32>,
}

/// Shared session registry. The forwarding loop clones `Arc`s out of it;
/// the control task mutates it through the ops below.
pub struct SessionTable {
    state: RwLock<TableState>,
    /// Sticky "keyed engine" flag: once the control plane stages an epoch,
    /// plaintext mode is gone for good — a later RevokePeer blocks
    /// traffic instead of silently speaking cleartext again.
    provisioned: AtomicBool,
}

impl SessionTable {
    /// `initial` is the lab `--key` epoch-0 session; without it the table
    /// starts empty and the engine stays in the M2 plaintext mode until a
    /// StagePeer provisions the first managed epoch.
    pub fn new(initial: Option<SessionState>) -> Self {
        let mut rx = HashMap::new();
        let tx = initial.map(|session| {
            let session = Arc::new(session);
            rx.insert(session.peer_index(), session.clone());
            session
        });
        Self {
            state: RwLock::new(TableState {
                tx,
                staged: None,
                rx,
                lab_session_id: None,
            }),
            provisioned: AtomicBool::new(false),
        }
    }

    /// Whether the engine speaks the AEAD wire format: a lab `--key`
    /// session or a staged managed epoch. After RevokePeer this stays true
    /// and both directions block, per the spec's "block traffic until
    /// reprovisioning completes".
    pub fn keyed(&self) -> bool {
        self.provisioned.load(Ordering::Relaxed) || {
            let state = self.state.read().unwrap();
            state.tx.is_some() || !state.rx.is_empty()
        }
    }

    /// The active epoch for sealing, or `None` while none is active.
    pub fn tx(&self) -> Option<Arc<SessionState>> {
        self.state.read().unwrap().tx.clone()
    }

    pub fn has_tx(&self) -> bool {
        self.state.read().unwrap().tx.is_some()
    }

    /// The epoch that accepts `sender_index` on rx — active, staged or
    /// retiring.
    pub fn rx_lookup(&self, sender_index: u32) -> Option<Arc<SessionState>> {
        self.state.read().unwrap().rx.get(&sender_index).cloned()
    }

    /// StagePeer: allocate the next epoch into the staging slot and start
    /// accepting its `peer_index` on rx immediately. Idempotent against
    /// at-least-once redelivery: replaying the identical stage is a no-op.
    #[allow(clippy::too_many_arguments)]
    pub fn stage(
        &self,
        lab_session_id: Option<u32>,
        key_epoch: u64,
        sender_index: u32,
        peer_index: u32,
        tx_key: zeroize::Zeroizing<[u8; 32]>,
        rx_key: zeroize::Zeroizing<[u8; 32]>,
    ) -> Result<(), TableError> {
        if sender_index == 0 || peer_index == 0 {
            return Err(TableError::BadInput(
                "sender_index and peer_index must be nonzero".into(),
            ));
        }
        if sender_index == peer_index {
            return Err(TableError::BadInput(
                "sender_index and peer_index must differ".into(),
            ));
        }
        let mut state = self.state.write().unwrap();
        if let Some(staged) = &state.staged {
            // A replayed StagePeer for the same epoch carries the same
            // material and is accepted as a no-op; different material is a
            // conflicting provisioning attempt.
            let same = staged.key_epoch() == key_epoch
                && staged.local_index() == sender_index
                && staged.peer_index() == peer_index
                && **staged.tx_key() == *tx_key
                && **staged.rx_key() == *rx_key;
            if same {
                return Ok(());
            }
            return Err(TableError::StagedPending(staged.key_epoch()));
        }
        if state
            .tx
            .as_ref()
            .is_some_and(|active| active.key_epoch() == key_epoch)
            || state.rx.values().any(|s| s.key_epoch() == key_epoch)
        {
            return Err(TableError::BadInput(format!(
                "epoch {key_epoch} already exists in the table"
            )));
        }
        // The rx map key must be unique per epoch or a late packet could
        // not pick its key deterministically.
        if state.rx.contains_key(&peer_index) {
            return Err(TableError::IndexInUse(peer_index));
        }
        let session = Arc::new(SessionState::new(
            key_epoch,
            tx_key,
            rx_key,
            sender_index,
            peer_index,
        ));
        state.rx.insert(peer_index, session.clone());
        state.staged = Some(session);
        state.lab_session_id = lab_session_id;
        self.provisioned.store(true, Ordering::Relaxed);
        Ok(())
    }

    /// Activate: the staged epoch becomes the sealing epoch at a packet
    /// boundary. The previous active epoch stays in the `rx` map and
    /// becomes retiring automatically.
    pub fn activate(&self, key_epoch: u64) -> Result<u64, TableError> {
        let mut state = self.state.write().unwrap();
        // A mismatched Activate must not consume the staged epoch — the
        // agent may have sent the ops out of order.
        if state.staged.as_ref().map(|s| s.key_epoch()) != Some(key_epoch) {
            return Err(TableError::UnknownEpoch(key_epoch));
        }
        let staged = state.staged.take().unwrap();
        let previous = state.tx.replace(staged);
        Ok(previous.map(|s| s.key_epoch()).unwrap_or(0))
    }

    /// Retire: remove a non-active epoch from the `rx` map. Retiring the
    /// staged epoch unstages it (the drain for a rotation that never
    /// activated); retiring an old epoch ends its drain window.
    pub fn retire(&self, key_epoch: u64) -> Result<(), TableError> {
        let mut state = self.state.write().unwrap();
        if state
            .tx
            .as_ref()
            .is_some_and(|active| active.key_epoch() == key_epoch)
        {
            return Err(TableError::ActiveEpoch(key_epoch));
        }
        if let Some(staged) = &state.staged {
            if staged.key_epoch() == key_epoch {
                let staged = state.staged.take().unwrap();
                state.rx.remove(&staged.peer_index());
                return Ok(());
            }
        }
        let peer_index = state
            .rx
            .values()
            .find(|s| s.key_epoch() == key_epoch)
            .map(|s| s.peer_index())
            .ok_or(TableError::UnknownEpoch(key_epoch))?;
        state.rx.remove(&peer_index);
        Ok(())
    }

    /// RevokePeer: wipe every epoch — active, staged and retiring. The
    /// engine stays keyed, so traffic blocks until a fresh StagePeer +
    /// Activate reprovisions it.
    pub fn revoke(&self, lab_session_id: Option<u32>) -> Result<(), TableError> {
        let mut state = self.state.write().unwrap();
        if let (Some(stored), Some(asked)) = (state.lab_session_id, lab_session_id) {
            if stored != asked {
                return Err(TableError::UnknownLabSession(asked));
            }
        }
        state.tx = None;
        state.staged = None;
        state.rx.clear();
        state.lab_session_id = None;
        Ok(())
    }

    /// Ordered snapshot for metrics: active first, then staged, then
    /// retiring epochs by key_epoch.
    pub fn snapshot(&self) -> Vec<EpochInfo> {
        let state = self.state.read().unwrap();
        let mut rows: Vec<EpochInfo> = state
            .rx
            .values()
            .map(|s| {
                let role = if state.tx.as_ref().is_some_and(|tx| Arc::ptr_eq(tx, s)) {
                    EpochRole::Active
                } else if state.staged.as_ref().is_some_and(|st| Arc::ptr_eq(st, s)) {
                    EpochRole::Staged
                } else {
                    EpochRole::Retiring
                };
                EpochInfo {
                    key_epoch: s.key_epoch(),
                    sender_index: s.local_index(),
                    peer_index: s.peer_index(),
                    role,
                    tx_pkts: s.tx_pkts(),
                    rx_pkts: s.rx_pkts(),
                }
            })
            .collect();
        rows.sort_by(|a, b| {
            let rank = |r: EpochRole| match r {
                EpochRole::Active => 0,
                EpochRole::Staged => 1,
                EpochRole::Retiring => 2,
            };
            rank(a.role)
                .cmp(&rank(b.role))
                .then(a.key_epoch.cmp(&b.key_epoch))
        });
        rows
    }

    /// The lab_session_id staged by the control plane, if any.
    pub fn lab_session_id(&self) -> Option<u32> {
        self.state.read().unwrap().lab_session_id
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use zeroize::Zeroizing;

    const KEY_A: [u8; 32] = [1u8; 32];
    const KEY_B: [u8; 32] = [2u8; 32];
    const KEY_C: [u8; 32] = [3u8; 32];

    fn stage_args(
        epoch: u64,
        sender: u32,
        peer: u32,
        key: [u8; 32],
    ) -> (u64, u32, u32, Zeroizing<[u8; 32]>, Zeroizing<[u8; 32]>) {
        (
            epoch,
            sender,
            peer,
            Zeroizing::new(key),
            Zeroizing::new(key),
        )
    }

    #[test]
    fn empty_table_is_plaintext_until_provisioned() {
        let table = SessionTable::new(None);
        assert!(!table.keyed());
        assert!(table.tx().is_none());
        table
            .stage(
                Some(5),
                1,
                11,
                22,
                Zeroizing::new(KEY_A),
                Zeroizing::new(KEY_A),
            )
            .unwrap();
        assert!(table.keyed());
        // Staged but not activated: rx accepts, tx still blocked.
        assert!(table.rx_lookup(22).is_some());
        assert!(table.tx().is_none());
    }

    #[test]
    fn lab_key_installs_active_epoch_zero() {
        let table = SessionTable::new(Some(SessionState::from_lab_psk(
            Zeroizing::new(KEY_A),
            1,
            2,
        )));
        assert!(table.keyed());
        assert_eq!(table.tx().unwrap().key_epoch(), 0);
        assert!(table.rx_lookup(2).is_some());
    }

    #[test]
    fn stage_is_idempotent_on_replay() {
        let table = SessionTable::new(None);
        let (e, s, p, k1, k2) = stage_args(7, 11, 22, KEY_A);
        table.stage(Some(5), e, s, p, k1, k2).unwrap();
        table
            .stage(
                Some(5),
                e,
                s,
                p,
                Zeroizing::new(KEY_A),
                Zeroizing::new(KEY_A),
            )
            .unwrap();
        assert_eq!(table.snapshot().len(), 1);
        // Same epoch, different material: a conflicting delivery.
        assert!(matches!(
            table.stage(
                Some(5),
                e,
                s,
                p,
                Zeroizing::new(KEY_B),
                Zeroizing::new(KEY_B),
            ),
            Err(TableError::StagedPending(7))
        ));
    }

    #[test]
    fn rotation_keeps_both_epochs_on_rx() {
        let table = SessionTable::new(Some(SessionState::from_lab_psk(
            Zeroizing::new(KEY_A),
            1,
            2,
        )));
        let (e, s, p, k1, k2) = stage_args(1, 33, 44, KEY_B);
        table.stage(Some(9), e, s, p, k1, k2).unwrap();
        table.activate(1).unwrap();
        // New epoch seals; old epoch still decrypts during drain.
        assert_eq!(table.tx().unwrap().key_epoch(), 1);
        assert!(table.rx_lookup(44).is_some());
        assert!(table.rx_lookup(2).is_some());
        let snap = table.snapshot();
        assert_eq!(snap[0].role, EpochRole::Active);
        assert_eq!(snap[1].role, EpochRole::Retiring);
        // After Retire the old index is unknown again.
        table.retire(0).unwrap();
        assert!(table.rx_lookup(2).is_none());
        assert!(table.rx_lookup(44).is_some());
    }

    #[test]
    fn retire_rejects_active_and_unknown_epochs() {
        let table = SessionTable::new(Some(SessionState::from_lab_psk(
            Zeroizing::new(KEY_A),
            1,
            2,
        )));
        assert!(matches!(table.retire(0), Err(TableError::ActiveEpoch(0))));
        assert!(matches!(
            table.retire(99),
            Err(TableError::UnknownEpoch(99))
        ));
    }

    #[test]
    fn retire_on_staged_unstages_it() {
        let table = SessionTable::new(None);
        table
            .stage(
                Some(5),
                1,
                11,
                22,
                Zeroizing::new(KEY_A),
                Zeroizing::new(KEY_A),
            )
            .unwrap();
        table.retire(1).unwrap();
        assert!(table.rx_lookup(22).is_none());
        assert!(table.tx().is_none());
        // And a different epoch can be staged right away.
        table
            .stage(
                Some(5),
                2,
                33,
                44,
                Zeroizing::new(KEY_B),
                Zeroizing::new(KEY_B),
            )
            .unwrap();
    }

    #[test]
    fn stage_rejects_colliding_and_equal_indexes() {
        let table = SessionTable::new(Some(SessionState::from_lab_psk(
            Zeroizing::new(KEY_A),
            1,
            2,
        )));
        // peer_index 2 already maps to epoch 0.
        assert!(matches!(
            table.stage(
                Some(5),
                1,
                11,
                2,
                Zeroizing::new(KEY_B),
                Zeroizing::new(KEY_B),
            ),
            Err(TableError::IndexInUse(2))
        ));
        // Equal indexes would collapse the direction keys.
        assert!(matches!(
            table.stage(
                Some(5),
                1,
                7,
                7,
                Zeroizing::new(KEY_B),
                Zeroizing::new(KEY_B),
            ),
            Err(TableError::BadInput(_))
        ));
        // Zero indexes are rejected.
        assert!(matches!(
            table.stage(
                Some(5),
                1,
                0,
                8,
                Zeroizing::new(KEY_B),
                Zeroizing::new(KEY_B),
            ),
            Err(TableError::BadInput(_))
        ));
    }

    #[test]
    fn activate_requires_matching_staged_epoch() {
        let table = SessionTable::new(None);
        assert!(matches!(
            table.activate(1),
            Err(TableError::UnknownEpoch(1))
        ));
        table
            .stage(
                Some(5),
                1,
                11,
                22,
                Zeroizing::new(KEY_A),
                Zeroizing::new(KEY_A),
            )
            .unwrap();
        assert!(matches!(
            table.activate(9),
            Err(TableError::UnknownEpoch(9))
        ));
        // The staged epoch survives a mismatched Activate.
        table.activate(1).unwrap();
        assert_eq!(table.tx().unwrap().key_epoch(), 1);
    }

    #[test]
    fn revoke_wipes_everything_and_stays_keyed() {
        let table = SessionTable::new(Some(SessionState::from_lab_psk(
            Zeroizing::new(KEY_A),
            1,
            2,
        )));
        table
            .stage(
                Some(5),
                1,
                11,
                22,
                Zeroizing::new(KEY_B),
                Zeroizing::new(KEY_B),
            )
            .unwrap();
        table.revoke(Some(5)).unwrap();
        assert!(table.tx().is_none());
        assert!(table.rx_lookup(2).is_none());
        assert!(table.rx_lookup(22).is_none());
        // Traffic blocks until reprovisioning; no plaintext fallback.
        assert!(table.keyed());
        // A mismatched lab_session_id is refused and revokes nothing.
        table
            .stage(
                Some(7),
                2,
                33,
                44,
                Zeroizing::new(KEY_C),
                Zeroizing::new(KEY_C),
            )
            .unwrap();
        assert!(matches!(
            table.revoke(Some(8)),
            Err(TableError::UnknownLabSession(8))
        ));
        assert!(table.rx_lookup(44).is_some());
    }
}
