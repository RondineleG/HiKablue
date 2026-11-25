//! The forwarding loop: IPv4 between a TUN device and UDP.
//!
//! Two 2048-byte buffers are allocated once and reused for every packet, so
//! the hot path performs no per-packet heap allocation. `MAX_MTU` bounds the
//! configurable inner MTU to what those buffers can carry after the 30-byte
//! crypto overhead (14-byte header headroom + 16-byte tag tailroom).
//!
//! Phase 2 adds the optional AEAD wire format: with `--key`, every datagram
//! carries the 14-byte cleartext prefix (type, sender_index, nonce) and a
//! 16-byte Poly1305 tag; without it the M2 plaintext format is kept so old
//! lab runs still work.

use std::io;
use std::net::{Ipv4Addr, SocketAddr, SocketAddrV4};
use std::os::unix::io::AsRawFd;
use std::path::PathBuf;
use std::sync::atomic::Ordering;
use std::time::Duration;

use tokio::io::Interest;
use tokio::net::UdpSocket;
use tokio::signal::unix::{signal, SignalKind};
use zeroize::Zeroizing;

use crate::cli::Ipv4Cidr;
use crate::control;
use crate::crypto::{CryptoError, SessionState};
use crate::metrics::{self, Metrics};
use crate::packet::{destination_ip, source_ip, validate_ipv4};
use crate::sessions::SessionTable;
use crate::tun::{configure_interface, Tun};
use crate::wire::{frame, HEADER_LEN, MSG_DATA, MSG_KEEPALIVE, TOTAL_OVERHEAD};

/// Fixed reusable buffer size.
pub const BUFFER_SIZE: usize = 2048;
/// Largest supported inner MTU: the buffer must also fit the 14-byte
/// cleartext prefix (headroom) and the 16-byte tag (tailroom).
pub const MAX_MTU: u16 = (BUFFER_SIZE - TOTAL_OVERHEAD) as u16;
/// Largest inner MTU that keeps the encrypted outer datagram inside a
/// 1500-byte path: 1500 minus 58 bytes of overhead (20 IPv4 + 8 UDP +
/// 14 header + 16 tag).
pub const MAX_MTU_ENCRYPTED: u16 = 1500 - 58;
/// Same ceiling over an IPv6 outer: 40 IPv6 + 8 UDP + 30 = 78 bytes.
pub const MAX_MTU_ENCRYPTED_V6: u16 = 1500 - 78;

pub struct Config {
    pub bind: SocketAddr,
    /// Client only: the fixed remote endpoint.
    pub remote: Option<SocketAddr>,
    /// Virtual IP the peer must present inside the tunnel — the learned
    /// endpoint on the server, the server's VIP on the client.
    pub peer_vip: Option<Ipv4Addr>,
    pub tun_cidr: Ipv4Cidr,
    pub tun_name: String,
    pub mtu: u16,
    /// Unix socket that answers GetMetrics.
    pub metrics_sock: PathBuf,
    pub metrics_group: Option<String>,
    /// Unix socket that accepts key-lifecycle ops (StagePeer/Activate/
    /// Retire/RevokePeer). Carries key material: mode 0600 plus a
    /// SO_PEERCRED check, separate from the read-only metrics socket.
    pub control_sock: Option<PathBuf>,
    /// UID the control socket is chowned to and authorized for — the
    /// unprivileged agent's uid. Root always passes the peer-credential
    /// check.
    pub control_uid: Option<u32>,
    /// Optional 32-byte session key enabling the AEAD wire format.
    pub key: Option<Zeroizing<[u8; 32]>>,
    /// Exact directional key pair for control-plane-issued lab sessions.
    /// When present it takes precedence over the legacy derived --key.
    pub tx_key: Option<Zeroizing<[u8; 32]>>,
    pub rx_key: Option<Zeroizing<[u8; 32]>>,
    /// Session index stamped on outbound packets; selects this side's
    /// derived direction key.
    pub sender_index: u32,
    /// Session index the peer stamps on its packets; selects the receive
    /// direction key. Must differ from `sender_index` when keyed.
    pub peer_index: u32,
    /// Idle time before a KEEPALIVE datagram is emitted (only when a
    /// session key is configured).
    pub keepalive_interval: Duration,
}

impl Config {
    fn validate(&self) -> io::Result<()> {
        if self.mtu == 0 || self.mtu > MAX_MTU {
            return Err(invalid(format!(
                "mtu {} outside 1..={MAX_MTU} (fixed {BUFFER_SIZE}-byte buffers)",
                self.mtu
            )));
        }
        // A client can never reach a remote of the other family: a v4
        // socket cannot send to v6 and vice versa. Reject at startup
        // instead of failing the first connect.
        if let Some(remote) = self.remote {
            if remote.is_ipv6() != self.bind.is_ipv6() {
                return Err(invalid(
                    "bind and remote address families must match".into(),
                ));
            }
        }
        // Client mode drops anything the peer did not send: without an
        // expected peer VIP the ingress check could never run.
        if self.remote.is_some() && self.peer_vip.is_none() {
            return Err(invalid("client mode requires a peer vip".into()));
        }
        if self.remote.is_none() && self.control_sock.is_none() && self.peer_vip.is_none() {
            return Err(invalid("plaintext server mode requires a peer vip".into()));
        }
        if self.tx_key.is_some() != self.rx_key.is_some() {
            return Err(invalid(
                "tx_key and rx_key must be configured together".into(),
            ));
        }
        if self.key.is_some() && self.tx_key.is_some() {
            return Err(invalid(
                "--key and --tx-key/--rx-key are mutually exclusive".into(),
            ));
        }
        if let (Some(tx), Some(rx)) = (&self.tx_key, &self.rx_key) {
            if tx[..] == rx[..] {
                return Err(invalid("tx_key and rx_key must differ".into()));
            }
        }
        if let Some(peer) = self.peer_vip {
            if !self.tun_cidr.contains(peer) {
                return Err(invalid(format!(
                    "peer {peer} is outside tunnel subnet {}",
                    self.tun_cidr
                )));
            }
            if peer == self.tun_cidr.addr {
                return Err(invalid(format!(
                    "peer {peer} equals this node's tunnel address {}",
                    self.tun_cidr.addr
                )));
            }
        }
        if self.key.is_some() || self.tx_key.is_some() {
            if self.sender_index == 0 || self.peer_index == 0 {
                return Err(invalid(
                    "sender_index and peer_index must both be nonzero when keyed".into(),
                ));
            }
            // The indexes select the derived direction keys; identical
            // indexes would collapse both directions onto one key.
            if self.sender_index == self.peer_index {
                return Err(invalid("sender_index and peer_index must differ".into()));
            }
        }
        // Keepalive and MTU rules apply whenever the engine can become
        // keyed — a fixed --key now, or a StagePeer over the control
        // socket later. A zero interval would re-arm instantly and win
        // every biased select! round once an epoch activates.
        if self.key.is_some() || self.tx_key.is_some() || self.control_sock.is_some() {
            if self.keepalive_interval.is_zero() {
                return Err(invalid(
                    "keepalive interval must be greater than zero".into(),
                ));
            }
            let max_mtu = if self.outer_is_ipv6() {
                MAX_MTU_ENCRYPTED_V6
            } else {
                MAX_MTU_ENCRYPTED
            };
            if self.mtu > max_mtu {
                return Err(invalid(format!(
                    "mtu {} above {max_mtu} (1500-byte path minus {}-byte outer overhead)",
                    self.mtu,
                    if self.outer_is_ipv6() { 78 } else { 58 }
                )));
            }
        }
        Ok(())
    }

    /// Outer transport family the socket will actually use. For the client
    /// the connected remote decides; for the server it's the bind address
    /// — `[::]` binds dual-stack and IPv4 peers arrive as IPv4-mapped, so
    /// the conservative IPv6 overhead applies there too.
    fn outer_is_ipv6(&self) -> bool {
        match self.remote {
            Some(remote) => remote.is_ipv6(),
            None => self.bind.is_ipv6(),
        }
    }
}

