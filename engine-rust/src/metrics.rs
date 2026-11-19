//! Milestone 1 telemetry: shared counters exposed as `GetMetrics` over a Unix
//! socket.
//!
//! Frame format (shared with the Go agent and, later, the control plane): a
//! big-endian `u32` length prefix followed by a UTF-8 JSON object. A single
//! `read` is not guaranteed to deliver a whole frame — TCP-style stream
//! sockets may return any prefix of the bytes in flight — so every frame is
//! assembled with `read_exact`, which loops until the buffer is full or the
//! peer disconnects.

use std::io;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use std::time::Instant;

use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::UnixStream;

/// Largest accepted request frame; the GetMetrics request is a handful of
/// bytes, so anything bigger is rejected before allocation.
const MAX_FRAME: u32 = 4096;

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

    fn to_json(&self) -> String {
        format!(
            "{{\"rx_bytes\":{},\"rx_pkts\":{},\"tx_bytes\":{},\"tx_pkts\":{},\"dropped_pkts\":{},\"peers\":{},\"uptime_secs\":{}}}",
            self.rx_bytes.load(Ordering::Relaxed),
            self.rx_pkts.load(Ordering::Relaxed),
            self.tx_bytes.load(Ordering::Relaxed),
            self.tx_pkts.load(Ordering::Relaxed),
            self.dropped_pkts.load(Ordering::Relaxed),
            self.peers.load(Ordering::Relaxed),
            self.started.elapsed().as_secs(),
        )
    }
}

/// Binds the metrics socket and serves `GetMetrics` until the task is
/// aborted. A stale socket file from a previous run is removed first so a
/// restart does not fail with EADDRINUSE.
pub async fn serve(metrics: Arc<Metrics>, path: PathBuf) -> io::Result<()> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let _ = std::fs::remove_file(&path);
    let listener = tokio::net::UnixListener::bind(&path)?;
    // The engine usually runs as root (TUN needs CAP_NET_ADMIN) while the
    // agent does not: without this the socket file would be srwxr-xr-x and
    // the unprivileged poller could not connect. Lab telemetry only — the
    // control-plane milestone adds real auth.
    std::fs::set_permissions(&path, std::os::unix::fs::PermissionsExt::from_mode(0o666))?;
    loop {
        let (stream, _) = listener.accept().await?;
        let m = metrics.clone();
        tokio::spawn(async move {
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
    let len = stream.read_u32().await?;
    if len > MAX_FRAME {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            format!("metrics frame of {len} bytes exceeds {MAX_FRAME}"),
        ));
    }
    // The length prefix says how many bytes the frame holds; the socket is
    // free to deliver them one at a time. read_exact keeps reading until the
    // whole frame is assembled or the peer disconnects.
    let mut frame = vec![0u8; len as usize];
    stream.read_exact(&mut frame).await?;

    let request: serde_json::Value = serde_json::from_slice(&frame)
        .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e.to_string()))?;
    let body = match request.get("method").and_then(|m| m.as_str()) {
        Some("GetMetrics") => metrics.to_json(),
        Some(other) => format!("{{\"error\":\"unknown method {other}\"}}"),
        None => "{\"error\":\"missing method\"}".to_string(),
    };

    stream.write_u32(body.len() as u32).await?;
    stream.write_all(body.as_bytes()).await
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::net::UnixStream;

    fn sock_path(tag: &str) -> PathBuf {
        std::env::temp_dir().join(format!(
            "vpn-metrics-test-{}-{tag}.sock",
            std::process::id()
        ))
    }

    async fn spawn_server(tag: &str) -> (PathBuf, tokio::task::JoinHandle<io::Result<()>>) {
        let path = sock_path(tag);
        let m = Metrics::new();
        let handle = tokio::spawn(serve(m.clone(), path.clone()));
        // Let bind happen before clients connect.
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;
        (path, handle)
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
        let (path, handle) = spawn_server("basic").await;
        let m = Metrics::new();
        let _ = m; // counters live inside serve's Arc; read them via the frame
        let resp = request(&path, &serde_json::json!({"method": "GetMetrics"})).await;
        assert_eq!(resp["rx_bytes"], 0);
        assert_eq!(resp["dropped_pkts"], 0);
        assert!(resp["uptime_secs"].is_u64());
        handle.abort();
        cleanup(&path);
    }

    #[tokio::test]
    async fn frame_split_across_writes_still_assembles() {
        // A single read(2) may return only a prefix of the bytes in flight —
        // POSIX guarantees at most the bytes available now, not a whole
        // record. Writing the frame in two chunks with a pause forces that
        // partial-read case: if handle() had called read() once instead of
        // read_exact(), the response would be a parse error, not metrics.
        let (path, handle) = spawn_server("split").await;
        let mut c = UnixStream::connect(&path).await.unwrap();
        let req = serde_json::json!({"method": "GetMetrics"}).to_string();
        let half = req.len() / 2;
        c.write_u32(req.len() as u32).await.unwrap();
        c.write_all(&req.as_bytes()[..half]).await.unwrap();
        tokio::time::sleep(std::time::Duration::from_millis(100)).await;
        c.write_all(&req.as_bytes()[half..]).await.unwrap();
        let n = c.read_u32().await.unwrap() as usize;
        let mut buf = vec![0u8; n];
        c.read_exact(&mut buf).await.unwrap();
        let resp: serde_json::Value = serde_json::from_slice(&buf).unwrap();
        assert!(resp.get("rx_bytes").is_some());
        handle.abort();
        cleanup(&path);
    }

    #[tokio::test]
    async fn unknown_method_returns_error_frame() {
        let (path, handle) = spawn_server("unknown").await;
        let resp = request(&path, &serde_json::json!({"method": "NoSuch"})).await;
        assert!(resp["error"].as_str().unwrap().contains("unknown method"));
        handle.abort();
        cleanup(&path);
    }

    #[tokio::test]
    async fn counters_reflect_increment() {
        let path = sock_path("count");
        let m = Metrics::new();
        m.rx_bytes.store(5, Ordering::Relaxed);
        m.dropped_pkts.store(1, Ordering::Relaxed);
        let handle = tokio::spawn(serve(m.clone(), path.clone()));
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;
        let resp = request(&path, &serde_json::json!({"method": "GetMetrics"})).await;
        assert_eq!(resp["rx_bytes"], 5);
        assert_eq!(resp["dropped_pkts"], 1);
        handle.abort();
        cleanup(&path);
    }
}
