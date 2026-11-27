// coverage: lab-only begin — session provisioning and TUN process supervision
// require the isolated control-plane lab and CAP_NET_ADMIN.
use anyhow::{anyhow, bail, Context, Result};
use base64::{engine::general_purpose::STANDARD, Engine as _};
use clap::Parser;
use reqwest::{header::HeaderValue, Certificate, Client, Url};
use serde::{Deserialize, Serialize};
use std::{
    fs::{self, OpenOptions},
    io::Write,
    net::{Ipv4Addr, SocketAddr},
    path::{Path, PathBuf},
    process::Stdio,
    time::Duration,
};
use tokio::{
    process::{Child, Command},
    time::sleep,
};
use uuid::Uuid;
use zeroize::{Zeroize, Zeroizing};

#[derive(Parser)]
#[command(
    name = "vpn-cli",
    about = "Provision and supervise one HiKablue lab client session"
)]
struct Args {
    #[arg(long)]
    control_url: Url,
    #[arg(long)]
    node_id: String,
    #[arg(long)]
    subject: String,
    #[arg(long)]
    ca_cert: PathBuf,
    #[arg(long)]
    remote: SocketAddr,
    #[arg(long)]
    peer: Ipv4Addr,
    #[arg(long, default_value_t = 24)]
    tun_prefix: u8,
    #[arg(long, default_value_t = 180)]
    ttl_seconds: u64,
    #[arg(long, default_value = "tun0")]
    tun_name: String,
    #[arg(long)]
    netns: Option<String>,
    #[arg(long)]
    engine: Option<PathBuf>,
    #[arg(long, default_value = "/run/hikablue/client")]
    state_dir: PathBuf,
    #[arg(long)]
    session_info: Option<PathBuf>,
    #[arg(long)]
    metrics_sock: Option<PathBuf>,
    #[arg(long, default_value_t = 250)]
    poll_interval_ms: u64,
}

#[derive(Deserialize)]
struct ApiResponse<T> {
    data: T,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct SessionView {
    session_id: String,
    status: String,
    virtual_ip: Option<String>,
    expires_at: Option<String>,
    key_epoch: Option<u64>,
    egress_key: Option<String>,
    ingress_key: Option<String>,
    sender_index: Option<u32>,
    peer_index: Option<u32>,
    session_token: Option<String>,
}

impl Drop for SessionView {
    fn drop(&mut self) {
        self.egress_key.zeroize();
        self.ingress_key.zeroize();
        self.session_token.zeroize();
    }
}

struct SessionInfo {
    session_id: String,
    session_token: Zeroizing<String>,
    subject: String,
    virtual_ip: Ipv4Addr,
    expires_at: String,
    initial_status: String,
    initial_key_epoch: u64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct SessionInfoFile<'a> {
    session_id: &'a str,
    session_token: &'a str,
    subject: &'a str,
    virtual_ip: Ipv4Addr,
    expires_at: &'a str,
    status: &'a str,
    key_epoch: u64,
}

struct ClientKeys {
    tx: Zeroizing<[u8; 32]>,
    rx: Zeroizing<[u8; 32]>,
    sender_index: u32,
    peer_index: u32,
}

struct EngineRun {
    child: Child,
    key_dir: Option<PathBuf>,
    pid_file: PathBuf,
}

struct DirectoryCleanup(Option<PathBuf>);

impl DirectoryCleanup {
    fn new(path: PathBuf) -> Self {
        Self(Some(path))
    }