fn invalid(msg: String) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidInput, msg)
}

/// What the server does with one inbound UDP datagram: it has already passed
/// structural validation, so only the endpoint rules remain.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ServerInbound {
    /// Pin the sender's outer endpoint as the lab peer.
    Learn,
    /// Sender is the pinned peer: forward to TUN.
    Forward,
    /// Wrong inner source or a different endpoint: drop.
    Drop,
}

/// Server-side peer selection, kept pure so it can be tested without sockets.
/// Learning requires the inner source IP to equal the configured peer virtual
/// IP and the inner destination to equal this node's own virtual IP — input
/// validation, not authentication.
fn classify_server_packet(
    learned: Option<SocketAddr>,
    from: SocketAddr,
    inner_src: Ipv4Addr,
    inner_dst: Ipv4Addr,
    peer_vip: Ipv4Addr,
    local_vip: Ipv4Addr,
) -> ServerInbound {
    let addressed_to_us = inner_src == peer_vip && inner_dst == local_vip;
    match learned {
        // The inner source and destination are re-checked on every datagram
        // — learning does not suspend source validation, so the pinned
        // endpoint can never inject packets from or toward other virtual IPs.
        None if addressed_to_us => ServerInbound::Learn,
        Some(peer) if peer == from && addressed_to_us => ServerInbound::Forward,
        _ => ServerInbound::Drop,
    }
}

/// Receives one UDP datagram through `recvmsg(MSG_TRUNC)` so the return value
/// is the *real* datagram length even when it exceeds `buf`. Without this, a
/// truncated datagram could present a valid-looking prefix and be injected
/// into TUN as if it were a complete packet.
async fn recv_datagram(socket: &UdpSocket, buf: &mut [u8]) -> io::Result<(usize, SocketAddr)> {
    socket
        .async_io(Interest::READABLE, || unsafe {
            let mut storage: libc::sockaddr_storage = std::mem::zeroed();
            let mut iov = libc::iovec {
                iov_base: buf.as_mut_ptr() as *mut libc::c_void,
                iov_len: buf.len(),
            };
            let mut msg: libc::msghdr = std::mem::zeroed();
            msg.msg_name = &mut storage as *mut _ as *mut libc::c_void;
            msg.msg_namelen = std::mem::size_of::<libc::sockaddr_storage>() as libc::socklen_t;
            msg.msg_iov = &mut iov;
            msg.msg_iovlen = 1;
            let n = libc::recvmsg(socket.as_raw_fd(), &mut msg, libc::MSG_TRUNC);
            if n < 0 {
                return Err(io::Error::last_os_error());
            }
            let addr = sockaddr_to_std(&storage, msg.msg_namelen)?;
            Ok((n as usize, addr))
        })
        .await
}

fn sockaddr_to_std(
    storage: &libc::sockaddr_storage,
    len: libc::socklen_t,
) -> io::Result<SocketAddr> {
    match storage.ss_family as libc::c_int {
        libc::AF_INET => {
            if (len as usize) < std::mem::size_of::<libc::sockaddr_in>() {
                return Err(invalid("short IPv4 source address".into()));
            }
            let sa: &libc::sockaddr_in = unsafe { &*(storage as *const _ as *const _) };
            // s_addr is stored in network byte order; from_be yields the
            // host-order value Ipv4Addr::from expects.
            let ip = Ipv4Addr::from(u32::from_be(sa.sin_addr.s_addr));
            Ok(SocketAddr::V4(SocketAddrV4::new(
                ip,
                u16::from_be(sa.sin_port),
            )))
        }
        libc::AF_INET6 => {
            // A `[::]` bind yields AF_INET6 senders, including IPv4-mapped
            // ones; rejecting them would silently stall the tunnel.
            if (len as usize) < std::mem::size_of::<libc::sockaddr_in6>() {
                return Err(invalid("short IPv6 source address".into()));
            }
            let sa: &libc::sockaddr_in6 = unsafe { &*(storage as *const _ as *const _) };
            Ok(SocketAddr::V6(std::net::SocketAddrV6::new(
                std::net::Ipv6Addr::from(sa.sin6_addr.s6_addr),
                u16::from_be(sa.sin6_port),
                u32::from_be(sa.sin6_flowinfo),
                sa.sin6_scope_id,
            )))
        }
        family => Err(invalid(format!("unexpected address family {family}"))),
    }
}

/// Runs the forwarding loop until SIGINT or SIGTERM. The TUN descriptor is
/// dropped on return, which removes the transient interface.
// coverage: lab-only begin — opens /dev/net/tun and configures the
// interface; needs CAP_NET_ADMIN, exercised by the lab steps.
pub async fn run(cfg: Config) -> io::Result<()> {
    cfg.validate()?;
    let tun = Tun::open(&cfg.tun_name)?;
    configure_interface(&cfg.tun_name, &cfg.tun_cidr.to_string(), cfg.mtu)?;
    run_loop(cfg, tun).await
}
// coverage: lab-only end

