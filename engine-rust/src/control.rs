//! Control-plane IPC: a dedicated Unix socket for key lifecycle commands.
//!
//! Key material never crosses the metrics socket — it is read-only and
//! group-readable. This listener instead runs mode 0600 (only the file
//! owner connects, root always can) plus a `SO_PEERCRED` check on every
//! accepted connection: the peer's uid must be 0 or the `--control-uid`
//! the engine was launched with. Wire format reuses the telemetry
//! envelope: big-endian `u32` length + UTF-8 JSON
//! `{request_id, version: 1, op, payload}`.
//!
//! Ops (two-phase rotation with drain, single lab peer):
//!
//! - `StagePeer` `{lab_session_id?, key_epoch, sender_index, peer_index,
//!   egress_key, ingress_key}`: allocates a staged epoch; rx starts
//!   accepting the new `peer_index` immediately so early datagrams land.
//! - `Activate` `{key_epoch}`: the staged epoch becomes the sealing epoch
//!   at a packet boundary; the previous active epoch keeps decrypting
//!   until retired.
//! - `Retire` `{key_epoch}`: ends a retiring epoch's drain window (or
//!   unstages a never-activated epoch) and drops its key material.
//! - `RevokePeer` `{lab_session_id?}`: wipes every epoch; traffic blocks
//!   until reprovisioning.
//! - `GetSessions` `{}`: the epoch snapshot, for diagnostics.
//!
//! Keys are 64-hex-char strings (same format as `--key`), decoded into
//! `Zeroizing` buffers and never logged.

use std::io;
use std::net::Ipv4Addr;
use std::os::unix::fs::FileTypeExt;
use std::path::Path;
use std::sync::atomic::Ordering;
use std::sync::Arc;

use tokio::net::{UnixListener, UnixStream};
use zeroize::{Zeroize, Zeroizing};

use crate::cli::parse_key_hex;
use crate::metrics::{self, Metrics};
use crate::sessions::{SessionTable, StagePeerRequest, TableError};

/// Upper bound on simultaneous control connections — far fewer than
/// metrics: only the agent talks here.
const MAX_CONNECTIONS: usize = 8;

/// Binds the control socket with the same stale-file probing as the
/// metrics socket, then tightens it to mode 0600 — it carries key
/// material, unlike the group-readable telemetry socket. When
/// `control_uid` is set the file is chowned to that user so the
/// unprivileged agent can connect while everyone else is refused.
pub fn bind(path: &Path, control_uid: Option<u32>) -> io::Result<UnixListener> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    if path.exists() {
        let is_socket = std::fs::metadata(path)
            .map(|m| m.file_type().is_socket())
            .unwrap_or(false);
        if !is_socket {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                format!(
                    "{}: exists and is not a socket — refusing to touch it",
                    path.display()
                ),
            ));
        }
        if std::os::unix::net::UnixStream::connect(path).is_ok() {
            return Err(io::Error::new(
                io::ErrorKind::AddrInUse,
                format!(
                    "{}: another engine is answering on this socket",
                    path.display()
                ),
            ));
        }
        std::fs::remove_file(path)?;
    }
    let listener = UnixListener::bind(path)?;
    std::fs::set_permissions(path, std::os::unix::fs::PermissionsExt::from_mode(0o600))?;
    if let Some(uid) = control_uid {
        let c_path = std::ffi::CString::new(path.as_os_str().as_encoded_bytes()).map_err(|_| {
            io::Error::new(
                io::ErrorKind::InvalidInput,
                "control socket path contains NUL",
            )
        })?;
        // gid -1 keeps the current group.
        if unsafe { libc::chown(c_path.as_ptr(), uid, u32::MAX) } != 0 {
            return Err(io::Error::last_os_error());
        }
    }
    Ok(listener)
}

/// Removes the socket file; called on clean shutdown.
pub fn cleanup(path: &Path) {
    let _ = std::fs::remove_file(path);
}

