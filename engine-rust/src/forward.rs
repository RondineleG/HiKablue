//! The Phase 1 forwarding loop: raw IPv4 between a TUN device and UDP.
//!
//! Two 2048-byte buffers are allocated once and reused for every packet, so
//! the hot path performs no per-packet heap allocation. `MAX_MTU` bounds the
//! configurable inner MTU to what those buffers can carry.

use std::io;
use std::net::{Ipv4Addr, SocketAddr, SocketAddrV4};
use std::os::unix::io::AsRawFd;
use std::path::PathBuf;
use std::sync::atomic::Ordering;

use tokio::io::Interest;
use tokio::net::UdpSocket;
use tokio::signal::unix::{signal, SignalKind};

use crate::cli::Ipv4Cidr;
use crate::metrics::{self, Metrics};
use crate::packet::{destination_ip, source_ip, validate_ipv4};
use crate::tun::{configure_interface, Tun};

/// Fixed reusable buffer size; also the largest supported inner MTU.
pub const BUFFER_SIZE: usize = 2048;
pub const MAX_MTU: u16 = BUFFER_SIZE as u16;

#[derive(Debug)]
pub struct Config {
    pub bind: SocketAddr,
    /// Client only: the fixed remote endpoint.
    pub remote: Option<SocketAddr>,
    /// Server only: the virtual IP the learned peer must present.
    pub peer_vip: Option<Ipv4Addr>,
    pub tun_cidr: Ipv4Cidr,
    pub tun_name: String,
    pub mtu: u16,
    /// Unix socket that answers GetMetrics.
    pub metrics_sock: PathBuf,
    pub metrics_group: Option<String>,
}

impl Config {
    fn validate(&self) -> io::Result<()> {
        if self.mtu == 0 || self.mtu > MAX_MTU {
            return Err(invalid(format!(
                "mtu {} outside 1..={MAX_MTU} (fixed {BUFFER_SIZE}-byte buffers)",
                self.mtu
            )));
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
        Ok(())
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
pub async fn run(cfg: Config) -> io::Result<()> {
    cfg.validate()?;
    let is_server = cfg.peer_vip.is_some();

    let tun = Tun::open(&cfg.tun_name)?;
    configure_interface(&cfg.tun_name, &cfg.tun_cidr.to_string(), cfg.mtu)?;
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

    // Telemetry lives beside the dataplane: the forwarding loop only touches
    // relaxed atomics, and the Unix listener is a separate task that never
    // blocks the packet path.
    let metrics = Metrics::new();
    // Bind eagerly so a socket failure (path in use, bad group, permissions)
    // fails the engine with a clear error instead of leaving a dead task
    // that nobody observes.
    let metrics_listener = metrics::bind(&cfg.metrics_sock, cfg.metrics_group.as_deref())?;
    let metrics_task = tokio::spawn(metrics::serve(metrics.clone(), metrics_listener));
    if !is_server {
        // The client connects to its configured remote: it has one peer
        // from the start, no learning needed.
        metrics.peers.store(1, Ordering::Relaxed);
    }

    let mut learned_peer: Option<SocketAddr> = None;
    let mut tun_buf = [0u8; BUFFER_SIZE];
    let mut udp_buf = [0u8; BUFFER_SIZE];
    let mut sigterm = signal(SignalKind::terminate())?;

    loop {
        tokio::select! {
            biased;
            result = tokio::signal::ctrl_c() => {
                result?;
                break;
            }
            _ = sigterm.recv() => {
                break;
            }
            result = tun.read(&mut tun_buf) => {
                let n = result?;
                if validate_ipv4(&tun_buf[..n], cfg.mtu as usize).is_err() {
                    metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                    continue;
                }
                // A failed send for one datagram (e.g. ECONNREFUSED while the
                // peer is down, or EMSGSIZE) must not kill the tunnel.
                let sent = if is_server {
                    match learned_peer {
                        Some(peer) => Some(socket.send_to(&tun_buf[..n], peer).await),
                        None => None,
                    }
                } else {
                    Some(socket.send(&tun_buf[..n]).await)
                };
                match sent {
                    Some(Ok(sent_n)) => {
                        metrics.tx_pkts.fetch_add(1, Ordering::Relaxed);
                        metrics.tx_bytes.fetch_add(sent_n as u64, Ordering::Relaxed);
                    }
                    Some(Err(err)) => {
                        metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                        eprintln!("vpn-engine: dropping outbound datagram: {err}");
                    }
                    None => {
                        // Server before learning the peer: the packet has
                        // nowhere to go — count it as dropped.
                        metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                    }
                }
            }
            result = recv_datagram(&socket, &mut udp_buf) => {
                // Per-datagram receive errors (e.g. a queued ICMP
                // port-unreachable on a connected socket) are not fatal
                // either.
                let (real_len, from) = match result {
                    Ok(v) => v,
                    Err(err) => {
                        eprintln!("vpn-engine: dropping inbound datagram: {err}");
                        continue;
                    }
                };
                // Count every received datagram — even the ones validation
                // drops next — so a 5-byte datagram moves rx_bytes by five.
                metrics.rx_bytes.fetch_add(real_len as u64, Ordering::Relaxed);
                if real_len > udp_buf.len() {
                    // Truncated by the buffer: not a complete packet.
                    metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                    continue;
                }
                let packet = &udp_buf[..real_len];
                if validate_ipv4(packet, cfg.mtu as usize).is_err() {
                    metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                    continue;
                }
                if is_server {
                    let peer_vip = cfg.peer_vip.expect("server sets peer_vip");
                    match classify_server_packet(
                        learned_peer,
                        from,
                        source_ip(packet),
                        destination_ip(packet),
                        peer_vip,
                        cfg.tun_cidr.addr,
                    ) {
                        // Learn the endpoint only after structural validation
                        // and an inner source-IP match. This pins the peer but
                        // is not authentication: any sender that can reach this
                        // UDP port and spoof the peer's virtual IP would pass.
                        ServerInbound::Learn => {
                            learned_peer = Some(from);
                            metrics.peers.store(1, Ordering::Relaxed);
                            eprintln!("vpn-engine: learned peer endpoint {from}");
                        }
                        ServerInbound::Forward => {}
                        ServerInbound::Drop => {
                            metrics.dropped_pkts.fetch_add(1, Ordering::Relaxed);
                            continue;
                        }
                    }
                } else if destination_ip(packet) != cfg.tun_cidr.addr {
                    // Client: the connected socket already pins the remote,
                    // but the peer must not inject packets addressed to
                    // other tunnel IPs.
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
    eprintln!("vpn-engine: shutting down");
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

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
}