    fn disarm(&mut self) {
        self.0 = None;
    }
}

impl Drop for DirectoryCleanup {
    fn drop(&mut self) {
        if let Some(path) = self.0.take() {
            if let Err(error) = fs::remove_dir_all(&path) {
                if error.kind() != std::io::ErrorKind::NotFound {
                    eprintln!(
                        "vpn-cli: failed to remove temporary protected directory {}: {error}",
                        path.display()
                    );
                }
            }
        }
    }
}

impl Drop for EngineRun {
    fn drop(&mut self) {
        if let Err(error) = self.remove_files() {
            eprintln!("vpn-cli: failed to remove protected engine files: {error}");
        }
    }
}

impl EngineRun {
    fn remove_files(&mut self) -> Result<()> {
        let pid_result = remove_file_if_exists(&self.pid_file).context("remove engine PID file");
        let key_result = match self.key_dir.take() {
            Some(key_dir) => match fs::remove_dir_all(key_dir) {
                Ok(()) => Ok(()),
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
                Err(error) => Err(error).context("remove protected key files"),
            },
            None => Ok(()),
        };

        match (pid_result, key_result) {
            (Ok(()), Ok(())) => Ok(()),
            (Err(error), Ok(())) | (Ok(()), Err(error)) => Err(error),
            (Err(pid_error), Err(key_error)) => {
                Err(key_error.context(format!("engine PID cleanup also failed: {pid_error:#}")))
            }
        }
    }
}

enum PollError {
    Temporary(anyhow::Error),
    Permanent(anyhow::Error),
}

#[tokio::main]
async fn main() {
    if let Err(error) = run().await {
        eprintln!("vpn-cli: {error:#}");
        std::process::exit(1);
    }
}

async fn run() -> Result<()> {
    let args = Args::parse();
    validate_args(&args)?;

    let client = build_client(&args.ca_cert)?;
    let sessions_url = args
        .control_url
        .join("/v1/sessions")
        .context("invalid control URL")?;
    let session_idempotency_key = Uuid::new_v4().to_string();
    let mut created =
        create_session(&client, sessions_url, &args, &session_idempotency_key).await?;

    let session_id = created.session_id.clone();
    Uuid::parse_str(&session_id).context("control plane returned an invalid session ID")?;
    let session_token = Zeroizing::new(
        created
            .session_token
            .take()
            .context("session response did not contain a session token")?,
    );
    let virtual_ip = created
        .virtual_ip
        .as_deref()
        .context("session response did not contain a virtual IP")?
        .parse::<Ipv4Addr>()
        .context("session response contained an invalid virtual IP")?;
    let expires_at = created
        .expires_at
        .take()
        .context("session response did not contain an expiration time")?;
    let initial_key_epoch = required_epoch(&created)?;
    created.egress_key.zeroize();
    created.ingress_key.zeroize();
    let info = SessionInfo {
        session_id,
        session_token,
        subject: args.subject.clone(),
        virtual_ip,
        expires_at,
        initial_status: created.status.clone(),
        initial_key_epoch,
    };

    let run_result = async {
        if let Some(path) = &args.session_info {
            write_session_info(path, &info)?;
        }
        println!(
            "vpn-cli: session {} created for {} at epoch {}",
            info.session_id, info.subject, info.initial_key_epoch
        );

        supervise(&client, &args, &info).await
    }
    .await;
    let revocation_result = revoke_session(&client, &args, &info).await;
    match (run_result, revocation_result) {
        (Ok(()), Ok(status)) => {
            println!(
                "vpn-cli: session {} revocation requested ({status})",
                info.session_id
            );
            Ok(())
        }
        (Err(error), Ok(status)) => {
            eprintln!("vpn-cli: session revocation requested ({status})");
            Err(error)
        }
        (Ok(()), Err(error)) => Err(error)
            .context("vpn-cli stopped but could not request control-plane session revocation"),
        (Err(error), Err(revocation_error)) => Err(error.context(format!(
            "session revocation also failed: {revocation_error:#}"
        ))),
    }
}

fn validate_args(args: &Args) -> Result<()> {
    if args.control_url.scheme() != "https" {
        bail!("--control-url must use HTTPS");
    }
    if args.node_id.trim().is_empty() || args.subject.trim().is_empty() {
        bail!("--node-id and --subject must not be blank");
    }
    if !(30..=86_400).contains(&args.ttl_seconds) {
        bail!("--ttl-seconds must be between 30 and 86400");
    }
    if !(16..=30).contains(&args.tun_prefix) {
        bail!("--tun-prefix must be between 16 and 30; full-tunnel prefixes are deferred");
    }
    if !(100..=30_000).contains(&args.poll_interval_ms) {
        bail!("--poll-interval-ms must be between 100 and 30000");
    }
    if let Some(netns) = &args.netns {
        if netns.is_empty()
            || !netns
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_' | b'.'))
        {
            bail!("--netns contains unsupported characters");
        }
    }
    Ok(())
}

fn build_client(ca_path: &Path) -> Result<Client> {
    let pem = fs::read(ca_path)
        .with_context(|| format!("read control-plane CA certificate {}", ca_path.display()))?;
    let ca = Certificate::from_pem(&pem).context("parse control-plane CA certificate")?;
    Client::builder()
        .https_only(true)
        .add_root_certificate(ca)
        .timeout(Duration::from_secs(5))
        .build()
        .context("build HTTPS client")
}

async fn create_session(
    client: &Client,
    url: Url,
    args: &Args,
    idempotency_key: &str,
) -> Result<SessionView> {
    let payload = serde_json::json!({
        "nodeId": args.node_id,
        "ttlSeconds": args.ttl_seconds
    });

    for attempt in 0..3 {
        let response = client
            .post(url.clone())
            .header("X-Subject", &args.subject)
            .header("Idempotency-Key", idempotency_key)
            .json(&payload)
            .send()
            .await;

        match response {
            Ok(response) if response.status().is_server_error() && attempt < 2 => {
                sleep(Duration::from_millis(250 * (attempt + 1))).await;
            }
            Ok(response) => {
                let response = response
                    .error_for_status()
                    .context("control plane rejected session creation")?;
                let envelope = response
                    .json::<ApiResponse<SessionView>>()
                    .await
                    .context("decode control-plane session response")?;
                return Ok(envelope.data);
            }
            Err(error) if (error.is_connect() || error.is_timeout()) && attempt < 2 => {
                sleep(Duration::from_millis(250 * (attempt + 1))).await;
            }
            Err(error) => return Err(error).context("create control-plane session"),
        }
    }

    Err(anyhow!("session creation did not complete after retries"))
}

fn write_session_info(path: &Path, info: &SessionInfo) -> Result<()> {
    let content = Zeroizing::new(serde_json::to_vec(&SessionInfoFile {
        session_id: &info.session_id,
        session_token: info.session_token.as_str(),
        subject: &info.subject,
        virtual_ip: info.virtual_ip,
        expires_at: &info.expires_at,
        status: &info.initial_status,
        key_epoch: info.initial_key_epoch,
    })?);
    write_private_new_file(path, &content)
}

async fn supervise(client: &Client, args: &Args, info: &SessionInfo) -> Result<()> {
    ensure_private_directory(&args.state_dir)?;
    let state_dir = args.state_dir.join(&info.session_id);
    create_private_directory(&state_dir)?;
    let manager_pid_file = state_dir.join("manager.pid");
    write_private_new_file(&manager_pid_file, std::process::id().to_string().as_bytes())?;
    let session_url = args
        .control_url
        .join(&format!("/v1/sessions/{}", info.session_id))
        .context("invalid session URL")?;
    let poll_interval = Duration::from_millis(args.poll_interval_ms);
    let mut running: Option<EngineRun> = None;
    let mut active_epoch: Option<u64> = None;
    let mut first_poll = true;
    let mut shutdown = shutdown_signal();
    tokio::pin!(shutdown);

    let loop_result: Result<()> = loop {
        if let Some(engine) = running.as_mut() {
            match engine.child.try_wait() {
                Ok(Some(status)) => {
                    break Err(anyhow!("vpn-engine exited unexpectedly with {status}"));
                }
                Ok(None) => {}
                Err(error) => break Err(error).context("check vpn-engine process"),
            }
        }

        if !first_poll {
            let delay = if active_epoch.is_some() {
                poll_interval
            } else {
                poll_interval.min(Duration::from_millis(250))
            };
            tokio::select! {
                signal = &mut shutdown => {
                    match signal {
                        Ok(()) => break Ok(()),
                        Err(error) => break Err(error).context("wait for shutdown signal"),
                    }
                }
                _ = sleep(delay) => {}
            }
        }
        first_poll = false;

        let mut view =
            match fetch_session(client, &session_url, &info.subject, &info.session_token).await {
                Ok(view) => view,
                Err(PollError::Temporary(error)) => {
                    eprintln!("vpn-cli: session poll temporarily failed; retrying: {error}");
                    continue;
                }
                Err(PollError::Permanent(error)) => break Err(error),
            };
        if view.session_id != info.session_id {
            break Err(anyhow!("control plane returned a different session ID"));
        }
        let expected_ip = info.virtual_ip.to_string();
        if view.virtual_ip.as_deref() != Some(expected_ip.as_str()) {
            break Err(anyhow!("control plane changed the session virtual IP"));
        }

        let epoch = match required_epoch(&view) {
            Ok(epoch) => epoch,
            Err(error) => break Err(error),
        };
        let is_active = view.status == "active";
        if !is_active {
            if let Some(engine) = running.take() {
                if let Err(error) = stop_engine(engine).await {
                    break Err(error);
                }
                active_epoch = None;
            }
            match view.status.as_str() {
                "pending" => continue,
                "revoking" | "revoked" | "failed" => {
                    break Err(anyhow!("session {} is {}", info.session_id, view.status));
                }
                state => break Err(anyhow!("unknown session state '{state}'")),
            }
        }

        if let Some(current_epoch) = active_epoch {
            if epoch < current_epoch {
                break Err(anyhow!("control plane returned a stale key epoch"));
            }
            if epoch == current_epoch {
                continue;
            }
            if let Some(engine) = running.take() {
                if let Err(error) = stop_engine(engine).await {
                    break Err(error);
                }
            }
            active_epoch = None;
        }

        let keys = match client_keys(&mut view) {
            Ok(keys) => keys,
            Err(error) => break Err(error),
        };
        let engine = match spawn_engine(args, &state_dir, epoch, info.virtual_ip, keys).await {
            Ok(engine) => engine,
            Err(error) => break Err(error),
        };
        let pid = engine.child.id().unwrap_or_default();
        println!(
            "vpn-cli: session {} active at epoch {} (engine PID {})",
            info.session_id, epoch, pid
        );
        running = Some(engine);
        active_epoch = Some(epoch);
    };

    if let Some(engine) = running.take() {
        if let Err(cleanup_error) = stop_engine(engine).await {
            let manager_cleanup = remove_file_if_exists(&manager_pid_file);
            return match (loop_result, manager_cleanup) {
                (Ok(()), Ok(())) => Err(cleanup_error),
                (Ok(()), Err(manager_error)) => Err(cleanup_error.context(format!(
                    "manager PID cleanup also failed: {manager_error:#}"
                ))),
                (Err(error), Ok(())) => {
                    Err(error.context(format!("engine cleanup also failed: {cleanup_error:#}")))
                }
                (Err(error), Err(manager_error)) => Err(error.context(format!(
                    "engine cleanup also failed: {cleanup_error:#}; manager PID cleanup also failed: {manager_error:#}"
                ))),
            };
        }
    }
    remove_file_if_exists(&manager_pid_file)?;
    loop_result
}

async fn fetch_session(
    client: &Client,
    url: &Url,
    subject: &str,
    token: &str,
) -> std::result::Result<SessionView, PollError> {
    let mut token_header =
        HeaderValue::from_str(token).map_err(|error| PollError::Permanent(error.into()))?;
    token_header.set_sensitive(true);
    let response = client
        .get(url.clone())
        .header("X-Subject", subject)
        .header("X-Session-Token", token_header)
        .send()
        .await
        .map_err(|error| {
            if error.is_connect() || error.is_timeout() {
                PollError::Temporary(error.into())
            } else {
                PollError::Permanent(error.into())
            }
        })?;

    let status = response.status();
    if status.is_server_error()
        || status == reqwest::StatusCode::REQUEST_TIMEOUT
        || status == reqwest::StatusCode::TOO_MANY_REQUESTS
    {
        return Err(PollError::Temporary(anyhow!(
            "control API returned HTTP {status}"
        )));
    }
    if !status.is_success() {
        return Err(PollError::Permanent(anyhow!(
            "control API returned HTTP {status}"
        )));
    }

    response
        .json::<ApiResponse<SessionView>>()
        .await
        .map(|envelope| envelope.data)
        .map_err(|error| PollError::Temporary(error.into()))
}

async fn revoke_session(client: &Client, args: &Args, info: &SessionInfo) -> Result<String> {
    let url = args
        .control_url
        .join(&format!("/v1/sessions/{}", info.session_id))
        .context("invalid session URL")?;
    let mut token_header = HeaderValue::from_str(info.session_token.as_str())
        .context("session token cannot be used as an HTTP header")?;
    token_header.set_sensitive(true);
    let response = client
        .delete(url)
        .header("X-Subject", &info.subject)
        .header("X-Session-Token", token_header)
        .send()
        .await
        .context("request control-plane session revocation")?
        .error_for_status()
        .context("control plane rejected session revocation")?;
    let envelope = response
        .json::<ApiResponse<SessionView>>()
        .await
        .context("decode control-plane session revocation response")?;
    if envelope.data.session_id != info.session_id {
        bail!("control plane revoked a different session");
    }
    if !matches!(envelope.data.status.as_str(), "revoking" | "revoked") {
        bail!(
            "control plane returned invalid session state after revocation: {}",
            envelope.data.status
        );
    }

    Ok(envelope.data.status.clone())
}

fn required_epoch(view: &SessionView) -> Result<u64> {
    let epoch = view
        .key_epoch
        .context("control-plane session response omitted keyEpoch")?;
    if epoch == 0 {
        bail!("control-plane session response contains an invalid keyEpoch");
    }
    Ok(epoch)
}

fn client_keys(view: &mut SessionView) -> Result<ClientKeys> {
    let egress = decode_key("egressKey", view.egress_key.take())?;
    let ingress = decode_key("ingressKey", view.ingress_key.take())?;
    let server_sender = view
        .sender_index
        .context("control-plane session response omitted senderIndex")?;
    let server_peer = view
        .peer_index
        .context("control-plane session response omitted peerIndex")?;
    if server_sender == 0 || server_peer == 0 || server_sender == server_peer {
        bail!("control-plane session response contains invalid wire indexes");
    }

    Ok(ClientKeys {
        tx: ingress,
        rx: egress,
        sender_index: server_peer,
        peer_index: server_sender,
    })
}

fn decode_key(name: &str, encoded: Option<String>) -> Result<Zeroizing<[u8; 32]>> {
    let encoded = Zeroizing::new(
        encoded.ok_or_else(|| anyhow!("control-plane session response omitted {name}"))?,
    );
    let decoded = Zeroizing::new(
        STANDARD
            .decode(encoded.as_bytes())
            .with_context(|| format!("decode control-plane {name}"))?,
    );
    if decoded.len() != 32 {
        bail!("control-plane {name} must decode to 32 bytes");
    }
    let mut key = Zeroizing::new([0u8; 32]);
    key.copy_from_slice(&decoded);
    Ok(key)
}

async fn spawn_engine(
    args: &Args,
    state_dir: &Path,
    epoch: u64,
    virtual_ip: Ipv4Addr,
    keys: ClientKeys,
) -> Result<EngineRun> {
    let key_dir = state_dir.join(format!("keys-{epoch}-{}", Uuid::new_v4()));
    create_private_directory(&key_dir)?;
    let mut key_dir_cleanup = DirectoryCleanup::new(key_dir.clone());
    let tx_key_path = key_dir.join("tx.key");
    let rx_key_path = key_dir.join("rx.key");
    let tx_hex = encode_hex(&keys.tx);
    let rx_hex = encode_hex(&keys.rx);
    write_private_new_file(&tx_key_path, tx_hex.as_bytes())?;
    write_private_new_file(&rx_key_path, rx_hex.as_bytes())?;

    let engine_path = resolve_engine_path(args.engine.as_deref())?;
    let metrics_sock = args
        .metrics_sock
        .clone()
        .unwrap_or_else(|| state_dir.join("engine.sock"));
    let mut command = if let Some(netns) = &args.netns {
        let mut command = Command::new("ip");
        command.args(["netns", "exec", netns]);
        command.arg(&engine_path);
        command
    } else {
        Command::new(&engine_path)
    };
    command
        .args([
            "client",
            "--remote",
            &args.remote.to_string(),
            "--tun-cidr",
            &format!("{virtual_ip}/{}", args.tun_prefix),
            "--peer",
            &args.peer.to_string(),
            "--tun-name",
            &args.tun_name,
            "--metrics-sock",
            metrics_sock
                .to_str()
                .context("metrics socket path is not valid UTF-8")?,
            "--tx-key-file",
            tx_key_path
                .to_str()
                .context("transmit key path is not valid UTF-8")?,
            "--rx-key-file",
            rx_key_path
                .to_str()
                .context("receive key path is not valid UTF-8")?,
            "--sender-index",
            &keys.sender_index.to_string(),
            "--peer-index",
            &keys.peer_index.to_string(),
        ])
        .stdin(Stdio::null())
        .stdout(Stdio::inherit())
        .stderr(Stdio::inherit())
        .kill_on_drop(true);

    let mut child = command.spawn().context("start vpn-engine client")?;
    let pid = child
        .id()
        .context("vpn-engine did not expose a process ID")?;
    let pid_file = state_dir.join("engine.pid");
    if let Err(error) = write_private_new_file(&pid_file, pid.to_string().as_bytes()) {
        let error = error.context("write vpn-engine PID file");
        let process_cleanup = stop_child(&mut child).await;
        let pid_cleanup = remove_file_if_exists(&pid_file);
        return match (process_cleanup, pid_cleanup) {
            (Ok(()), Ok(())) => Err(error),
            (Err(process_error), Ok(())) => Err(error.context(format!(
                "vpn-engine cleanup also failed: {process_error:#}"
            ))),
            (Ok(()), Err(pid_error)) => Err(error.context(format!(
                "PID-file cleanup also failed: {pid_error:#}"
            ))),
            (Err(process_error), Err(pid_error)) => Err(error.context(format!(
                "vpn-engine cleanup also failed: {process_error:#}; PID-file cleanup also failed: {pid_error:#}"
            ))),
        };
    }

    let engine = EngineRun {
        child,
        key_dir: Some(key_dir),
        pid_file,
    };
    key_dir_cleanup.disarm();
    Ok(engine)
}

fn resolve_engine_path(explicit: Option<&Path>) -> Result<PathBuf> {
    let path = if let Some(path) = explicit {
        path.to_path_buf()
    } else {
        let executable = std::env::current_exe().context("locate vpn-cli executable")?;
        executable
            .parent()
            .context("vpn-cli executable has no parent directory")?
            .join(if cfg!(windows) {
                "vpn-engine.exe"
            } else {
                "vpn-engine"
            })
    };
    if !path.is_file() {
        bail!("vpn-engine binary not found: {}", path.display());
    }
    Ok(path)
}

fn ensure_private_directory(path: &Path) -> Result<()> {
    match fs::symlink_metadata(path) {
        Ok(metadata) => {
            if metadata.file_type().is_symlink() || !metadata.is_dir() {
                bail!("state path is not a real directory: {}", path.display());
            }
            check_directory_permissions(path, &metadata)?;
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            create_private_directory_all(path)?;
        }
        Err(error) => return Err(error).with_context(|| format!("inspect {}", path.display())),
    }
    Ok(())
}

fn create_private_directory(path: &Path) -> Result<()> {
    #[cfg(unix)]
    {
        use std::os::unix::fs::DirBuilderExt;
        let mut builder = fs::DirBuilder::new();
        builder.mode(0o700);
        builder
            .create(path)
            .with_context(|| format!("create protected directory {}", path.display()))?;
    }
    #[cfg(not(unix))]
    {
        fs::create_dir(path)
            .with_context(|| format!("create protected directory {}", path.display()))?;
    }
    Ok(())
}

fn create_private_directory_all(path: &Path) -> Result<()> {
    #[cfg(unix)]
    {
        use std::os::unix::fs::DirBuilderExt;
        let mut builder = fs::DirBuilder::new();
        builder.recursive(true).mode(0o700);
        builder
            .create(path)
            .with_context(|| format!("create protected state directory {}", path.display()))?;
    }
    #[cfg(not(unix))]
    {
        fs::create_dir_all(path)
            .with_context(|| format!("create state directory {}", path.display()))?;
    }
    Ok(())
}

#[cfg(unix)]
fn check_directory_permissions(path: &Path, metadata: &fs::Metadata) -> Result<()> {
    use std::os::unix::fs::PermissionsExt;
    if metadata.permissions().mode() & 0o077 != 0 {
        bail!(
            "state directory must not be accessible by group or other: {}",
            path.display()
        );
    }
    Ok(())
}

#[cfg(not(unix))]
fn check_directory_permissions(_path: &Path, _metadata: &fs::Metadata) -> Result<()> {
    Ok(())
}

fn write_private_new_file(path: &Path, contents: &[u8]) -> Result<()> {
    let parent = path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."));
    if !parent.is_dir() {
        bail!("parent directory does not exist: {}", parent.display());
    }
    let mut options = OpenOptions::new();
    options.write(true).create_new(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.mode(0o600);
    }
    let mut file = options
        .open(path)
        .with_context(|| format!("create protected file {}", path.display()))?;
    let write_result = file.write_all(contents).and_then(|_| file.sync_all());
    if let Err(error) = write_result {
        drop(file);
        let error = anyhow!(error).context(format!("write protected file {}", path.display()));
        return match remove_file_if_exists(path) {
            Ok(()) => Err(error),
            Err(cleanup_error) => Err(error.context(format!(
                "partial protected file cleanup also failed: {cleanup_error:#}"
            ))),
        };
    }
    Ok(())
}

fn remove_file_if_exists(path: &Path) -> Result<()> {
    match fs::remove_file(path) {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(error).with_context(|| format!("remove {}", path.display())),
    }
}

fn encode_hex(key: &[u8; 32]) -> Zeroizing<String> {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut encoded = Zeroizing::new(String::with_capacity(64));
    for byte in key {
        encoded.push(HEX[(byte >> 4) as usize] as char);
        encoded.push(HEX[(byte & 0x0f) as usize] as char);
    }
    encoded
}

async fn stop_engine(mut engine: EngineRun) -> Result<()> {
    stop_child(&mut engine.child).await?;
    engine.remove_files()?;
    Ok(())
}

async fn stop_child(child: &mut Child) -> Result<()> {
    if child
        .try_wait()
        .context("check vpn-engine before stopping")?
        .is_none()
    {
        match child.kill().await {
            Ok(()) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => return Err(error).context("stop vpn-engine"),
        }
    }
    child.wait().await.context("wait for vpn-engine")?;
    Ok(())
}

async fn shutdown_signal() -> std::io::Result<()> {
    #[cfg(unix)]
    {
        let mut terminate =
            tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())?;
        tokio::select! {
            result = tokio::signal::ctrl_c() => result,
            _ = terminate.recv() => Ok(()),
        }
    }
    #[cfg(not(unix))]
    {
        tokio::signal::ctrl_c().await
    }
}