/// `SO_PEERCRED` gate: root always passes; otherwise the connecting uid
/// must equal the configured agent uid. When no agent uid is configured
/// the socket is root-only even if the file mode were widened later.
fn authorize(peer_uid: u32, allowed_uid: Option<u32>) -> bool {
    // The engine's own uid owns the 0600 socket file, so a same-uid peer
    // (e.g. an agent running beside the engine) is always authorized;
    // root and the configured agent uid pass too.
    peer_uid == 0 || peer_uid == unsafe { libc::geteuid() } || Some(peer_uid) == allowed_uid
}

/// Serves control ops on the listener until the task is aborted. Like the
/// metrics loop, connections are capped and each frame exchange runs
/// under a deadline so a silent peer cannot exhaust descriptors.
pub async fn serve(
    sessions: Arc<SessionTable>,
    metrics: Arc<Metrics>,
    listener: UnixListener,
    allowed_uid: Option<u32>,
) -> io::Result<()> {
    let permits = Arc::new(tokio::sync::Semaphore::new(MAX_CONNECTIONS));
    loop {
        let (stream, _) = listener.accept().await?;
        let s = sessions.clone();
        let m = metrics.clone();
        let permit = permits.clone().acquire_owned().await.unwrap();
        tokio::spawn(async move {
            let _permit = permit;
            match stream.peer_cred() {
                Ok(cred) if authorize(cred.uid(), allowed_uid) => {
                    if let Err(err) = handle(s, m, stream).await {
                        eprintln!("vpn-engine control: {err}");
                    }
                }
                Ok(cred) => {
                    eprintln!("vpn-engine control: refused uid {}", cred.uid());
                }
                Err(err) => eprintln!("vpn-engine control: peer_cred: {err}"),
            }
        });
    }
}

/// Parses a required u32/u64 payload field.
fn take_u64(payload: &serde_json::Value, field: &str) -> Result<u64, (&'static str, String)> {
    payload.get(field).and_then(|v| v.as_u64()).ok_or((
        "invalid_field",
        format!("payload.{field} must be an integer"),
    ))
}

fn take_u32(payload: &serde_json::Value, field: &str) -> Result<u32, (&'static str, String)> {
    u32::try_from(take_u64(payload, field)?)
        .map_err(|_| ("invalid_field", format!("payload.{field} exceeds u32")))
}

fn take_key(
    payload: &serde_json::Value,
    field: &str,
) -> Result<Zeroizing<[u8; 32]>, (&'static str, String)> {
    let raw = payload
        .get(field)
        .and_then(|v| v.as_str())
        .ok_or(("invalid_field", format!("payload.{field} must be a string")))?;
    parse_key_hex(raw).map_err(|e| ("invalid_field", format!("payload.{field}: {e}")))
}

fn take_opt_u32(
    payload: &serde_json::Value,
    field: &str,
) -> Result<Option<u32>, (&'static str, String)> {
    match payload.get(field) {
        None | Some(serde_json::Value::Null) => Ok(None),
        Some(_) => Ok(Some(take_u32(payload, field)?)),
    }
}

fn take_opt_ipv4(
    payload: &serde_json::Value,
    field: &str,
) -> Result<Option<Ipv4Addr>, (&'static str, String)> {
    match payload.get(field) {
        None | Some(serde_json::Value::Null) => Ok(None),
        Some(serde_json::Value::String(raw)) => raw.parse::<Ipv4Addr>().map(Some).map_err(|_| {
            (
                "invalid_field",
                format!("payload.{field} must be an IPv4 address"),
            )
        }),
        _ => Err(("invalid_field", format!("payload.{field} must be a string"))),
    }
}

fn table_err(e: TableError) -> (&'static str, String) {
    let code = match &e {
        TableError::BadInput(_) => "invalid_field",
        TableError::StagedPending(_) => "staged_pending",
        TableError::UnknownEpoch(_) => "unknown_epoch",
        TableError::ActiveEpoch(_) => "active_epoch",
        TableError::IndexInUse(_) => "index_in_use",
        TableError::UnknownLabSession(_) => "unknown_lab_session",
    };
    (code, e.to_string())
}

