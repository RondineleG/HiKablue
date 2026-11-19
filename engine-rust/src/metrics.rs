//! Milestone 1 telemetry: shared counters exposed as `GetMetrics` over a Unix
//! socket.
//!
//! Wire format (shared with the Go agent and, later, the control plane): a
//! big-endian `u32` length prefix followed by one UTF-8 JSON envelope:
//!
//! ```json
//! {"request_id":"req-1","version":1,"op":"GetMetrics","payload":{}}
//! ```
//!
//! The response echoes `request_id` and carries `status`: `"ok"` plus a
//! `result` object, or `"error"` plus an `error` object with `code` and
//! `message`. A single `read` is not guaranteed to deliver a whole frame —
//! stream sockets may return any prefix of the bytes in flight — so every
//! frame is assembled with `read_exact`, which loops until the buffer is
//! full or the peer disconnects.

use std::io;
use std::os::unix::fs::FileTypeExt;
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{UnixListener, UnixStream};

/// Largest accepted request frame, per the socket contract (1 MiB). The
/// GetMetrics request is a handful of bytes, so anything bigger is rejected
/// before allocation.
const MAX_FRAME: u32 = 1 << 20;

/// Time a connection may take to deliver or receive a frame. An accepted
/// connection holds a task and a file descriptor; without a deadline a
/// silent peer could exhaust descriptors and take the listener down.
const IO_DEADLINE: Duration = Duration::from_secs(10);

/// Upper bound on simultaneous metrics connections.
const MAX_CONNECTIONS: usize = 64;

/// Counters shared between the forwarding loop and the metrics listener.
/// `rx_bytes` counts every datagram received, including the ones validation
/// later drops — that is what lets a 5-byte datagram move the counter by
/// five. `rx_pkts` counts only datagrams that reached the TUN.
#[derive(Debug)]
pub struct Metrics {
    pub rx_bytes: AtomicU64,
    pub rx_pkts: AtomicU64,
    pub tx_bytes: AtomicU64,
    pub tx_pkts: AtomicU64,
    pub dropped_pkts: AtomicU64,
    /// 0 or 1 for the current single fixed peer.
    pub peers: AtomicU64,
    started: Instant,
}

impl Metrics {
    pub fn new() -> Arc<Self> {
        Arc::new(Self {
            rx_bytes: AtomicU64::new(0),
            rx_pkts: AtomicU64::new(0),
            tx_bytes: AtomicU64::new(0),
            tx_pkts: AtomicU64::new(0),
            dropped_pkts: AtomicU64::new(0),
            peers: AtomicU64::new(0),
            started: Instant::now(),
        })
    }
}

fn metrics_json(m: &Metrics) -> serde_json::Value {
    serde_json::json!({
        "rx_bytes": m.rx_bytes.load(Ordering::Relaxed),
        "rx_pkts": m.rx_pkts.load(Ordering::Relaxed),
        "tx_bytes": m.tx_bytes.load(Ordering::Relaxed),
        "tx_pkts": m.tx_pkts.load(Ordering::Relaxed),
        "dropped_pkts": m.dropped_pkts.load(Ordering::Relaxed),
        "peers": m.peers.load(Ordering::Relaxed),
        "uptime_secs": m.started.elapsed().as_secs(),
    })
}

fn error_envelope_body(code: &str, message: &str) -> serde_json::Value {
    serde_json::json!({
        "status": "error",
        "error": {"code": code, "message": message},
    })
}

/// Envelope rules from the socket contract: `request_id`, `version`,
/// `op` and `payload` are all required; only protocol version 1 exists.
fn validate_request(req: &serde_json::Value) -> Result<(), (&'static str, String)> {
    let missing = |f: &str| Err(("missing_field", format!("missing {f}")));
    if req.get("request_id").is_none() {
        return missing("request_id");
    }
    match req.get("version") {
        None => return missing("version"),
        Some(serde_json::Value::Number(n)) if n.as_u64() == Some(1) => {}
        Some(_) => {
            return Err((
                "unsupported_version",
                "only protocol version 1 is supported".to_string(),
            ));
        }
    }
    if req.get("op").is_none() {
        return missing("op");
    }
    if req.get("payload").is_none() {
        return missing("payload");
    }
    Ok(())
}