// coverage: lab-only end

#[cfg(test)]
mod tests {
    use super::*;

    fn sample_view() -> SessionView {
        SessionView {
            session_id: Uuid::new_v4().to_string(),
            status: "active".to_owned(),
            virtual_ip: Some("10.80.1.129".to_owned()),
            expires_at: Some("2030-01-01T00:00:00+00:00".to_owned()),
            key_epoch: Some(3),
            egress_key: Some(STANDARD.encode([1u8; 32])),
            ingress_key: Some(STANDARD.encode([2u8; 32])),
            sender_index: Some(11),
            peer_index: Some(22),
            session_token: Some("secret-token".to_owned()),
        }
    }

    #[test]
    fn client_key_material_reverses_server_directions() {
        let mut view = sample_view();

        let keys = client_keys(&mut view).expect("valid server key material");

        assert_eq!(&*keys.tx, &[2u8; 32]);
        assert_eq!(&*keys.rx, &[1u8; 32]);
        assert_eq!(keys.sender_index, 22);
        assert_eq!(keys.peer_index, 11);
        assert!(view.egress_key.is_none());
        assert!(view.ingress_key.is_none());
    }

    #[test]
    fn malformed_key_length_is_rejected() {
        let encoded = STANDARD.encode([1u8; 31]);

        let error = decode_key("egressKey", Some(encoded)).expect_err("31-byte key must fail");

        assert!(error.to_string().contains("32 bytes"));
    }