/// Applies one control op; returns the `result` object or an error pair.
fn apply(
    sessions: &SessionTable,
    metrics: &Metrics,
    op: &str,
    payload: &serde_json::Value,
) -> Result<serde_json::Value, (&'static str, String)> {
    match op {
        "StagePeer" => {
            let lab_session_id = take_opt_u32(payload, "lab_session_id")?;
            let key_epoch = take_u64(payload, "key_epoch")?;
            let sender_index = take_u32(payload, "sender_index")?;
            let peer_index = take_u32(payload, "peer_index")?;
            let egress_key = take_key(payload, "egress_key")?;
            let ingress_key = take_key(payload, "ingress_key")?;
            let virtual_ip = take_opt_ipv4(payload, "virtual_ip")?;
            sessions
                .stage(StagePeerRequest {
                    lab_session_id,
                    virtual_ip,
                    key_epoch,
                    sender_index,
                    peer_index,
                    tx_key: egress_key,
                    rx_key: ingress_key,
                })
                .map_err(table_err)?;
            Ok(serde_json::json!({
                "key_epoch": key_epoch,
                "role": "staged",
            }))
        }
        "Activate" => {
            let key_epoch = take_u64(payload, "key_epoch")?;
            let lab_session_id = take_opt_u32(payload, "lab_session_id")?;
            let previous = sessions
                .activate(lab_session_id, key_epoch)
                .map_err(table_err)?;
            metrics.rotations_total.fetch_add(1, Ordering::Relaxed);
            Ok(serde_json::json!({
                "key_epoch": key_epoch,
                "role": "active",
                "previous_epoch": previous,
            }))
        }
        "Retire" => {
            let key_epoch = take_u64(payload, "key_epoch")?;
            let lab_session_id = take_opt_u32(payload, "lab_session_id")?;
            sessions
                .retire(lab_session_id, key_epoch)
                .map_err(table_err)?;
            Ok(serde_json::json!({
                "key_epoch": key_epoch,
                "retired": true,
            }))
        }
        "RevokePeer" => {
            let lab_session_id = take_opt_u32(payload, "lab_session_id")?;
            sessions.revoke(lab_session_id).map_err(table_err)?;
            Ok(serde_json::json!({"revoked": true}))
        }
        "GetSessions" => {
            let peers: Vec<serde_json::Value> = sessions
                .peers_snapshot()
                .iter()
                .map(|p| {
                    let epochs: Vec<serde_json::Value> = p
                        .epochs
                        .iter()
                        .map(|e| {
                            serde_json::json!({
                                "key_epoch": e.key_epoch,
                                "sender_index": e.sender_index,
                                "peer_index": e.peer_index,
                                "role": format!("{:?}", e.role).to_lowercase(),
                                "tx_pkts": e.tx_pkts,
                                "rx_pkts": e.rx_pkts,
                            })
                        })
                        .collect();
                    serde_json::json!({
                        "lab_session_id": p.lab_session_id,
                        "virtual_ip": p.virtual_ip.map(|ip| ip.to_string()),
                        "endpoint": p.endpoint.map(|ep| ep.to_string()),
                        "epochs": epochs,
                    })
                })
                .collect();
            let epochs: Vec<serde_json::Value> = peers
                .iter()
                .flat_map(|p| p["epochs"].as_array().cloned().unwrap_or_default())
                .collect();
            let single_lab = if peers.len() == 1 {
                peers[0]["lab_session_id"].clone()
            } else {
                serde_json::Value::Null
            };
            Ok(serde_json::json!({
                "backend_instance_id": sessions.backend_instance_id(),
                "revocations": sessions.revocations(),
                "peers": peers,
                "lab_session_id": single_lab,
                // Compatibility for the single-peer rotation consumers.
                "epochs": epochs,
            }))
        }
        other => Err(("unknown_op", format!("unknown op {other:?}"))),
    }
}