/// Binds the metrics socket and returns its listener so bind failures are
/// fatal for the engine instead of leaving a dead task nobody observes.
///
/// If the path already exists it may be a stale file from a crashed run or
/// a live engine's socket. We probe it: a live listener means we refuse to
/// take over, anything else is removed as stale.
pub fn bind(path: &Path, group: Option<&str>) -> io::Result<UnixListener> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    if path.exists() {
        // Only a socket file may be ours to take over — anything else
        // (regular file, directory, FIFO) belongs to someone else and is
        // never removed, whatever failed before.
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
        // A socket nobody answers is a stale remnant of a crashed run.
        std::fs::remove_file(path)?;
    }
    let listener = UnixListener::bind(path)?;
    // The engine usually runs as root (TUN needs CAP_NET_ADMIN) while the
    // agent connects as an unprivileged user in a dedicated group: the lab
    // procedure asks for mode 0660 plus a group that may traverse the
    // directory. Users outside the group cannot connect.
    std::fs::set_permissions(path, std::os::unix::fs::PermissionsExt::from_mode(0o660))?;
    if let Some(group) = group {
        let status = std::process::Command::new("chgrp")
            .arg(group)
            .arg(path)
            .status()?;
        if !status.success() {
            return Err(io::Error::other(format!("chgrp {group} failed: {status}")));
        }
    }
    Ok(listener)
}

/// Serves `GetMetrics` on the listener until the task is aborted.
pub async fn serve(metrics: Arc<Metrics>, listener: UnixListener) -> io::Result<()> {
    let permits = Arc::new(tokio::sync::Semaphore::new(MAX_CONNECTIONS));
    loop {
        let (stream, _) = listener.accept().await?;
        let m = metrics.clone();
        let permit = permits.clone().acquire_owned().await.unwrap();
        tokio::spawn(async move {
            let _permit = permit;
            if let Err(err) = handle(m, stream).await {
                eprintln!("vpn-engine metrics: {err}");
            }
        });
    }
}

/// Removes the socket file; called on clean shutdown.
pub fn cleanup(path: &Path) {
    let _ = std::fs::remove_file(path);
}