    #[test]
    fn full_tunnel_prefixes_are_not_supported() {
        let mut args = test_args();
        args.tun_prefix = 0;
        assert!(validate_args(&args).is_err());
        args.tun_prefix = 24;
        assert!(validate_args(&args).is_ok());
    }

    #[test]
    fn session_info_file_does_not_contain_key_material() {
        let info = SessionInfo {
            session_id: "00000000-0000-4000-8000-000000000001".to_owned(),
            session_token: Zeroizing::new("session-token".to_owned()),
            subject: "alice".to_owned(),
            virtual_ip: Ipv4Addr::new(10, 80, 1, 129),
            expires_at: "2030-01-01T00:00:00+00:00".to_owned(),
            initial_status: "pending".to_owned(),
            initial_key_epoch: 1,
        };

        let content = serde_json::to_string(&SessionInfoFile {
            session_id: &info.session_id,
            session_token: info.session_token.as_str(),
            subject: &info.subject,
            virtual_ip: info.virtual_ip,
            expires_at: &info.expires_at,
            status: &info.initial_status,
            key_epoch: info.initial_key_epoch,
        })
        .expect("session info serializes");

        assert!(!content.contains("egressKey"));
        assert!(!content.contains("ingressKey"));
        assert!(!content.contains(&STANDARD.encode([1u8; 32])));
    }