/// Overwrites every string held in a parsed request tree so hex key
/// material does not outlive the command that carried it. Each string's
/// buffer is wiped and truncated to len 0 — valid UTF-8.
fn scrub_strings(value: &mut serde_json::Value) {
    match value {
        serde_json::Value::String(s) => {
            // SAFETY: Vec::zeroize wipes the allocation and truncates to
            // len 0 — an empty buffer is valid UTF-8, so the String stays
            // sound.
            unsafe { s.as_mut_vec() }.zeroize();
        }
        serde_json::Value::Array(items) => items.iter_mut().for_each(scrub_strings),
        serde_json::Value::Object(map) => map.values_mut().for_each(scrub_strings),
        _ => {}
    }
}

async fn handle(
    sessions: Arc<SessionTable>,
    metrics: Arc<Metrics>,
    mut stream: UnixStream,
) -> io::Result<()> {
    // Key hex arrives in this frame: keep it in a Zeroizing buffer so even
    // a parse failure wipes the bytes on the way out.
    let frame = Zeroizing::new(metrics::read_frame(&mut stream).await?);
    let mut request: serde_json::Value = serde_json::from_slice(frame.as_slice())
        .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e.to_string()))?;

    let body = match metrics::validate_request(&request) {
        Ok(()) => match request["op"].as_str() {
            Some(op) => match apply(&sessions, &metrics, op, &request["payload"]) {
                Ok(result) => serde_json::json!({"status": "ok", "result": result}),
                Err((code, message)) => metrics::error_envelope_body(code, &message),
            },
            None => metrics::error_envelope_body("invalid_op", "op must be a string"),
        },
        Err((code, message)) => metrics::error_envelope_body(code, &message),
    };
    let response = metrics::respond(&request, body);
    scrub_strings(&mut request);
    metrics::write_frame(&mut stream, &response).await
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};

    const HEX_A: &str = "1111111111111111111111111111111111111111111111111111111111111111";
    const HEX_B: &str = "2222222222222222222222222222222222222222222222222222222222222222";
    const HEX_C: &str = "3333333333333333333333333333333333333333333333333333333333333333";
    const HEX_D: &str = "4444444444444444444444444444444444444444444444444444444444444444";

    fn sock_path(tag: &str) -> PathBuf {
        std::env::temp_dir().join(format!(
            "vpn-control-test-{}-{tag}.sock",
            std::process::id()
        ))
    }

    fn table() -> Arc<SessionTable> {
        Arc::new(SessionTable::new(
            Some(crate::crypto::SessionState::from_lab_psk(
                Zeroizing::new([9u8; 32]),
                1,
                2,
            )),
            Some(Ipv4Addr::new(10, 80, 1, 2)),
            false,
        ))
    }

    async fn spawn_server(
        tag: &str,
        uid: Option<u32>,
    ) -> (PathBuf, Arc<SessionTable>, Arc<Metrics>) {
        let path = sock_path(tag);
        let _ = std::fs::remove_file(&path);
        let listener = bind(&path, uid).unwrap();
        let sessions = table();
        let metrics = Metrics::new();
        tokio::spawn(serve(sessions.clone(), metrics.clone(), listener, uid));
        (path, sessions, metrics)
    }

    async fn request(path: &Path, op: &str, payload: serde_json::Value) -> serde_json::Value {
        let mut c = UnixStream::connect(path).await.unwrap();
        let req = serde_json::json!({
            "request_id": "r1", "version": 1, "op": op, "payload": payload,
        })
        .to_string();
        c.write_u32(req.len() as u32).await.unwrap();
        c.write_all(req.as_bytes()).await.unwrap();
        let n = c.read_u32().await.unwrap() as usize;
        let mut buf = vec![0u8; n];
        c.read_exact(&mut buf).await.unwrap();
        serde_json::from_slice(&buf).unwrap()
    }

    fn stage_payload(
        epoch: u64,
        sender: u32,
        peer: u32,
        egress_key: &str,
        ingress_key: &str,
    ) -> serde_json::Value {
        serde_json::json!({
            "lab_session_id": 42,
            "key_epoch": epoch,
            "sender_index": sender,
            "peer_index": peer,
            "egress_key": egress_key,
            "ingress_key": ingress_key,
        })
    }

    #[test]
    fn scrub_strings_wipes_hex_material_and_keeps_shape() {
        let mut v = stage_payload(1, 11, 22, HEX_A, HEX_B);
        scrub_strings(&mut v);
        for field in ["egress_key", "ingress_key"] {
            // Vec::zeroize wipes the allocation and truncates to len 0.
            assert_eq!(v[field].as_str().unwrap(), "");
        }
        assert_eq!(v["key_epoch"], 1);
        assert_eq!(v["sender_index"], 11);
        assert_eq!(v["lab_session_id"], 42);
    }

    #[test]
    fn authorize_allows_root_owner_and_configured_uid_only() {
        let euid = unsafe { libc::geteuid() };
        assert!(authorize(0, None));
        assert!(authorize(euid, None));
        assert!(!authorize(euid + 1, None));
        assert!(authorize(1000, Some(1000)));
        assert!(!authorize(1001, Some(1000)));
        assert!(authorize(0, Some(1000)));
    }

    #[tokio::test]
    async fn bind_refuses_regular_file_and_replaces_stale_socket() {
        let regular = sock_path("regular");
        let _ = std::fs::remove_file(&regular);
        std::fs::write(&regular, b"not a socket").unwrap();
        let err = bind(&regular, None).unwrap_err();
        assert_eq!(err.kind(), io::ErrorKind::InvalidInput);
        let _ = std::fs::remove_file(&regular);

        let stale = sock_path("stale");
        let _ = std::fs::remove_file(&stale);
        {
            let _listener = std::os::unix::net::UnixListener::bind(&stale).unwrap();
        } // dropping the listener leaves the filesystem entry behind
        let listener = bind(&stale, None).unwrap();
        drop(listener);
        cleanup(&stale);
    }

    #[test]
    fn table_error_mapping_is_stable_for_agent_and_tests() {
        assert_eq!(
            table_err(TableError::BadInput("bad".into())).0,
            "invalid_field"
        );
        assert_eq!(table_err(TableError::StagedPending(1)).0, "staged_pending");
        assert_eq!(table_err(TableError::UnknownEpoch(1)).0, "unknown_epoch");
        assert_eq!(table_err(TableError::ActiveEpoch(1)).0, "active_epoch");
        assert_eq!(table_err(TableError::IndexInUse(1)).0, "index_in_use");
        assert_eq!(
            table_err(TableError::UnknownLabSession(1)).0,
            "unknown_lab_session"
        );
    }

    #[test]
    fn scrub_strings_wipes_nested_arrays_and_objects() {
        let mut v = serde_json::json!({
            "outer": [{"key": "secret"}, ["nested-secret"]],
            "plain": 7,
        });
        scrub_strings(&mut v);
        assert_eq!(v["outer"][0]["key"], "");
        assert_eq!(v["outer"][1][0], "");
        assert_eq!(v["plain"], 7);
    }

    #[tokio::test]
    async fn full_rotation_over_the_socket() {
        let (path, sessions, metrics) = spawn_server("rot", Some(unsafe { libc::geteuid() })).await;

        // Stage a new epoch: rx already accepts peer_index 22.
        let resp = request(&path, "StagePeer", stage_payload(1, 11, 22, HEX_A, HEX_B)).await;
        assert_eq!(resp["status"], "ok");
        assert_eq!(resp["result"]["role"], "staged");
        assert_eq!(resp["request_id"], "r1");
        assert!(sessions.rx_lookup(22).is_some());
        assert!(sessions.single_tx().map(|target| target.session).is_some()); // epoch 0 still seals

        // Replaying the identical stage is a no-op, not an error.
        let resp = request(&path, "StagePeer", stage_payload(1, 11, 22, HEX_A, HEX_B)).await;
        assert_eq!(resp["status"], "ok");

        // Activate flips tx; the old epoch keeps decrypting.
        let resp = request(&path, "Activate", serde_json::json!({"key_epoch": 1})).await;
        assert_eq!(resp["result"]["role"], "active");
        assert_eq!(resp["result"]["previous_epoch"], 0);
        assert_eq!(metrics.rotations_total.load(Ordering::Relaxed), 1);
        assert_eq!(
            sessions
                .single_tx()
                .map(|target| target.session)
                .unwrap()
                .key_epoch(),
            1
        );
        assert!(sessions.rx_lookup(2).is_some());

        // If the activation ACK was lost, at-least-once delivery replays
        // the whole provision operation. The exact active epoch accepts
        // that replay instead of treating it as key reuse.
        let resp = request(&path, "StagePeer", stage_payload(1, 11, 22, HEX_A, HEX_B)).await;
        assert_eq!(resp["status"], "ok");
        let resp = request(&path, "Activate", serde_json::json!({"key_epoch": 1})).await;
        assert_eq!(resp["status"], "ok");

        // Retire ends the drain window for epoch 0.
        let resp = request(&path, "Retire", serde_json::json!({"key_epoch": 0})).await;
        assert_eq!(resp["status"], "ok");
        assert!(sessions.rx_lookup(2).is_none());

        cleanup(&path);
    }

    #[tokio::test]
    async fn errors_are_enveloped_not_disconnected() {
        let (path, sessions, _m) = spawn_server("err", None).await;

        let resp = request(&path, "Activate", serde_json::json!({"key_epoch": 5})).await;
        assert_eq!(resp["error"]["code"], "unknown_epoch");

        let resp = request(&path, "Retire", serde_json::json!({"key_epoch": 0})).await;
        assert_eq!(resp["error"]["code"], "active_epoch");

        let resp = request(&path, "StagePeer", stage_payload(1, 7, 7, HEX_A, HEX_B)).await;
        assert_eq!(resp["error"]["code"], "invalid_field");

        // Identical direction keys are refused.
        let resp = request(&path, "StagePeer", stage_payload(1, 11, 22, HEX_A, HEX_A)).await;
        assert_eq!(resp["error"]["code"], "invalid_field");

        let mut bad = stage_payload(1, 11, 22, HEX_A, HEX_B);
        bad["ingress_key"] = serde_json::json!("zz");
        let resp = request(&path, "StagePeer", bad).await;
        assert_eq!(resp["error"]["code"], "invalid_field");

        // A colliding peer_index is refused.
        let resp = request(&path, "StagePeer", stage_payload(1, 11, 2, HEX_A, HEX_B)).await;
        assert_eq!(resp["error"]["code"], "index_in_use");
        assert!(sessions.single_tx().map(|target| target.session).is_some());

        let resp = request(&path, "Bogus", serde_json::json!({})).await;
        assert_eq!(resp["error"]["code"], "unknown_op");

        cleanup(&path);
    }

    #[tokio::test]
    async fn revoke_wipes_epochs_and_getsessions_reports_them() {
        let (path, sessions, _m) = spawn_server("rev", None).await;
        request(&path, "StagePeer", stage_payload(1, 11, 22, HEX_B, HEX_C)).await;
        request(&path, "Activate", serde_json::json!({"key_epoch": 1})).await;

        let resp = request(&path, "GetSessions", serde_json::json!({})).await;
        let epochs = resp["result"]["epochs"].as_array().unwrap();
        assert_eq!(epochs.len(), 2);
        assert_eq!(epochs[0]["role"], "active");
        assert_eq!(epochs[1]["role"], "retiring");
        assert_eq!(resp["result"]["lab_session_id"], 42);

        // Wrong lab_session_id refuses; right one wipes the table.
        let resp = request(
            &path,
            "RevokePeer",
            serde_json::json!({"lab_session_id": 7}),
        )
        .await;
        assert_eq!(resp["error"]["code"], "unknown_lab_session");
        let resp = request(
            &path,
            "RevokePeer",
            serde_json::json!({"lab_session_id": 42}),
        )
        .await;
        assert_eq!(resp["status"], "ok");
        assert!(sessions.single_tx().map(|target| target.session).is_none());
        assert!(sessions.keyed());

        cleanup(&path);
    }

    #[tokio::test]
    async fn same_user_connection_is_authorized() {
        // Tests connect as our own uid: authorize() must let us through
        // when the engine allows this uid (uid 0 path is covered by the
        // table above, we are not root in CI).
        let euid = unsafe { libc::geteuid() };
        let (path, ..) = spawn_server("auth", Some(euid)).await;
        let resp = request(&path, "GetSessions", serde_json::json!({})).await;
        assert_eq!(resp["status"], "ok");
        cleanup(&path);
    }

    #[tokio::test]
    async fn socket_file_is_mode_0600() {
        let path = sock_path("mode");
        let _ = std::fs::remove_file(&path);
        let _listener = bind(&path, None).unwrap();
        use std::os::unix::fs::PermissionsExt;
        let mode = std::fs::metadata(&path).unwrap().permissions().mode() & 0o777;
        assert_eq!(mode, 0o600);
        cleanup(&path);
    }

    #[tokio::test]
    async fn invalid_optional_ip_and_envelope_errors_are_reported() {
        let (path, ..) = spawn_server("invalid-ip", None).await;
        let mut bad = stage_payload(1, 11, 22, HEX_A, HEX_B);
        bad["virtual_ip"] = serde_json::json!("not-an-ip");
        let resp = request(&path, "StagePeer", bad).await;
        assert_eq!(resp["error"]["code"], "invalid_field");

        let mut bad = stage_payload(1, 11, 22, HEX_A, HEX_B);
        bad["virtual_ip"] = serde_json::json!(7);
        let resp = request(&path, "StagePeer", bad).await;
        assert_eq!(resp["error"]["code"], "invalid_field");

        let mut c = UnixStream::connect(&path).await.unwrap();
        let req = serde_json::json!({
            "request_id": "bad-env", "version": 1, "op": 7, "payload": {},
        })
        .to_string();
        c.write_u32(req.len() as u32).await.unwrap();
        c.write_all(req.as_bytes()).await.unwrap();
        let n = c.read_u32().await.unwrap() as usize;
        let mut buf = vec![0u8; n];
        c.read_exact(&mut buf).await.unwrap();
        let resp: serde_json::Value = serde_json::from_slice(&buf).unwrap();
        assert_eq!(resp["error"]["code"], "invalid_op");
        cleanup(&path);
    }

    #[tokio::test]
    async fn getsessions_with_multiple_peers_has_no_single_lab_alias() {
        let (path, ..) = spawn_server("multi-get", None).await;
        let first = stage_payload(1, 11, 22, HEX_A, HEX_B);
        assert_eq!(request(&path, "StagePeer", first).await["status"], "ok");
        let mut second = stage_payload(2, 33, 44, HEX_C, HEX_D);
        second["lab_session_id"] = serde_json::json!(43);
        second["virtual_ip"] = serde_json::json!("10.80.1.3");
        assert_eq!(request(&path, "StagePeer", second).await["status"], "ok");

        let resp = request(&path, "GetSessions", serde_json::json!({})).await;
        assert_eq!(resp["status"], "ok");
        assert!(resp["result"]["lab_session_id"].is_null());
        assert_eq!(resp["result"]["peers"].as_array().unwrap().len(), 2);
        cleanup(&path);
    }
}