/// The forwarding loop itself, split from `run` so tests can feed it a fake
/// TUN (a socketpair end) and real loopback UDP sockets.
async fn run_loop(cfg: Config, tun: Tun) -> io::Result<()> {
    cfg.validate()?;
    let is_server = cfg.remote.is_none();

    let socket = UdpSocket::bind(cfg.bind).await?;
    if let Some(remote) = cfg.remote {
        socket.connect(remote).await?;
    }

    let role = if is_server { "server" } else { "client" };
    eprintln!(
        "vpn-engine {role}: {} mtu={} bind={}",
        cfg.tun_cidr,
        cfg.mtu,
        socket.local_addr()?,
    );

    let metrics = Metrics::new();
    let metrics_listener =
        metrics::bind(&cfg.metrics_sock, cfg.metrics_group.as_deref()).map_err(|e| {
            match e.kind() {
                io::ErrorKind::Other => e,
                _ => io::Error::new(
                    e.kind(),
                    format!("{e} (use --metrics-sock to pick a writable path)"),
                ),
            }
        })?;
    let initial = match (cfg.tx_key, cfg.rx_key) {
        (Some(tx), Some(rx)) => Some(SessionState::new(
            0,
            tx,
            rx,
            cfg.sender_index,
            cfg.peer_index,
        )),
        (None, None) => cfg
            .key
            .map(|key| SessionState::from_lab_psk(key, cfg.sender_index, cfg.peer_index)),
        // validate() rejects half-configured pairs before the loop starts.
        _ => unreachable!(),
    };
    let sessions = std::sync::Arc::new(SessionTable::new(
        initial,
        cfg.peer_vip,
        cfg.control_sock.is_some(),
    ));
    let control_task = if let Some(path) = &cfg.control_sock {
        let listener = control::bind(path, cfg.control_uid)?;
        Some(tokio::spawn(control::serve(
            sessions.clone(),
            metrics.clone(),
            listener,
            cfg.control_uid,
        )))
    } else {
        None
    };
    let metrics_task = tokio::spawn(metrics::serve(
        metrics.clone(),
        metrics_listener,
        Some(sessions.clone()),
    ));
    if !is_server {
        metrics.peers.store(1, Ordering::Relaxed);
    }

    // Plaintext compatibility mode still pins the one fixed peer here.
    // Keyed peers pin endpoints inside SessionTable so revocation removes
    // exactly one client's endpoint and leaves the others untouched.
    let mut learned_peer: Option<SocketAddr> = None;
    let mut tun_buf = [0u8; BUFFER_SIZE];
    let mut udp_buf = [0u8; BUFFER_SIZE];
    let mut sigterm = signal(SignalKind::terminate())?;
    let mut next_keepalive = tokio::time::Instant::now() + cfg.keepalive_interval;

    loop {
        if is_server {
            metrics
                .peers
                .store(sessions.endpoint_count() as u64, Ordering::Relaxed);
        }
        tokio::select! {
            biased;
            result = tokio::signal::ctrl_c() => {
                result?;
                break;
            }
            _ = sigterm.recv() => {
                break;
            }
            _ = tokio::time::sleep_until(next_keepalive) => {
                next_keepalive = tokio::time::Instant::now() + cfg.keepalive_interval;
                if !sessions.keyed() {
                    continue;
                }
                if is_server {
                    for target in sessions.active_targets() {
                        let wire_len = match target.session.seal(&mut tun_buf, 0, MSG_KEEPALIVE) {
                            Ok(n) => n,
                            Err(_) => continue,
                        };
                        if let Some(endpoint) = target.endpoint {
                            if let Ok(sent_n) = socket.send_to(&tun_buf[..wire_len], endpoint).await {
                                metrics.tx_bytes.fetch_add(sent_n as u64, Ordering::Relaxed);
                            }
                        }
                    }
                } else if let Some(target) = sessions.single_tx() {
                    if let Ok(wire_len) = target.session.seal(&mut tun_buf, 0, MSG_KEEPALIVE) {
                        if let Ok(sent_n) = socket.send(&tun_buf[..wire_len]).await {
                            metrics.tx_bytes.fetch_add(sent_n as u64, Ordering::Relaxed);
                        }
                    }
                }
            }
            result = tun.read(&mut tun_buf[HEADER_LEN..]) => {
                let n = result?;
                let plaintext = &tun_buf[HEADER_LEN..HEADER_LEN + n];
                if validate_ipv4(plaintext, cfg.mtu as usize).is_err() {
                    metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                    continue;
                }
                let inner_dst = destination_ip(plaintext);
                let keyed = sessions.keyed();
                let mut keyed_endpoint = None;
                let payload_len = if keyed {
                    let target = if is_server {
                        sessions.tx_for_virtual_ip(inner_dst)
                    } else {
                        sessions.single_tx()
                    };
                    match target {
                        Some(target) => {
                            keyed_endpoint = target.endpoint;
                            match target.session.seal(&mut tun_buf, n, MSG_DATA) {
                                Ok(wire_len) => wire_len,
                                Err(CryptoError::NonceExhaustionNeedsRekey) => {
                                    metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                                    eprintln!("vpn-engine: key epoch {} exhausted, needs rekey", target.session.key_epoch());
                                    continue;
                                }
                                Err(err) => {
                                    metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                                    eprintln!("vpn-engine: cannot seal datagram: {err}");
                                    continue;
                                }
                            }
                        }
                        None => {
                            metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                            continue;
                        }
                    }
                } else {
                    HEADER_LEN + n
                };

                let sent = if is_server {
                    if keyed {
                        match keyed_endpoint {
                            Some(peer) => Some(socket.send_to(&tun_buf[..payload_len], peer).await),
                            None => None,
                        }
                    } else {
                        match learned_peer {
                            Some(peer) => Some(socket.send_to(&tun_buf[HEADER_LEN..payload_len], peer).await),
                            None => None,
                        }
                    }
                } else if keyed {
                    Some(socket.send(&tun_buf[..payload_len]).await)
                } else {
                    Some(socket.send(&tun_buf[HEADER_LEN..payload_len]).await)
                };
                match sent {
                    Some(Ok(sent_n)) => {
                        metrics.tx_pkts.fetch_add(1, Ordering::Relaxed);
                        metrics.tx_bytes.fetch_add(sent_n as u64, Ordering::Relaxed);
                        next_keepalive = tokio::time::Instant::now() + cfg.keepalive_interval;
                    }
                    Some(Err(err)) => {
                        metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                        eprintln!("vpn-engine: dropping outbound datagram: {err}");
                    }
                    None => {
                        metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                    }
                }
            }
            result = recv_datagram(&socket, &mut udp_buf) => {
                let (real_len, from) = match result {
                    Ok(v) => v,
                    Err(err) => {
                        eprintln!("vpn-engine: dropping inbound datagram: {err}");
                        continue;
                    }
                };
                metrics.rx_bytes.fetch_add(real_len as u64, Ordering::Relaxed);
                if real_len > udp_buf.len() {
                    metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                    continue;
                }

                let keyed = sessions.keyed();
                // Plaintext mode has no cryptographic peer identity, so it
                // keeps the milestone-2 fixed-endpoint prefilter. Keyed mode
                // first resolves sender_index, then enforces that peer's own
                // endpoint before spending AEAD work.
                if !keyed && is_server && learned_peer.is_some_and(|peer| peer != from) {
                    metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                    continue;
                }

                let (msg_type, packet, rx_peer) = if keyed {
                    if real_len < HEADER_LEN + crate::wire::TAG_LEN {
                        metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                        continue;
                    }
                    let sender_index = match frame::parse(&udp_buf[..HEADER_LEN]) {
                        Ok(header) => header.sender_index,
                        Err(_) => {
                            metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                            continue;
                        }
                    };
                    match sessions.rx_lookup(sender_index) {
                        None => {
                            metrics.rx_unknown_sender_index.fetch_add(1, Ordering::Relaxed);
                            metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                            continue;
                        }
                        Some(lookup) => {
                            if is_server && lookup.endpoint.is_some_and(|endpoint| endpoint != from) {
                                // Drop cross-endpoint copies before open():
                                // they must not burn the real peer's nonce.
                                metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                                continue;
                            }
                            let meta = (lookup.lab_session_id, lookup.virtual_ip, lookup.endpoint);
                            match lookup.session.open(&mut udp_buf, real_len) {
                                Ok((header, pt_len)) => (
                                    header.message_type,
                                    &udp_buf[HEADER_LEN..HEADER_LEN + pt_len],
                                    Some(meta),
                                ),
                                Err(_err) => {
                                    metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                                    continue;
                                }
                            }
                        }
                    }
                } else {
                    (MSG_DATA, &udp_buf[..real_len], None)
                };

                if msg_type == MSG_KEEPALIVE {
                    if !keyed && is_server && learned_peer != Some(from) {
                        metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                    }
                    continue;
                }

                if validate_ipv4(packet, cfg.mtu as usize).is_err() {
                    metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                    continue;
                }
                if is_server {
                    if keyed {
                        let Some((lab, peer_vip, _)) = rx_peer else {
                            metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                            continue;
                        };
                        let src = source_ip(packet);
                        let dst = destination_ip(packet);
                        if Some(src) != peer_vip {
                            metrics.invalid_inner_src.fetch_add(1, Ordering::Relaxed);
                            metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                            continue;
                        }
                        // MVP policy: no client-to-client hairpin. Responses
                        // originated behind the node may still use any source.
                        if sessions.is_peer_virtual_ip(dst) {
                            metrics.invalid_inner_dst.fetch_add(1, Ordering::Relaxed);
                            metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                            continue;
                        }
                        match sessions.learn_endpoint(lab, from) {
                            Some(endpoint) if endpoint == from => {
                                metrics.peers.store(sessions.endpoint_count() as u64, Ordering::Relaxed);
                            }
                            _ => {
                                metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                                continue;
                            }
                        }
                    } else {
                        let peer_vip = cfg.peer_vip.expect("plaintext server sets peer_vip");
                        match classify_server_packet(
                            learned_peer,
                            from,
                            source_ip(packet),
                            destination_ip(packet),
                            peer_vip,
                            cfg.tun_cidr.addr,
                        ) {
                            ServerInbound::Learn => {
                                learned_peer = Some(from);
                                metrics.peers.store(1, Ordering::Relaxed);
                                eprintln!("vpn-engine: learned peer endpoint {from}");
                            }
                            ServerInbound::Forward => {}
                            ServerInbound::Drop => {
                                metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                                if source_ip(packet) != peer_vip {
                                    metrics.invalid_inner_src.fetch_add(1, Ordering::Relaxed);
                                } else {
                                    metrics.invalid_inner_dst.fetch_add(1, Ordering::Relaxed);
                                }
                                continue;
                            }
                        }
                    }
                } else if destination_ip(packet) != cfg.tun_cidr.addr {
                    // The connected socket pins the outer node and AEAD
                    // authenticates it. Forwarded answers originate at the
                    // test service, not at the node VIP, so the client-side
                    // authorization invariant is the assigned destination.
                    metrics.invalid_inner_dst.fetch_add(1, Ordering::Relaxed);
                    metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                    continue;
                }
                tun.write(packet).await?;
                metrics.rx_pkts.fetch_add(1, Ordering::Relaxed);
            }
        }
    }

    metrics_task.abort();
    metrics::cleanup(&cfg.metrics_sock);
    if let Some(task) = control_task {
        task.abort();
        if let Some(path) = &cfg.control_sock {
            control::cleanup(path);
        }
    }
    eprintln!("vpn-engine: shutting down");
    Ok(())
}

