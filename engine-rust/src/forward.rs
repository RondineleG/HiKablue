//! The Phase 1 forwarding loop: raw IPv4 between a TUN device and UDP.
//!
//! Two 2048-byte buffers are allocated once and reused for every packet, so
//! the hot path performs no per-packet heap allocation. `MAX_MTU` bounds the
//! configurable inner MTU to what those buffers can carry.

use std::io;
use std::net::{Ipv4Addr, SocketAddr};

use tokio::net::UdpSocket;
use tokio::signal::unix::{signal, SignalKind};

use crate::cli::Ipv4Cidr;
use crate::packet::{source_ip, validate_ipv4};
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
        "vpngine {role}: {} mtu={} bind={}",
        cfg.tun_cidr,
        cfg.mtu,
        socket.local_addr()?,
    );

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
                    continue;
                }
                if is_server {
                    if let Some(peer) = learned_peer {
                        socket.send_to(&tun_buf[..n], peer).await?;
                    }
                } else {
                    socket.send(&tun_buf[..n]).await?;
                }
            }
            result = async {
                if is_server {
                    socket.recv_from(&mut udp_buf).await
                } else {
                    // The connected socket already filters to the remote
                    // endpoint; report it as the source for uniformity.
                    socket.recv(&mut udp_buf).await.map(|n| {
                        (n, cfg.remote.expect("client mode sets remote"))
                    })
                }
            } => {
                let (n, from) = result?;
                let packet = &udp_buf[..n];
                if validate_ipv4(packet, cfg.mtu as usize).is_err() {
                    continue;
                }
                if is_server {
                    match learned_peer {
                        // Learn the endpoint only after structural validation
                        // and an inner source-IP match. This pins the peer but
                        // is not authentication: any sender that can reach this
                        // UDP port and spoof the peer's virtual IP would pass.
                        None if source_ip(packet) == cfg.peer_vip.expect("server sets peer_vip") => {
                            learned_peer = Some(from);
                            eprintln!("vpngine: learned peer endpoint {from}");
                        }
                        None => continue,
                        Some(peer) if peer != from => continue,
                        _ => {}
                    }
                }
                tun.write(packet).await?;
            }
        }
    }

    eprintln!("vpngine: shutting down");
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
}
