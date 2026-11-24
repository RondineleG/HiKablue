//! Multi-peer, multi-epoch session table shared by the forwarding loop and
//! the control socket.
//!
//! Each managed peer (keyed by `lab_session_id`) owns its key epochs,
//! virtual IP and learned outer endpoint. A global rx map keyed by the wire
//! `sender_index` the peer stamps dispatches inbound datagrams to the right
//! epoch without touching peer state for other clients. Mutations are rare
//! and serialized by one `RwLock`; the packet path clones `Arc`s and never
//! blocks on epoch ownership.

use std::collections::{HashMap, HashSet};
use std::fmt;
use std::net::{Ipv4Addr, SocketAddr};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, RwLock};

use crate::crypto::SessionState;

fn key_fingerprint(key: &[u8; 32]) -> [u8; 32] {
    use blake2::Digest;
    let mut h = blake2::Blake2s256::new();
    h.update(b"hikablue-retired-key");
    h.update(key);
    h.finalize().into()
}

#[derive(Clone)]
struct Tombstone {
    lab_session_id: u32,
    key_epoch: u64,
    tx_fp: [u8; 32],
    rx_fp: [u8; 32],
}

impl Tombstone {
    fn of(lab_session_id: u32, session: &SessionState) -> Self {
        Self {
            lab_session_id,
            key_epoch: session.key_epoch(),
            tx_fp: key_fingerprint(session.tx_key()),
            rx_fp: key_fingerprint(session.rx_key()),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EpochRole {
    Active,
    Staged,
    Retiring,
}

pub struct EpochInfo {
    pub key_epoch: u64,
    pub sender_index: u32,
    pub peer_index: u32,
    pub role: EpochRole,
    pub tx_pkts: u64,
    pub rx_pkts: u64,
}

pub struct PeerInfo {
    pub lab_session_id: u32,
    pub virtual_ip: Option<Ipv4Addr>,
    pub endpoint: Option<SocketAddr>,
    pub epochs: Vec<EpochInfo>,
}

/// Inbound lookup result: the peer metadata and the epoch that accepts the
/// wire index. The endpoint is copied so the forwarding loop can enforce
/// pinning before spending AEAD work on a cross-endpoint replay.
pub struct PeerRx {
    pub lab_session_id: u32,
    pub virtual_ip: Option<Ipv4Addr>,
    pub endpoint: Option<SocketAddr>,
    pub session: Arc<SessionState>,
}

/// Outbound lookup result for one virtual IP.
pub struct PeerTx {
    pub endpoint: Option<SocketAddr>,
    pub session: Arc<SessionState>,
}

pub struct StagePeerRequest {
    pub lab_session_id: Option<u32>,
    pub virtual_ip: Option<Ipv4Addr>,
    pub key_epoch: u64,
    pub sender_index: u32,
    pub peer_index: u32,
    pub tx_key: zeroize::Zeroizing<[u8; 32]>,
    pub rx_key: zeroize::Zeroizing<[u8; 32]>,
}

#[derive(Debug)]
pub enum TableError {
    BadInput(String),
    StagedPending(u64),
    UnknownEpoch(u64),
    ActiveEpoch(u64),
    IndexInUse(u32),
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
            Self::UnknownLabSession(i) => write!(f, "no peer with lab_session_id {i}"),
        }
    }
}

struct PeerState {
    virtual_ip: Option<Ipv4Addr>,
    tx: Option<Arc<SessionState>>,
    staged: Option<Arc<SessionState>>,
    endpoint: Option<SocketAddr>,
}

struct TableState {
    peers: HashMap<u32, PeerState>,
    /// Wire peer_index -> (lab_session_id, epoch). Indexes are global and
    /// one-shot so one wire index can never move between peers or epochs.
    rx: HashMap<u32, (u32, Arc<SessionState>)>,
    retired: Vec<Tombstone>,
    used_indexes: HashSet<u32>,
}

pub struct SessionTable {
    state: RwLock<TableState>,
    managed: bool,
    provisioned: AtomicBool,
    revocations: AtomicU64,
    backend_instance_id: String,
}

fn random_backend_instance_id() -> String {
    use std::io::Read;
    let mut bytes = [0u8; 16];
    if std::fs::File::open("/dev/urandom")
        .and_then(|mut f| f.read_exact(&mut bytes))
        .is_err()
    {
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default();
        let seed = (now.as_nanos() as u64) ^ ((std::process::id() as u64) << 32);
        bytes[..8].copy_from_slice(&seed.to_be_bytes());
        bytes[8..].copy_from_slice(&seed.rotate_left(17).to_be_bytes());
    }
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

impl SessionTable {
    /// `managed=true` means a control socket will provision the engine: it
    /// speaks the keyed wire format even before the first peer exists.
    /// Static `--key` sessions use lab id 0 and remain backward compatible.
    pub fn new(initial: Option<SessionState>, virtual_ip: Option<Ipv4Addr>, managed: bool) -> Self {
        let keyed = initial.is_some();
        let mut peers = HashMap::new();
        let mut rx = HashMap::new();
        let mut used_indexes = HashSet::new();
        if let Some(session) = initial {
            used_indexes.insert(session.local_index());
            used_indexes.insert(session.peer_index());
            let session = Arc::new(session);
            rx.insert(session.peer_index(), (0, session.clone()));
            peers.insert(
                0,
                PeerState {
                    virtual_ip,
                    tx: Some(session),
                    staged: None,
                    endpoint: None,
                },
            );
        }
        Self {
            state: RwLock::new(TableState {
                peers,
                rx,
                retired: Vec::new(),
                used_indexes,
            }),
            managed,
            provisioned: AtomicBool::new(keyed),
            revocations: AtomicU64::new(0),
            backend_instance_id: random_backend_instance_id(),
        }
    }

    pub fn backend_instance_id(&self) -> &str {
        &self.backend_instance_id
    }

    pub fn keyed(&self) -> bool {
        self.managed || self.provisioned.load(Ordering::Relaxed) || {
            let state = self.state.read().unwrap();
            state
                .peers
                .values()
                .any(|p| p.tx.is_some() || p.staged.is_some())
        }
    }

    pub fn rx_lookup(&self, sender_index: u32) -> Option<PeerRx> {
        let state = self.state.read().unwrap();
        let (lab, session) = state.rx.get(&sender_index)?;
        let peer = state.peers.get(lab)?;
        Some(PeerRx {
            lab_session_id: *lab,
            virtual_ip: peer.virtual_ip,
            endpoint: peer.endpoint,
            session: session.clone(),
        })
    }

    /// Active tx epoch for exactly one provisioned peer. Client mode uses
    /// this because every inner destination leaves through the same tunnel.
    pub fn single_tx(&self) -> Option<PeerTx> {
        let state = self.state.read().unwrap();
        if state.peers.len() != 1 {
            return None;
        }
        let (_, peer) = state.peers.iter().next()?;
        Some(PeerTx {
            endpoint: peer.endpoint,
            session: peer.tx.clone()?,
        })
    }

    /// Every peer with an active epoch and a learned endpoint. Used only by
    /// the keepalive timer, never by the per-packet hot path.
    pub fn active_targets(&self) -> Vec<PeerTx> {
        self.state
            .read()
            .unwrap()
            .peers
            .values()
            .filter_map(|peer| {
                Some(PeerTx {
                    endpoint: Some(peer.endpoint?),
                    session: peer.tx.clone()?,
                })
            })
            .collect()
    }

    /// Active tx epoch for the managed peer that owns `virtual_ip`.
    pub fn tx_for_virtual_ip(&self, virtual_ip: Ipv4Addr) -> Option<PeerTx> {
        let state = self.state.read().unwrap();
        let (_, peer) = state
            .peers
            .iter()
            .find(|(_, p)| p.virtual_ip == Some(virtual_ip))?;
        Some(PeerTx {
            endpoint: peer.endpoint,
            session: peer.tx.clone()?,
        })
    }

    pub fn learn_endpoint(&self, lab_session_id: u32, endpoint: SocketAddr) -> Option<SocketAddr> {
        let mut state = self.state.write().unwrap();
        let peer = state.peers.get_mut(&lab_session_id)?;
        if peer.endpoint.is_none() {
            peer.endpoint = Some(endpoint);
        }
        peer.endpoint
    }

    pub fn endpoint_count(&self) -> usize {
        self.state
            .read()
            .unwrap()
            .peers
            .values()
            .filter(|p| p.endpoint.is_some())
            .count()
    }

    pub fn is_peer_virtual_ip(&self, ip: Ipv4Addr) -> bool {
        self.state
            .read()
            .unwrap()
            .peers
            .values()
            .any(|p| p.virtual_ip == Some(ip))
    }

    /// Stage a new epoch for one peer. Replaying the identical StagePeer is
    /// a no-op, which makes at-least-once delivery safe.
    pub fn stage(&self, req: StagePeerRequest) -> Result<(), TableError> {
        if req.sender_index == 0 || req.peer_index == 0 {
            return Err(TableError::BadInput(
                "sender_index and peer_index must be nonzero".into(),
            ));
        }
        if req.sender_index == req.peer_index {
            return Err(TableError::BadInput(
                "sender_index and peer_index must differ".into(),
            ));
        }
        if *req.tx_key == *req.rx_key {
            return Err(TableError::BadInput("tx_key and rx_key must differ".into()));
        }
        let lab = req.lab_session_id.unwrap_or(0);
        let mut state = self.state.write().unwrap();

        // Static `--key` development engines historically had the implicit
        // lab id 0. When a controller provisions the first explicit lab id,
        // adopt that id for the existing single static peer so old socket
        // clients and rotation tests keep their epoch-0 behavior.
        if lab != 0 && !state.peers.contains_key(&lab) && state.peers.len() == 1 {
            if let Some(peer0) = state.peers.remove(&0) {
                if peer0.endpoint.is_none() {
                    for owner in state.rx.values_mut() {
                        if owner.0 == 0 {
                            owner.0 = lab;
                        }
                    }
                    state.peers.insert(lab, peer0);
                } else {
                    state.peers.insert(0, peer0);
                }
            }
        }

        if let Some(peer) = state.peers.get(&lab) {
            let same_as = |session: &Arc<SessionState>| {
                session.key_epoch() == req.key_epoch
                    && session.local_index() == req.sender_index
                    && session.peer_index() == req.peer_index
                    && **session.tx_key() == *req.tx_key
                    && **session.rx_key() == *req.rx_key
            };
            // At-least-once delivery can replay StagePeer after Activate
            // when the first ACK was lost. The exact active epoch is the
            // same command result, not a key reuse attempt.
            if peer.tx.as_ref().is_some_and(same_as) {
                return Ok(());
            }
            if let Some(staged) = &peer.staged {
                if same_as(staged) {
                    return Ok(());
                }
                return Err(TableError::StagedPending(staged.key_epoch()));
            }
            if peer.virtual_ip.is_some()
                && req.virtual_ip.is_some()
                && peer.virtual_ip != req.virtual_ip
            {
                return Err(TableError::BadInput(format!(
                    "virtual_ip conflicts with the provisioned peer {lab}"
                )));
            }
        }
        if let Some(vip) = req.virtual_ip {
            if state
                .peers
                .iter()
                .any(|(id, p)| *id != lab && p.virtual_ip == Some(vip))
            {
                return Err(TableError::BadInput(format!(
                    "virtual_ip {vip} already belongs to another peer"
                )));
            }
        }
        // Epochs are control-plane-unique on this backend. Keeping them
        // globally unique avoids ambiguity in Activate/Retire ops.
        if state.peers.values().any(|p| {
            p.tx.as_ref()
                .is_some_and(|s| s.key_epoch() == req.key_epoch)
                || p.staged
                    .as_ref()
                    .is_some_and(|s| s.key_epoch() == req.key_epoch)
        }) || state.retired.iter().any(|t| t.key_epoch == req.key_epoch)
        {
            return Err(TableError::BadInput(format!(
                "epoch {} already exists or was retired",
                req.key_epoch
            )));
        }
        if state.rx.contains_key(&req.peer_index) {
            return Err(TableError::IndexInUse(req.peer_index));
        }
        if state.used_indexes.contains(&req.sender_index)
            || state.used_indexes.contains(&req.peer_index)
        {
            return Err(TableError::BadInput(
                "sender_index/peer_index already used by an earlier epoch".into(),
            ));
        }
        let tx_fp = key_fingerprint(&req.tx_key);
        let rx_fp = key_fingerprint(&req.rx_key);
        let fp_is_fresh = |fp: &[u8; 32]| {
            !state
                .retired
                .iter()
                .any(|t| t.tx_fp == *fp || t.rx_fp == *fp)
                && !state.rx.values().any(|(_, s)| {
                    key_fingerprint(s.tx_key()) == *fp || key_fingerprint(s.rx_key()) == *fp
                })
        };
        if !fp_is_fresh(&tx_fp) || !fp_is_fresh(&rx_fp) {
            return Err(TableError::BadInput(
                "key material already used by an earlier epoch".into(),
            ));
        }

        let session = Arc::new(SessionState::new(
            req.key_epoch,
            req.tx_key,
            req.rx_key,
            req.sender_index,
            req.peer_index,
        ));
        state.rx.insert(req.peer_index, (lab, session.clone()));
        let peer = state.peers.entry(lab).or_insert(PeerState {
            virtual_ip: req.virtual_ip,
            tx: None,
            staged: None,
            endpoint: None,
        });
        if peer.virtual_ip.is_none() {
            peer.virtual_ip = req.virtual_ip;
        }
        peer.staged = Some(session);
        state.used_indexes.insert(req.sender_index);
        state.used_indexes.insert(req.peer_index);
        self.provisioned.store(true, Ordering::Relaxed);
        Ok(())
    }

    fn peer_for_op(
        state: &mut TableState,
        lab_session_id: Option<u32>,
    ) -> Result<(u32, &mut PeerState), TableError> {
        let lab = match lab_session_id {
            Some(lab) => lab,
            None if state.peers.len() == 1 => *state.peers.keys().next().unwrap(),
            None => 0,
        };
        match state.peers.get_mut(&lab) {
            Some(peer) => Ok((lab, peer)),
            None => Err(TableError::UnknownLabSession(lab)),
        }
    }

    pub fn activate(&self, lab_session_id: Option<u32>, key_epoch: u64) -> Result<u64, TableError> {
        let mut state = self.state.write().unwrap();
        let (_, peer) = Self::peer_for_op(&mut state, lab_session_id)?;
        if peer
            .tx
            .as_ref()
            .is_some_and(|active| active.key_epoch() == key_epoch)
        {
            return Ok(key_epoch);
        }
        if peer.staged.as_ref().map(|s| s.key_epoch()) != Some(key_epoch) {
            return Err(TableError::UnknownEpoch(key_epoch));
        }
        let staged = peer.staged.take().unwrap();
        let previous = peer.tx.replace(staged);
        Ok(previous.map(|s| s.key_epoch()).unwrap_or(0))
    }

    pub fn retire(&self, lab_session_id: Option<u32>, key_epoch: u64) -> Result<(), TableError> {
        let mut state = self.state.write().unwrap();
        let (lab, peer) = Self::peer_for_op(&mut state, lab_session_id)?;
        if peer
            .tx
            .as_ref()
            .is_some_and(|active| active.key_epoch() == key_epoch)
        {
            return Err(TableError::ActiveEpoch(key_epoch));
        }
        if peer
            .staged
            .as_ref()
            .is_some_and(|s| s.key_epoch() == key_epoch)
        {
            let staged = peer.staged.take().unwrap();
            state.rx.remove(&staged.peer_index());
            state.retired.push(Tombstone::of(lab, &staged));
            return Ok(());
        }
        let victim = state
            .rx
            .values()
            .find(|(owner, s)| *owner == lab && s.key_epoch() == key_epoch)
            .map(|(_, s)| (s.peer_index(), s.clone()));
        match victim {
            Some((index, session)) => {
                state.rx.remove(&index);
                state.retired.push(Tombstone::of(lab, &session));
                Ok(())
            }
            None if state
                .retired
                .iter()
                .any(|t| t.lab_session_id == lab && t.key_epoch == key_epoch) =>
            {
                Ok(())
            }
            None => Err(TableError::UnknownEpoch(key_epoch)),
        }
    }

    /// Remove exactly one peer. Replaying a revocation after it succeeded
    /// is confirmed as a no-op because the lab id remains tombstoned.
    pub fn revoke(&self, lab_session_id: Option<u32>) -> Result<(), TableError> {
        let mut state = self.state.write().unwrap();
        let lab = match lab_session_id {
            Some(lab) => lab,
            None if state.peers.len() == 1 => *state.peers.keys().next().unwrap(),
            None => 0,
        };
        let Some(peer) = state.peers.remove(&lab) else {
            return if state.retired.iter().any(|t| t.lab_session_id == lab) {
                Ok(())
            } else {
                Err(TableError::UnknownLabSession(lab))
            };
        };
        let mut dead: Vec<Arc<SessionState>> = Vec::new();
        if let Some(tx) = peer.tx {
            dead.push(tx);
        }
        if let Some(staged) = peer.staged {
            dead.push(staged);
        }
        let indexes: Vec<u32> = state
            .rx
            .iter()
            .filter(|(_, (owner, _))| *owner == lab)
            .map(|(index, _)| *index)
            .collect();
        for index in indexes {
            if let Some((_, session)) = state.rx.remove(&index) {
                dead.push(session);
            }
        }
        for session in dead {
            if !state
                .retired
                .iter()
                .any(|t| t.lab_session_id == lab && t.key_epoch == session.key_epoch())
            {
                state.retired.push(Tombstone::of(lab, &session));
            }
        }
        self.revocations.fetch_add(1, Ordering::Relaxed);
        Ok(())
    }

    pub fn revocations(&self) -> u64 {
        self.revocations.load(Ordering::Relaxed)
    }

    pub fn peers_snapshot(&self) -> Vec<PeerInfo> {
        let state = self.state.read().unwrap();
        let mut peers: Vec<_> = state
            .peers
            .iter()
            .map(|(lab, peer)| {
                let mut epochs: Vec<EpochInfo> = state
                    .rx
                    .values()
                    .filter(|(owner, _)| owner == lab)
                    .map(|(_, s)| {
                        let role = if peer.tx.as_ref().is_some_and(|tx| Arc::ptr_eq(tx, s)) {
                            EpochRole::Active
                        } else if peer.staged.as_ref().is_some_and(|st| Arc::ptr_eq(st, s)) {
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
                epochs.sort_by(|a, b| {
                    let rank = |r: EpochRole| match r {
                        EpochRole::Active => 0,
                        EpochRole::Staged => 1,
                        EpochRole::Retiring => 2,
                    };
                    rank(a.role)
                        .cmp(&rank(b.role))
                        .then(a.key_epoch.cmp(&b.key_epoch))
                });
                PeerInfo {
                    lab_session_id: *lab,
                    virtual_ip: peer.virtual_ip,
                    endpoint: peer.endpoint,
                    epochs,
                }
            })
            .collect();
        peers.sort_by_key(|p| p.lab_session_id);
        peers
    }

    /// Compatibility view used by metrics: all epochs across all peers.
    pub fn snapshot(&self) -> Vec<EpochInfo> {
        self.peers_snapshot()
            .into_iter()
            .flat_map(|p| p.epochs)
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use zeroize::Zeroizing;

    const A: [u8; 32] = [1u8; 32];
    const B: [u8; 32] = [2u8; 32];
    const C: [u8; 32] = [3u8; 32];
    const D: [u8; 32] = [4u8; 32];

    fn req(
        lab: u32,
        epoch: u64,
        sender: u32,
        peer: u32,
        tx: [u8; 32],
        rx: [u8; 32],
    ) -> StagePeerRequest {
        StagePeerRequest {
            lab_session_id: Some(lab),
            virtual_ip: Some(Ipv4Addr::new(10, 80, 1, (lab + 1) as u8)),
            key_epoch: epoch,
            sender_index: sender,
            peer_index: peer,
            tx_key: Zeroizing::new(tx),
            rx_key: Zeroizing::new(rx),
        }
    }

    #[test]
    fn empty_unmanaged_table_is_plaintext_but_managed_is_keyed() {
        assert!(!SessionTable::new(None, None, false).keyed());
        assert!(SessionTable::new(None, None, true).keyed());
    }

    #[test]
    fn two_peers_stage_activate_and_route_independently() {
        let t = SessionTable::new(None, None, true);
        t.stage(req(1, 1, 11, 12, A, B)).unwrap();
        t.stage(req(2, 2, 21, 22, C, D)).unwrap();
        t.activate(Some(1), 1).unwrap();
        t.activate(Some(2), 2).unwrap();
        assert_eq!(t.rx_lookup(12).unwrap().lab_session_id, 1);
        assert_eq!(t.rx_lookup(22).unwrap().lab_session_id, 2);
        assert!(t.single_tx().is_none());
        let vip2 = Ipv4Addr::new(10, 80, 1, 3);
        assert!(t.tx_for_virtual_ip(vip2).is_some());
    }

    #[test]
    fn endpoint_learning_is_per_peer_and_revoke_removes_only_that_peer() {
        let t = SessionTable::new(None, None, true);
        t.stage(req(1, 1, 11, 12, A, B)).unwrap();
        t.stage(req(2, 2, 21, 22, C, D)).unwrap();
        t.activate(Some(1), 1).unwrap();
        t.activate(Some(2), 2).unwrap();
        let ep1: SocketAddr = "192.0.2.1:5000".parse().unwrap();
        let ep2: SocketAddr = "192.0.2.2:5000".parse().unwrap();
        assert_eq!(t.learn_endpoint(1, ep1), Some(ep1));
        assert_eq!(t.learn_endpoint(2, ep2), Some(ep2));
        assert_eq!(t.endpoint_count(), 2);
        t.revoke(Some(1)).unwrap();
        assert!(t.rx_lookup(12).is_none());
        assert_eq!(t.revocations(), 1);
        assert_eq!(t.endpoint_count(), 1);
        assert!(t.rx_lookup(22).is_some());
        // Redelivery is an idempotent success; a never-seen id is not.
        t.revoke(Some(1)).unwrap();
        assert!(matches!(
            t.revoke(Some(9)),
            Err(TableError::UnknownLabSession(9))
        ));
    }

    #[test]
    fn rotation_keeps_old_epoch_until_retire() {
        let t = SessionTable::new(None, None, true);
        t.stage(req(1, 1, 11, 12, A, B)).unwrap();
        t.activate(Some(1), 1).unwrap();
        t.stage(req(1, 2, 13, 14, C, D)).unwrap();
        assert!(t.rx_lookup(14).is_some());
        assert_eq!(t.activate(Some(1), 2).unwrap(), 1);
        assert!(t.rx_lookup(12).is_some());
        t.retire(Some(1), 1).unwrap();
        assert!(t.rx_lookup(12).is_none());
        assert!(matches!(t.retire(Some(1), 1), Ok(())));
        assert!(matches!(
            t.retire(Some(1), 2),
            Err(TableError::ActiveEpoch(2))
        ));
    }

    #[test]
    fn stage_rejects_conflicts_and_is_idempotent_on_replay() {
        let t = SessionTable::new(None, None, true);
        let first = req(1, 1, 11, 12, A, B);
        let sender = first.sender_index;
        let peer = first.peer_index;
        t.stage(first).unwrap();
        t.stage(req(1, 1, 11, 12, A, B)).unwrap();
        assert!(matches!(
            t.stage(req(1, 2, 13, 14, C, D)),
            Err(TableError::StagedPending(1))
        ));
        t.activate(Some(1), 1).unwrap();
        assert!(matches!(t.stage(req(1, 2, 13, 14, C, D)), Ok(())));
        assert!(matches!(
            t.stage(req(1, 3, 15, 16, A, C)),
            Err(TableError::StagedPending(2))
        ));
        assert!(matches!(
            t.stage(req(2, 4, 15, sender, A, C)),
            Err(TableError::BadInput(_))
        ));
        assert!(matches!(
            t.stage(req(2, 4, 15, peer, A, C)),
            Err(TableError::IndexInUse(_)) | Err(TableError::StagedPending(_))
        ));
    }

    #[test]
    fn backend_instance_ids_are_stable_per_table() {
        let t = SessionTable::new(None, None, true);
        assert_eq!(t.backend_instance_id().len(), 32);
        assert_eq!(t.backend_instance_id(), t.backend_instance_id());
    }
}