    #[test]
    fn uncommitted_key_directory_is_removed() {
        let path = std::env::temp_dir().join(format!("hikablue-key-dir-{}", Uuid::new_v4()));
        fs::create_dir(&path).expect("temporary key directory is created");
        let file = path.join("tx.key");
        fs::write(&file, "test-only").expect("temporary key file is created");

        drop(DirectoryCleanup::new(path.clone()));

        assert!(!path.exists());
    }

    #[cfg(unix)]
    #[test]
    fn created_state_directory_is_private_from_the_start() {
        use std::os::unix::fs::PermissionsExt;

        let path = std::env::temp_dir().join(format!("hikablue-state-{}", Uuid::new_v4()));

        ensure_private_directory(&path).expect("private state directory is created");

        let mode = fs::metadata(&path)
            .expect("state directory metadata exists")
            .permissions()
            .mode();
        assert_eq!(mode & 0o077, 0);
        fs::remove_dir(&path).expect("state directory is cleaned up");
    }

    #[cfg(unix)]
    #[test]
    fn protected_file_is_created_with_owner_only_permissions() {
        use std::os::unix::fs::PermissionsExt;

        let path = std::env::temp_dir().join(format!("hikablue-secret-{}", Uuid::new_v4()));

        write_private_new_file(&path, b"test-only").expect("protected file is created");

        let mode = fs::metadata(&path)
            .expect("protected file metadata exists")
            .permissions()
            .mode();
        assert_eq!(mode & 0o777, 0o600);
        fs::remove_file(&path).expect("protected file is cleaned up");
    }

    fn test_args() -> Args {
        Args {
            control_url: Url::parse("https://127.0.0.1:7443").expect("valid URL"),
            node_id: "edge-1".to_owned(),
            subject: "alice".to_owned(),
            ca_cert: PathBuf::from("ca.crt"),
            remote: "192.168.50.1:51820".parse().expect("valid endpoint"),
            peer: Ipv4Addr::new(10, 80, 1, 1),
            tun_prefix: 24,
            ttl_seconds: 180,
            tun_name: "tun0".to_owned(),
            netns: None,
            engine: None,
            state_dir: PathBuf::from("/tmp/vpn-cli-test"),
            session_info: None,
            metrics_sock: None,
            poll_interval_ms: 250,
        }
    }
}