async fn handle(metrics: Arc<Metrics>, mut stream: UnixStream) -> io::Result<()> {
    let read = async {
        let len = stream.read_u32().await?;
        if len > MAX_FRAME {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                format!("metrics frame of {len} bytes exceeds {MAX_FRAME}"),
            ));
        }
        // The length prefix says how many bytes the frame holds; the socket
        // is free to deliver them one at a time. read_exact keeps reading
        // until the whole frame is assembled or the peer disconnects.
        let mut frame = vec![0u8; len as usize];
        stream.read_exact(&mut frame).await?;
        Ok::<_, io::Error>(frame)
    };
    let frame = tokio::time::timeout(IO_DEADLINE, read)
        .await
        .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "metrics read deadline"))??;

    let request: serde_json::Value = serde_json::from_slice(&frame)
        .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e.to_string()))?;

    // Envelope contract: every field is required, version must be 1, and
    // the response echoes request_id verbatim (any JSON type). Errors are
    // answered with a valid error envelope, not a dropped connection.
    let body = match validate_request(&request) {
        Ok(()) => match request["op"].as_str() {
            Some("GetMetrics") => {
                serde_json::json!({"status": "ok", "result": metrics_json(&metrics)})
            }
            Some(other) => error_envelope_body("unknown_op", &format!("unknown op {other:?}")),
            None => error_envelope_body("invalid_op", "op must be a string"),
        },
        Err((code, message)) => error_envelope_body(code, &message),
    };
    let mut response = body;
    response["request_id"] = request
        .get("request_id")
        .cloned()
        .unwrap_or(serde_json::Value::Null);
    response["version"] = serde_json::json!(1);
    let response = response.to_string();
    let write = async {
        stream.write_u32(response.len() as u32).await?;
        stream.write_all(response.as_bytes()).await
    };
    tokio::time::timeout(IO_DEADLINE, write)
        .await
        .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "metrics write deadline"))?
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::net::UnixStream;

    fn sock_path(tag: &str) -> PathBuf {
        std::env::temp_dir().join(format!(
            "vpn-metrics-test-{}-{tag}.sock",
            std::process::id()
        ))
    }

    async fn spawn_server(tag: &str) -> PathBuf {
        let path = sock_path(tag);
        let _ = std::fs::remove_file(&path);
        let listener = bind(&path, None).unwrap();
        let m = Metrics::new();
        tokio::spawn(serve(m, listener));
        path
    }

    async fn request(path: &Path, body: &serde_json::Value) -> serde_json::Value {
        let mut c = UnixStream::connect(path).await.unwrap();
        let req = body.to_string();
        c.write_u32(req.len() as u32).await.unwrap();
        c.write_all(req.as_bytes()).await.unwrap();
        let n = c.read_u32().await.unwrap() as usize;
        let mut buf = vec![0u8; n];
        c.read_exact(&mut buf).await.unwrap();
        serde_json::from_slice(&buf).unwrap()
    }

    #[tokio::test]
    async fn get_metrics_returns_counters() {
        let path = spawn_server("basic").await;
        let resp = request(
            &path,
            &serde_json::json!({"request_id": "req-1", "version": 1, "op": "GetMetrics", "payload": {}}),
        )
        .await;
        assert_eq!(resp["request_id"], "req-1");
        assert_eq!(resp["status"], "ok");
        assert_eq!(resp["result"]["rx_bytes"], 0);
        assert_eq!(resp["result"]["dropped_pkts"], 0);
        assert!(resp["result"]["uptime_secs"].is_u64());
        cleanup(&path);
    }

    #[tokio::test]
    async fn frame_split_across_writes_still_assembles() {
        // A single read(2) may return only a prefix of the bytes in flight —
        // POSIX guarantees at most the bytes available now, not a whole
        // record. Writing the request in two chunks with a pause forces that
        // partial-read case: if handle() had called read() once instead of
        // read_exact(), the response would be a parse error, not metrics.
        let path = spawn_server("split").await;
        let mut c = UnixStream::connect(&path).await.unwrap();
        let req = serde_json::json!({"request_id": "req-2", "version": 1, "op": "GetMetrics", "payload": {}}).to_string();
        let half = req.len() / 2;
        c.write_u32(req.len() as u32).await.unwrap();
        c.write_all(&req.as_bytes()[..half]).await.unwrap();
        tokio::time::sleep(Duration::from_millis(100)).await;
        c.write_all(&req.as_bytes()[half..]).await.unwrap();
        let n = c.read_u32().await.unwrap() as usize;
        let mut buf = vec![0u8; n];
        c.read_exact(&mut buf).await.unwrap();
        let resp: serde_json::Value = serde_json::from_slice(&buf).unwrap();
        assert_eq!(resp["status"], "ok");
        assert!(resp["result"]["rx_bytes"].is_u64());
        cleanup(&path);
    }

    #[tokio::test]
    async fn unknown_op_returns_error_envelope() {
        let path = spawn_server("unknown").await;
        let resp = request(
            &path,
            &serde_json::json!({"request_id": "r", "version": 1, "op": "NoSuch", "payload": {}}),
        )
        .await;
        assert_eq!(resp["status"], "error");
        assert_eq!(resp["error"]["code"], "unknown_op");
        cleanup(&path);
    }

    #[tokio::test]
    async fn counters_reflect_increment() {
        let path = sock_path("count");
        let _ = std::fs::remove_file(&path);
        let listener = bind(&path, None).unwrap();
        let m = Metrics::new();
        m.rx_bytes.store(5, Ordering::Relaxed);
        m.dropped_pkts.store(1, Ordering::Relaxed);
        tokio::spawn(serve(m, listener));
        let resp = request(
            &path,
            &serde_json::json!({"request_id": "r", "version": 1, "op": "GetMetrics", "payload": {}}),
        )
        .await;
        assert_eq!(resp["result"]["rx_bytes"], 5);
        assert_eq!(resp["result"]["dropped_pkts"], 1);
        cleanup(&path);
    }

    #[tokio::test]
    async fn bind_refuses_a_live_socket() {
        let path = sock_path("live");
        let _ = std::fs::remove_file(&path);
        let listener = bind(&path, None).unwrap();
        tokio::spawn(serve(Metrics::new(), listener));
        tokio::time::sleep(Duration::from_millis(50)).await;
        let err = bind(&path, None).unwrap_err();
        assert_eq!(err.kind(), io::ErrorKind::AddrInUse);
        cleanup(&path);
    }

    #[tokio::test]
    async fn unsupported_version_is_rejected() {
        let path = spawn_server("ver").await;
        let resp = request(
            &path,
            &serde_json::json!({"request_id": "r", "version": 2, "op": "GetMetrics", "payload": {}}),
        )
        .await;
        assert_eq!(resp["status"], "error");
        assert_eq!(resp["error"]["code"], "unsupported_version");
        cleanup(&path);
    }

    #[tokio::test]
    async fn missing_request_id_is_rejected() {
        let path = spawn_server("noid").await;
        let resp = request(
            &path,
            &serde_json::json!({"version": 1, "op": "GetMetrics", "payload": {}}),
        )
        .await;
        assert_eq!(resp["status"], "error");
        assert_eq!(resp["error"]["code"], "missing_field");
        assert_eq!(resp["request_id"], serde_json::Value::Null);
        cleanup(&path);
    }

    #[tokio::test]
    async fn missing_payload_is_rejected() {
        let path = spawn_server("nopayload").await;
        let resp = request(
            &path,
            &serde_json::json!({"request_id": "r", "version": 1, "op": "GetMetrics"}),
        )
        .await;
        assert_eq!(resp["error"]["code"], "missing_field");
        cleanup(&path);
    }

    #[tokio::test]
    async fn special_chars_in_request_id_echo_safely() {
        // An op name or request_id containing quotes/backslashes must come
        // back escaped — serde_json builds the envelope, never format!.
        let path = spawn_server("chars").await;
        let id = "we\"ird\\id\n";
        let resp = request(
            &path,
            &serde_json::json!({"request_id": id, "version": 1, "op": "GetMetrics", "payload": {}}),
        )
        .await;
        assert_eq!(resp["request_id"], id);
        assert_eq!(resp["status"], "ok");
        cleanup(&path);
    }

    #[tokio::test]
    async fn hostile_op_name_is_escaped_in_error() {
        let path = spawn_server("evil").await;
        let op = "x\"}{\"status\":\"ok\",\"result\":{\"rx_bytes\":999";
        let resp = request(
            &path,
            &serde_json::json!({"request_id": "r", "version": 1, "op": op, "payload": {}}),
        )
        .await;
        assert_eq!(resp["status"], "error");
        assert_eq!(resp["error"]["code"], "unknown_op");
        cleanup(&path);
    }

    #[tokio::test]
    async fn bind_refuses_a_regular_file() {
        // A path that exists but is not a socket belongs to someone else;
        // bind must refuse and leave it untouched.
        let path = sock_path("notasock");
        std::fs::write(&path, b"keep me").unwrap();
        let err = bind(&path, None).unwrap_err();
        assert_eq!(err.kind(), io::ErrorKind::InvalidInput);
        assert_eq!(std::fs::read(&path).unwrap(), b"keep me");
        std::fs::remove_file(&path).unwrap();
    }

    // ---- coverage tests: bind/validate_request/handle edges ----
    use std::os::unix::fs::PermissionsExt;

    #[tokio::test]
    async fn missing_version_is_rejected() {
        let path = spawn_server("cov-mv").await;
        let res = request(
            &path,
            &serde_json::json!({
                "request_id": "x", "op": "get_metrics", "payload": {}
            }),
        )
        .await;
        assert_eq!(res["status"], "error");
        assert_eq!(res["error"]["code"], "missing_field");
        cleanup(&path);
    }

    #[tokio::test]
    async fn non_string_op_is_rejected() {
        let path = spawn_server("cov-op").await;
        let res = request(
            &path,
            &serde_json::json!({
                "request_id": "x", "version": 1, "op": 42, "payload": {}
            }),
        )
        .await;
        assert_eq!(res["error"]["code"], "invalid_op");
        cleanup(&path);
    }

    #[tokio::test]
    async fn missing_op_is_rejected() {
        let path = spawn_server("cov-mo").await;
        let res = request(
            &path,
            &serde_json::json!({
                "request_id": "x", "version": 1, "payload": {}
            }),
        )
        .await;
        assert_eq!(res["error"]["code"], "missing_field");
        cleanup(&path);
    }

    #[tokio::test]
    async fn oversized_frame_closes_the_connection() {
        let path = spawn_server("cov-big").await;
        let mut s = UnixStream::connect(&path).await.unwrap();
        s.write_u32(MAX_FRAME + 1).await.unwrap();
        let mut buf = [0u8; 4];
        // Server drops the connection instead of reading a giant frame.
        assert_eq!(s.read(&mut buf).await.unwrap(), 0);
        cleanup(&path);
    }

    #[tokio::test]
    async fn malformed_json_closes_the_connection() {
        let path = spawn_server("cov-bad").await;
        let mut s = UnixStream::connect(&path).await.unwrap();
        s.write_u32(3).await.unwrap();
        s.write_all(b"!!!").await.unwrap();
        let mut buf = [0u8; 4];
        assert_eq!(s.read(&mut buf).await.unwrap(), 0);
        cleanup(&path);
    }

    #[tokio::test]
    async fn bind_recovers_a_stale_socket_file() {
        let path = sock_path("cov-stale");
        {
            let _short = UnixListener::bind(&path).unwrap();
        }
        // File remains but nobody answers: bind must take it over.
        assert!(bind(&path, None).is_ok());
        cleanup(&path);
    }

    #[tokio::test]
    async fn bind_creates_missing_parents_and_sets_0660() {
        let path = sock_path("cov-deep");
        let path = path
            .with_file_name(format!("cov-deep-{}", std::process::id()))
            .join("a/b/metrics.sock");
        assert!(bind(&path, None).is_ok());
        let mode = std::fs::metadata(&path).unwrap().permissions().mode() & 0o777;
        assert_eq!(mode, 0o660);
        cleanup(&path);
    }

    #[tokio::test]
    async fn bind_with_unknown_group_fails() {
        let path = sock_path("cov-grp-bad");
        assert!(bind(&path, Some("hkcov-no-such-group")).is_err());
        cleanup(&path);
    }

    #[tokio::test]
    async fn bind_with_own_group_succeeds() {
        let group = String::from_utf8(
            std::process::Command::new("id")
                .arg("-gn")
                .output()
                .unwrap()
                .stdout,
        )
        .unwrap();
        let path = sock_path("cov-grp-own");
        assert!(bind(&path, Some(group.trim())).is_ok());
        cleanup(&path);
    }
}