#[cfg(test)]
fn test_sigterm() {
    // run_loop exits on SIGTERM; tokio's signal stream consumes the signal
    // instead of letting it kill the process.
    unsafe { libc::kill(libc::getpid(), libc::SIGTERM) };
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::wire::TAG_LEN;

    fn base() -> Config {
        Config {
            bind: "0.0.0.0:51820".parse().unwrap(),
            remote: None,
            peer_vip: Some(Ipv4Addr::new(10, 80, 1, 2)),
            tun_cidr: "10.80.1.1/24".parse().unwrap(),
            tun_name: "tun0".into(),
            mtu: 1400,
            metrics_sock: PathBuf::from("/tmp/test.sock"),
            metrics_group: None,
            control_sock: None,
            control_uid: None,
            key: None,
            tx_key: None,
            rx_key: None,
            sender_index: 1,
            peer_index: 0,
            keepalive_interval: Duration::from_secs(25),
        }
    }

    #[test]
    fn accepts_valid_server_config() {
        assert!(base().validate().is_ok());
    }

    #[test]
    fn rejects_zero_and_huge_mtu() {
        let mut cfg = base();
        cfg.mtu = 0;
        assert!(cfg.validate().is_err());
        cfg.mtu = MAX_MTU + 1;
        assert!(cfg.validate().is_err());
        cfg.mtu = MAX_MTU;
        assert!(cfg.validate().is_ok());
    }

    #[test]
    fn exact_key_config_rules() {
        let mut cfg = base();
        cfg.tx_key = Some(Zeroizing::new([1u8; 32]));
        cfg.rx_key = Some(Zeroizing::new([2u8; 32]));
        cfg.peer_index = 2;
        assert!(cfg.validate().is_ok());

        cfg.peer_index = 0;
        assert!(cfg.validate().is_err());
        cfg.peer_index = 2;
        cfg.key = Some(Zeroizing::new([3u8; 32]));
        assert!(cfg.validate().is_err());
        cfg.key = None;
        cfg.rx_key = Some(Zeroizing::new([1u8; 32]));
        assert!(cfg.validate().is_err());
        cfg.rx_key = Some(Zeroizing::new([2u8; 32]));
        cfg.mtu = MAX_MTU_ENCRYPTED + 1;
        assert!(cfg.validate().is_err());
    }

    #[test]
    fn rejects_peer_outside_subnet() {
        let mut cfg = base();
        cfg.peer_vip = Some(Ipv4Addr::new(10, 80, 2, 9));
        assert!(cfg.validate().is_err());
    }

    #[test]
    fn rejects_peer_equal_to_local_vip() {
        let mut cfg = base();
        cfg.peer_vip = Some(Ipv4Addr::new(10, 80, 1, 1));
        assert!(cfg.validate().is_err());
    }

    #[test]
    fn keyed_config_rules() {
        let mut cfg = base();
        cfg.key = Some(Zeroizing::new([1u8; 32]));
        // peer_index unset: required once --key is given.
        assert!(cfg.validate().is_err());
        cfg.peer_index = 2;
        assert!(cfg.validate().is_ok());
        cfg.sender_index = 0;
        assert!(cfg.validate().is_err());
        cfg.sender_index = 1;
        // Identical indexes would collapse both direction keys to one.
        cfg.peer_index = 1;
        assert!(cfg.validate().is_err());
        cfg.peer_index = 2;
        // A zero keepalive interval would starve the select loop.
        cfg.keepalive_interval = Duration::ZERO;
        assert!(cfg.validate().is_err());
        cfg.keepalive_interval = Duration::from_secs(25);
        // The outer datagram must still fit a 1500-byte path.
        cfg.mtu = MAX_MTU_ENCRYPTED + 1;
        assert!(cfg.validate().is_err());
        cfg.mtu = MAX_MTU_ENCRYPTED;
        assert!(cfg.validate().is_ok());
        // Without a key the fields are unused and the wider buffer limit
        // still applies.
        cfg.key = None;
        cfg.sender_index = 0;
        cfg.peer_index = 0;
        cfg.mtu = MAX_MTU;
        assert!(cfg.validate().is_ok());
    }

    #[test]
    fn keyed_mtu_limit_follows_outer_family() {
        let mut cfg = base();
        cfg.key = Some(Zeroizing::new([1u8; 32]));
        cfg.peer_index = 2;
        // IPv4 outer (58B overhead): 1442 passes, 1443 is rejected.
        cfg.mtu = MAX_MTU_ENCRYPTED;
        assert!(cfg.validate().is_ok());
        cfg.mtu = MAX_MTU_ENCRYPTED + 1;
        assert!(cfg.validate().is_err());
        // IPv6 outer (78B overhead): 1422 passes, 1423 is rejected.
        cfg.bind = "[::]:51820".parse().unwrap();
        cfg.mtu = MAX_MTU_ENCRYPTED_V6;
        assert!(cfg.validate().is_ok());
        cfg.mtu = MAX_MTU_ENCRYPTED_V6 + 1;
        assert!(cfg.validate().is_err());
    }

    #[test]
    fn client_rejects_mismatched_bind_and_remote_family() {
        let mut cfg = base();
        cfg.remote = Some("[::1]:51820".parse().unwrap());
        // v4 bind + v6 remote is unreachable — rejected at startup.
        assert!(cfg.validate().is_err());
        cfg.bind = "[::]:0".parse().unwrap();
        assert!(cfg.validate().is_ok());
    }

    #[test]
    fn client_requires_peer_vip() {
        let mut cfg = base();
        cfg.remote = Some("127.0.0.1:51820".parse().unwrap());
        cfg.peer_vip = None;
        assert!(cfg.validate().is_err());
        cfg.peer_vip = Some(Ipv4Addr::new(10, 80, 1, 2));
        assert!(cfg.validate().is_ok());
    }

    const PEER_VIP: Ipv4Addr = Ipv4Addr::new(10, 80, 1, 2);
    const LOCAL_VIP: Ipv4Addr = Ipv4Addr::new(10, 80, 1, 1);
    const OTHER_VIP: Ipv4Addr = Ipv4Addr::new(10, 80, 1, 99);
    const EP_A: SocketAddr =
        SocketAddr::V4(SocketAddrV4::new(Ipv4Addr::new(192, 168, 56, 12), 51820));
    const EP_B: SocketAddr =
        SocketAddr::V4(SocketAddrV4::new(Ipv4Addr::new(192, 168, 56, 77), 9999));

    #[test]
    fn first_valid_packet_with_peer_source_learns() {
        assert_eq!(
            classify_server_packet(None, EP_A, PEER_VIP, LOCAL_VIP, PEER_VIP, LOCAL_VIP),
            ServerInbound::Learn
        );
    }

    #[test]
    fn divergent_inner_source_is_dropped_not_learned() {
        assert_eq!(
            classify_server_packet(None, EP_A, OTHER_VIP, LOCAL_VIP, PEER_VIP, LOCAL_VIP),
            ServerInbound::Drop
        );
    }

    #[test]
    fn divergent_inner_destination_is_dropped_not_learned() {
        // Source matches the peer VIP but the packet is not addressed to this
        // node's virtual IP: it must not pin the endpoint.
        assert_eq!(
            classify_server_packet(None, EP_A, PEER_VIP, OTHER_VIP, PEER_VIP, LOCAL_VIP),
            ServerInbound::Drop
        );
    }

    #[test]
    fn learned_endpoint_forwards() {
        assert_eq!(
            classify_server_packet(Some(EP_A), EP_A, PEER_VIP, LOCAL_VIP, PEER_VIP, LOCAL_VIP),
            ServerInbound::Forward
        );
    }

    #[test]
    fn learned_endpoint_with_wrong_inner_source_is_dropped() {
        // A packet from the pinned endpoint but carrying a foreign virtual IP
        // must not be injected into the TUN: learning does not suspend source
        // validation.
        assert_eq!(
            classify_server_packet(Some(EP_A), EP_A, OTHER_VIP, LOCAL_VIP, PEER_VIP, LOCAL_VIP),
            ServerInbound::Drop
        );
    }

    #[test]
    fn learned_endpoint_with_wrong_inner_destination_is_dropped() {
        // The pinned endpoint sending to a foreign inner destination must not
        // reach the TUN either.
        assert_eq!(
            classify_server_packet(Some(EP_A), EP_A, PEER_VIP, OTHER_VIP, PEER_VIP, LOCAL_VIP),
            ServerInbound::Drop
        );
    }

    #[test]
    fn decodes_ipv6_source_from_dual_stack_socket() {
        // A `[::]` bind delivers AF_INET6 senders, including IPv4-mapped ones.
        let sa6 = libc::sockaddr_in6 {
            sin6_family: libc::AF_INET6 as libc::sa_family_t,
            sin6_port: 51820u16.to_be(),
            sin6_flowinfo: 0u32.to_be(),
            sin6_addr: libc::in6_addr {
                s6_addr: [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 172, 30, 0, 12],
            },
            sin6_scope_id: 0,
        };
        let mut storage: libc::sockaddr_storage = unsafe { std::mem::zeroed() };
        unsafe { std::ptr::write(&mut storage as *mut _ as *mut libc::sockaddr_in6, sa6) };
        let addr =
            sockaddr_to_std(&storage, std::mem::size_of::<libc::sockaddr_in6>() as _).unwrap();
        assert_eq!(
            addr,
            SocketAddr::V6(std::net::SocketAddrV6::new(
                std::net::Ipv6Addr::new(0, 0, 0, 0, 0, 0xffff, 0xac1e, 0x000c),
                51820,
                0,
                0,
            ))
        );
    }

    #[test]
    fn other_endpoint_after_pinning_is_dropped() {
        // Even with a spoofed matching inner source, a different outer
        // endpoint cannot take over the pinned peer.
        assert_eq!(
            classify_server_packet(Some(EP_A), EP_B, PEER_VIP, LOCAL_VIP, PEER_VIP, LOCAL_VIP),
            ServerInbound::Drop
        );
    }

    // ---- coverage tests: the pieces of run() that need no privileges ----

    use std::fs::File;
    use std::os::unix::io::{FromRawFd, IntoRawFd};
    use std::os::unix::net::UnixDatagram;
    use std::time::Duration;

    fn ipv4_packet(src: Ipv4Addr, dst: Ipv4Addr, payload: &[u8]) -> Vec<u8> {
        let mut p = vec![0u8; 20 + payload.len()];
        p[0] = 0x45;
        let total = p.len() as u16;
        p[2..4].copy_from_slice(&total.to_be_bytes());
        p[8] = 64;
        p[9] = 17;
        p[12..16].copy_from_slice(&src.octets());
        p[16..20].copy_from_slice(&dst.octets());
        p[20..].copy_from_slice(payload);
        let mut sum: u32 = 0;
        for i in (0..20).step_by(2) {
            sum += u16::from_be_bytes([p[i], p[i + 1]]) as u32;
        }
        while sum >> 16 != 0 {
            sum = (sum & 0xffff) + (sum >> 16);
        }
        p[10..12].copy_from_slice(&(!(sum as u16)).to_be_bytes());
        p
    }

    /// A datagram socketpair plays TUN: the engine end is a File, the test
    /// end is a UnixDatagram with a read timeout.
    fn fake_tun() -> (Tun, UnixDatagram) {
        let (a, b) = UnixDatagram::pair().unwrap();
        a.set_nonblocking(true).unwrap();
        b.set_read_timeout(Some(Duration::from_millis(300)))
            .unwrap();
        (
            Tun::from_file(unsafe { File::from_raw_fd(a.into_raw_fd()) }).unwrap(),
            b,
        )
    }

    fn sock_path(tag: &str) -> PathBuf {
        PathBuf::from(format!("/tmp/hkfwd-{}-{tag}.sock", std::process::id()))
    }

    fn free_port() -> u16 {
        std::net::UdpSocket::bind("127.0.0.1:0")
            .unwrap()
            .local_addr()
            .unwrap()
            .port()
    }

    fn cfg_for(
        bind: u16,
        remote: Option<SocketAddr>,
        peer_vip: Option<Ipv4Addr>,
        tag: &str,
    ) -> Config {
        Config {
            bind: SocketAddr::V4(SocketAddrV4::new(Ipv4Addr::LOCALHOST, bind)),
            remote,
            peer_vip,
            tun_cidr: "10.80.1.1/24".parse().unwrap(),
            tun_name: "hkcov0".into(),
            mtu: 1400,
            metrics_sock: sock_path(tag),
            metrics_group: None,
            control_sock: None,
            control_uid: None,
            key: None,
            tx_key: None,
            rx_key: None,
            sender_index: 1,
            peer_index: 0,
            keepalive_interval: Duration::from_secs(25),
        }
    }

    /// Keeps a SIGTERM consumer alive for the whole test so our self-sent
    /// signal can never hit the default disposition (which would kill the
    /// whole test process).
    fn sigterm_guard() -> tokio::signal::unix::Signal {
        signal(SignalKind::terminate()).unwrap()
    }

    #[tokio::test]
    async fn recv_datagram_reports_real_length_and_source() {
        let recv = UdpSocket::bind("127.0.0.1:0").await.unwrap();
        let addr = recv.local_addr().unwrap();
        let sender = UdpSocket::bind("127.0.0.1:0").await.unwrap();
        sender.send_to(b"hello", addr).await.unwrap();
        let mut buf = [0u8; 64];
        let (n, from) = recv_datagram(&recv, &mut buf).await.unwrap();
        assert_eq!(n, 5);
        assert_eq!(from, sender.local_addr().unwrap());

        // Longer than the buffer: MSG_TRUNC reports the real datagram size.
        sender.send_to(&vec![7u8; 300], addr).await.unwrap();
        let mut small = [0u8; 100];
        let (n2, _) = recv_datagram(&recv, &mut small).await.unwrap();
        assert_eq!(n2, 300);
    }

    #[test]
    fn sockaddr_to_std_decodes_ipv4_and_rejects_bad_inputs() {
        let sa = libc::sockaddr_in {
            sin_family: libc::AF_INET as _,
            sin_port: 1234u16.to_be(),
            sin_addr: libc::in_addr {
                s_addr: u32::from_ne_bytes([10, 1, 2, 3]),
            },
            sin_zero: [0; 8],
        };
        let mut storage: libc::sockaddr_storage = unsafe { std::mem::zeroed() };
        unsafe { std::ptr::write(&mut storage as *mut _ as *mut libc::sockaddr_in, sa) };
        let addr =
            sockaddr_to_std(&storage, std::mem::size_of::<libc::sockaddr_in>() as _).unwrap();
        assert_eq!(addr, "10.1.2.3:1234".parse().unwrap());

        // Short buffers are rejected for both families.
        assert!(sockaddr_to_std(&storage, 4).is_err());
        let sa6 = libc::sockaddr_in6 {
            sin6_family: libc::AF_INET6 as _,
            sin6_port: 1u16.to_be(),
            sin6_flowinfo: 0,
            sin6_addr: libc::in6_addr { s6_addr: [0; 16] },
            sin6_scope_id: 0,
        };
        unsafe { std::ptr::write(&mut storage as *mut _ as *mut libc::sockaddr_in6, sa6) };
        assert!(sockaddr_to_std(&storage, 4).is_err());

        // Unknown family is rejected outright.
        storage.ss_family = 0x99;
        let err = sockaddr_to_std(&storage, 64).unwrap_err();
        assert!(err.to_string().contains("unexpected address family"));
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn run_loop_client_and_server_scenarios() {
        // SIGTERM is process-wide, so all scenarios must live in one test:
        // the signal we send to stop one engine would otherwise reach an
        // engine still running in a parallel test and break it.
        let _sig = sigterm_guard();
        client_scenario().await;
        server_scenario().await;
        crypto_client_scenario().await;
        crypto_server_scenario().await;
        rotation_scenario().await;
        drop(_sig);
    }

    async fn client_scenario() {
        let (tun, peer) = fake_tun();
        let remote = UdpSocket::bind("127.0.0.1:0").await.unwrap();
        let remote_addr = remote.local_addr().unwrap();
        let bind = free_port();
        let cfg = cfg_for(bind, Some(remote_addr), Some(OTHER_VIP), "cli");
        let bind_addr = cfg.bind;
        let engine = tokio::spawn(run_loop(cfg, tun));

        // TUN -> UDP: a valid packet reaches the remote endpoint.
        peer.send(&ipv4_packet(LOCAL_VIP, OTHER_VIP, b"out"))
            .unwrap();
        let mut rb = [0u8; 64];
        let (n, _) = tokio::time::timeout(Duration::from_secs(2), remote.recv_from(&mut rb))
            .await
            .expect("remote receive timed out")
            .unwrap();
        assert_eq!(
            &rb[..n],
            ipv4_packet(LOCAL_VIP, OTHER_VIP, b"out").as_slice()
        );

        // UDP -> TUN: the engine's socket is connected to `remote`, so only
        // datagrams from that address are delivered — send from it. A packet
        // addressed to our virtual IP lands on TUN. The first send may race
        // the engine's bind, so retry briefly.
        let mut tb = [0u8; 64];
        for _ in 0..40 {
            remote
                .send_to(&ipv4_packet(OTHER_VIP, LOCAL_VIP, b"in"), bind_addr)
                .await
                .unwrap();
            if let Ok(n) = peer.recv(&mut tb) {
                assert_eq!(
                    &tb[..n],
                    ipv4_packet(OTHER_VIP, LOCAL_VIP, b"in").as_slice()
                );
                break;
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }

        // Drops: foreign inner destination, malformed frame, oversized
        // frame — none may reach TUN. Inner sources are intentionally not
        // pinned because forwarded test-service answers keep their own IP.
        remote
            .send_to(&ipv4_packet(OTHER_VIP, OTHER_VIP, b"zz"), bind_addr)
            .await
            .unwrap();
        remote.send_to(&[1, 2, 3, 4, 5], bind_addr).await.unwrap();
        remote.send_to(&vec![0u8; 2100], bind_addr).await.unwrap();
        assert!(peer.recv(&mut tb).is_err(), "dropped packet reached TUN");

        // TUN garbage never reaches the remote either (counts as dropped).
        peer.send(b"garbage").unwrap();

        test_sigterm();
        engine.await.unwrap().unwrap();
    }

    async fn server_scenario() {
        let (tun, peer) = fake_tun();
        let bind = free_port();
        let cfg = cfg_for(bind, None, Some(PEER_VIP), "srv");
        let bind_addr = cfg.bind;
        let engine = tokio::spawn(run_loop(cfg, tun));
        let good = UdpSocket::bind("127.0.0.1:0").await.unwrap();
        let stranger = UdpSocket::bind("127.0.0.1:0").await.unwrap();

        // Before learning, a TUN packet has nowhere to go (dropped).
        peer.send(&ipv4_packet(LOCAL_VIP, PEER_VIP, b"early"))
            .unwrap();

        // Learn: send until the loop is up and the datagram lands — the
        // first send may race the bind, so retry briefly.
        let mut tb = [0u8; 64];
        for _ in 0..40 {
            good.send_to(&ipv4_packet(PEER_VIP, LOCAL_VIP, b"hi"), bind_addr)
                .await
                .unwrap();
            if let Ok(n) = peer.recv(&mut tb) {
                assert_eq!(&tb[..n], ipv4_packet(PEER_VIP, LOCAL_VIP, b"hi").as_slice());
                break;
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }

        // A different endpoint can no longer inject, even with a matching
        // inner source. A bad inner source from the pinned endpoint is also
        // dropped, as are malformed and oversized datagrams.
        stranger
            .send_to(&ipv4_packet(PEER_VIP, LOCAL_VIP, b"!"), bind_addr)
            .await
            .unwrap();
        good.send_to(&ipv4_packet(OTHER_VIP, LOCAL_VIP, b"!"), bind_addr)
            .await
            .unwrap();
        good.send_to(&[9u8; 5], bind_addr).await.unwrap();
        good.send_to(&vec![0u8; 2100], bind_addr).await.unwrap();
        assert!(peer.recv(&mut tb).is_err(), "dropped packet reached TUN");

        // After learning, TUN -> UDP goes to the pinned endpoint.
        peer.send(&ipv4_packet(LOCAL_VIP, PEER_VIP, b"back"))
            .unwrap();
        let mut rb = [0u8; 64];
        let (n, _) = tokio::time::timeout(Duration::from_secs(2), good.recv_from(&mut rb))
            .await
            .expect("learned peer receive timed out")
            .unwrap();
        assert_eq!(
            &rb[..n],
            ipv4_packet(LOCAL_VIP, PEER_VIP, b"back").as_slice()
        );

        test_sigterm();
        engine.await.unwrap().unwrap();
    }

    const LAB_KEY: [u8; 32] = [0x42u8; 32];

    /// Client scenario with `--key`: outbound TUN traffic leaves as a sealed
    /// DATA datagram (1430B wire for 1400B inner max — here a small packet),
    /// inbound sealed traffic decrypts in place onto TUN, and a tampered
    /// datagram is dropped without reaching TUN. Runs inside the serialized
    /// SIGTERM test like the plaintext scenarios.
    async fn crypto_client_scenario() {
        let (tun, peer) = fake_tun();
        let remote = UdpSocket::bind("127.0.0.1:0").await.unwrap();
        let remote_addr = remote.local_addr().unwrap();
        let bind = free_port();
        let mut cfg = cfg_for(bind, Some(remote_addr), Some(OTHER_VIP), "crypt");
        cfg.key = Some(Zeroizing::new(LAB_KEY));
        cfg.peer_index = 2;
        cfg.keepalive_interval = Duration::from_millis(60);
        let bind_addr = cfg.bind;
        let engine = tokio::spawn(run_loop(cfg, tun));

        // TUN -> UDP: the wire datagram carries the 14-byte cleartext
        // header, ciphertext and the 16-byte tag — never plaintext.
        let inner = ipv4_packet(LOCAL_VIP, OTHER_VIP, b"out");
        peer.send(&inner).unwrap();
        let mut rb = [0u8; 256];
        let (n, _) = tokio::time::timeout(Duration::from_secs(2), remote.recv_from(&mut rb))
            .await
            .expect("remote receive timed out")
            .unwrap();
        assert_eq!(n, inner.len() + TOTAL_OVERHEAD);
        assert_eq!(rb[0], (crate::wire::frame::VERSION << 4) | MSG_DATA);
        assert_eq!(rb[1], 0);
        assert_ne!(&rb[..n], inner.as_slice());

        // The remote seals a reply and it lands decrypted on TUN. Its
        // session uses complementary indexes: it stamps 2 and expects 1.
        let peer_session = SessionState::from_lab_psk(Zeroizing::new(LAB_KEY), 2, 1);
        let reply = ipv4_packet(OTHER_VIP, LOCAL_VIP, b"in");
        let mut seal_buf = vec![0u8; HEADER_LEN + reply.len() + TAG_LEN + 32];
        seal_buf[HEADER_LEN..HEADER_LEN + reply.len()].copy_from_slice(&reply);
        let wire_len = peer_session
            .seal(&mut seal_buf, reply.len(), MSG_DATA)
            .unwrap();
        let mut tb = [0u8; 256];
        let mut landed = false;
        for _ in 0..40 {
            remote
                .send_to(&seal_buf[..wire_len], bind_addr)
                .await
                .unwrap();
            if let Ok(n) = peer.recv(&mut tb) {
                assert_eq!(&tb[..n], reply.as_slice());
                landed = true;
                break;
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
        assert!(landed, "sealed inbound packet never reached TUN");

        // A forged datagram (one flipped ciphertext bit) is dropped — it can
        // neither authenticate nor claim the replay slot.
        let mut forged = seal_buf[..wire_len].to_vec();
        forged[HEADER_LEN] ^= 0x40;
        remote.send_to(&forged, bind_addr).await.unwrap();
        // Re-sending the same valid datagram is a replay: also dropped.
        remote
            .send_to(&seal_buf[..wire_len], bind_addr)
            .await
            .unwrap();
        assert!(
            peer.recv(&mut tb).is_err(),
            "forged or replayed packet reached TUN"
        );

        // Forwarded application traffic is allowed to keep the service's
        // inner source address. The client authorizes the destination VIP;
        // it does not require the source to be the node VIP.
        let service_reply = ipv4_packet(PEER_VIP, LOCAL_VIP, b"svc");
        let mut svc_buf = vec![0u8; HEADER_LEN + service_reply.len() + TAG_LEN + 32];
        svc_buf[HEADER_LEN..HEADER_LEN + service_reply.len()].copy_from_slice(&service_reply);
        let svc_len = peer_session
            .seal(&mut svc_buf, service_reply.len(), MSG_DATA)
            .unwrap();
        let mut svc_landed = false;
        for _ in 0..40 {
            remote
                .send_to(&svc_buf[..svc_len], bind_addr)
                .await
                .unwrap();
            if let Ok(n) = peer.recv(&mut tb) {
                assert_eq!(&tb[..n], service_reply.as_slice());
                svc_landed = true;
                break;
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
        assert!(svc_landed, "forwarded service reply never reached TUN");

        // Idle for a beat: the engine emits a sealed KEEPALIVE (header + tag,
        // no payload) without any TUN traffic.
        let mut kb = [0u8; 64];
        let (kn, _) = tokio::time::timeout(Duration::from_secs(2), remote.recv_from(&mut kb))
            .await
            .expect("keepalive timed out")
            .unwrap();
        assert_eq!(kn, HEADER_LEN + TAG_LEN);
        assert_eq!(kb[0], (crate::wire::frame::VERSION << 4) | MSG_KEEPALIVE);

        // An inbound sealed KEEPALIVE is accepted but never reaches TUN.
        let mut ka_buf = vec![0u8; HEADER_LEN + TAG_LEN + 16];
        let ka_len = peer_session.seal(&mut ka_buf, 0, MSG_KEEPALIVE).unwrap();
        remote.send_to(&ka_buf[..ka_len], bind_addr).await.unwrap();
        assert!(peer.recv(&mut tb).is_err(), "keepalive reached TUN");

        test_sigterm();
        engine.await.unwrap().unwrap();
    }

    /// Keyed server: learns the peer from an authenticated datagram,
    /// drops a cross-endpoint replay before decrypting it (so the real
    /// copy still lands), and emits sealed KEEPALIVEs while idle.
    async fn crypto_server_scenario() {
        let (tun, peer) = fake_tun();
        let bind = free_port();
        let mut cfg = cfg_for(bind, None, Some(PEER_VIP), "csrv");
        cfg.key = Some(Zeroizing::new(LAB_KEY));
        cfg.sender_index = 2;
        cfg.peer_index = 1;
        cfg.keepalive_interval = Duration::from_millis(60);
        let bind_addr = cfg.bind;
        let engine = tokio::spawn(run_loop(cfg, tun));
        let good = UdpSocket::bind("127.0.0.1:0").await.unwrap();
        let stranger = UdpSocket::bind("127.0.0.1:0").await.unwrap();
        // The lab peer stamps index 1 and expects 2.
        let peer_session = SessionState::from_lab_psk(Zeroizing::new(LAB_KEY), 1, 2);

        // Learn: an authenticated datagram carrying the peer's inner VIP
        // pins the endpoint and forwards to TUN.
        let hello = ipv4_packet(PEER_VIP, LOCAL_VIP, b"hi");
        let mut seal_buf = vec![0u8; HEADER_LEN + hello.len() + TAG_LEN + 32];
        seal_buf[HEADER_LEN..HEADER_LEN + hello.len()].copy_from_slice(&hello);
        let wire_len = peer_session
            .seal(&mut seal_buf, hello.len(), MSG_DATA)
            .unwrap();
        let mut tb = [0u8; 256];
        let mut landed = false;
        for _ in 0..40 {
            good.send_to(&seal_buf[..wire_len], bind_addr)
                .await
                .unwrap();
            if let Ok(n) = peer.recv(&mut tb) {
                assert_eq!(&tb[..n], hello.as_slice());
                landed = true;
                break;
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
        assert!(landed, "sealed learn packet never reached TUN");

        // Endpoint pinning runs before decryption: a captured, still
        // unseen datagram replayed by a different endpoint is dropped
        // without burning its nonce, so the real copy from the pinned
        // endpoint still authenticates and lands on TUN.
        let fresh = ipv4_packet(PEER_VIP, LOCAL_VIP, b"second");
        let mut captured = vec![0u8; HEADER_LEN + fresh.len() + TAG_LEN + 32];
        captured[HEADER_LEN..HEADER_LEN + fresh.len()].copy_from_slice(&fresh);
        let cap_len = peer_session
            .seal(&mut captured, fresh.len(), MSG_DATA)
            .unwrap();
        stranger
            .send_to(&captured[..cap_len], bind_addr)
            .await
            .unwrap();
        tokio::time::sleep(Duration::from_millis(30)).await;
        let mut landed = false;
        for _ in 0..40 {
            good.send_to(&captured[..cap_len], bind_addr).await.unwrap();
            if let Ok(n) = peer.recv(&mut tb) {
                assert_eq!(&tb[..n], fresh.as_slice());
                landed = true;
                break;
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
        assert!(landed, "cross-endpoint replay poisoned the replay window");

        // Idle server with a learned peer emits sealed keepalives.
        let mut kb = [0u8; 64];
        let (kn, from) = tokio::time::timeout(Duration::from_secs(2), good.recv_from(&mut kb))
            .await
            .expect("server keepalive timed out")
            .unwrap();
        assert_eq!(from, bind_addr);
        assert_eq!(kn, HEADER_LEN + TAG_LEN);
        assert_eq!(kb[0], (crate::wire::frame::VERSION << 4) | MSG_KEEPALIVE);

        test_sigterm();
        engine.await.unwrap().unwrap();
    }

    /// One control-socket round trip: write the request frame, read the
    /// response frame.
    async fn control_request(
        path: &std::path::Path,
        op: &str,
        payload: serde_json::Value,
    ) -> serde_json::Value {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};
        let mut c = tokio::net::UnixStream::connect(path).await.unwrap();
        let req = serde_json::json!({
            "request_id": "t", "version": 1, "op": op, "payload": payload,
        })
        .to_string();
        c.write_u32(req.len() as u32).await.unwrap();
        c.write_all(req.as_bytes()).await.unwrap();
        let n = c.read_u32().await.unwrap() as usize;
        let mut buf = vec![0u8; n];
        c.read_exact(&mut buf).await.unwrap();
        serde_json::from_slice(&buf).unwrap()
    }

    fn hex32(byte: u8) -> String {
        format!("{byte:02x}").repeat(32)
    }

    /// Zero-drop rotation: a keyed client engine is rotated Stage ->
    /// Activate -> Retire through the control socket while the peer keeps
    /// sending under both epochs. Staged and retiring epochs decrypt
    /// alongside the active one; only after Retire does the old index
    /// become unknown.
    async fn rotation_scenario() {
        let (tun, peer) = fake_tun();
        let remote = UdpSocket::bind("127.0.0.1:0").await.unwrap();
        let remote_addr = remote.local_addr().unwrap();
        let bind = free_port();
        let control_path = sock_path("ctrl");
        let mut cfg = cfg_for(bind, Some(remote_addr), Some(OTHER_VIP), "rot");
        cfg.key = Some(Zeroizing::new(LAB_KEY));
        cfg.peer_index = 2;
        cfg.control_sock = Some(control_path.clone());
        let bind_addr = cfg.bind;
        let engine = tokio::spawn(run_loop(cfg, tun));

        // Epoch 0 peer: stamps 2, expects 1, derived lab keys.
        let peer0 = SessionState::from_lab_psk(Zeroizing::new(LAB_KEY), 2, 1);
        // Epoch 1 material comes over the socket: the peer's session holds
        // the mirrored keys — its tx is our ingress, its rx our egress.
        let egress = Zeroizing::new([0xA5; 32]);
        let ingress = Zeroizing::new([0x5A; 32]);
        let peer1 = SessionState::new(
            1,
            Zeroizing::new(*ingress), // peer seals with our ingress key
            Zeroizing::new(*egress),  // and opens our egress traffic
            22,                       // peer stamps its own index
            11,                       // and expects our new one
        );

        let send_sealed = |session: &SessionState, inner: &[u8]| {
            let mut buf = vec![0u8; HEADER_LEN + inner.len() + TAG_LEN + 32];
            buf[HEADER_LEN..HEADER_LEN + inner.len()].copy_from_slice(inner);
            let n = session.seal(&mut buf, inner.len(), MSG_DATA).unwrap();
            buf[..n].to_vec()
        };
        let mut tb = [0u8; 256];

        // Baseline: epoch-0 datagram lands.
        let pkt0 = ipv4_packet(OTHER_VIP, LOCAL_VIP, b"epoch0");
        let wire0 = send_sealed(&peer0, &pkt0);
        let mut landed = false;
        for _ in 0..40 {
            remote.send_to(&wire0, bind_addr).await.unwrap();
            if let Ok(n) = peer.recv(&mut tb) {
                assert_eq!(&tb[..n], pkt0.as_slice());
                landed = true;
                break;
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
        assert!(landed, "epoch-0 packet never reached TUN");

        // StagePeer: the new epoch already decrypts before Activate —
        // early datagrams under peer_index 22 land on TUN.
        let resp = control_request(
            &control_path,
            "StagePeer",
            serde_json::json!({
                "lab_session_id": 7,
                "key_epoch": 1,
                "sender_index": 11,
                "peer_index": 22,
                "egress_key": hex32(0xA5),
                "ingress_key": hex32(0x5A),
            }),
        )
        .await;
        assert_eq!(resp["status"], "ok");
        let pkt1 = ipv4_packet(OTHER_VIP, LOCAL_VIP, b"staged-epoch1");
        let wire1 = send_sealed(&peer1, &pkt1);
        let mut landed = false;
        for _ in 0..40 {
            remote.send_to(&wire1, bind_addr).await.unwrap();
            if let Ok(n) = peer.recv(&mut tb) {
                assert_eq!(&tb[..n], pkt1.as_slice());
                landed = true;
                break;
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
        assert!(landed, "staged-epoch packet never reached TUN");

        // Activate: outbound traffic flips to sender_index 11.
        let resp = control_request(
            &control_path,
            "Activate",
            serde_json::json!({"key_epoch": 1}),
        )
        .await;
        assert_eq!(resp["status"], "ok");
        peer.send(&ipv4_packet(LOCAL_VIP, OTHER_VIP, b"out1"))
            .unwrap();
        let mut rb = [0u8; 256];
        let (_, _) = tokio::time::timeout(Duration::from_secs(2), remote.recv_from(&mut rb))
            .await
            .expect("post-activate datagram timed out")
            .unwrap();
        assert_eq!(&rb[2..6], &11u32.to_be_bytes());

        // Retiring epoch 0 still decrypts during the drain window.
        let pkt0b = ipv4_packet(OTHER_VIP, LOCAL_VIP, b"epoch0-late");
        let wire0b = send_sealed(&peer0, &pkt0b);
        let mut landed = false;
        for _ in 0..40 {
            remote.send_to(&wire0b, bind_addr).await.unwrap();
            if let Ok(n) = peer.recv(&mut tb) {
                assert_eq!(&tb[..n], pkt0b.as_slice());
                landed = true;
                break;
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
        assert!(landed, "retiring-epoch packet dropped during drain");

        // Retire ends the window: the same peer can no longer reach epoch 0.
        let resp =
            control_request(&control_path, "Retire", serde_json::json!({"key_epoch": 0})).await;
        assert_eq!(resp["status"], "ok");
        let pkt0c = ipv4_packet(OTHER_VIP, LOCAL_VIP, b"epoch0-dead");
        let wire0c = send_sealed(&peer0, &pkt0c);
        remote.send_to(&wire0c, bind_addr).await.unwrap();
        assert!(
            peer.recv(&mut tb).is_err(),
            "retired-epoch packet reached TUN"
        );

        // GetSessions reports one active epoch with the observed counters.
        let resp = control_request(&control_path, "GetSessions", serde_json::json!({})).await;
        let epochs = resp["result"]["epochs"].as_array().unwrap();
        assert_eq!(epochs.len(), 1);
        assert_eq!(epochs[0]["key_epoch"], 1);
        assert_eq!(epochs[0]["role"], "active");
        assert_eq!(epochs[0]["rx_pkts"], 1);
        assert_eq!(epochs[0]["tx_pkts"], 1);

        test_sigterm();
        engine.await.unwrap().unwrap();
    }

    /// A metrics socket that cannot bind aborts the engine with a clear
    /// error — no half-running forwarder. The loop exits before the signal
    /// registration, so no SIGTERM interplay with the serialized test.
    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn run_loop_fails_when_metrics_socket_cannot_bind() {
        let (tun, _peer) = fake_tun();
        let bad = PathBuf::from(format!("/tmp/hkfwd-{}-bad.sock", std::process::id()));
        std::fs::write(&bad, b"occupied").unwrap(); // a file, not a socket
        let mut cfg = cfg_for(free_port(), None, Some(PEER_VIP), "badm");
        cfg.metrics_sock = bad.clone();
        let err = run_loop(cfg, tun).await.unwrap_err();
        assert!(err.to_string().contains("metrics"));
        std::fs::remove_file(&bad).ok();
    }
}
