//! Command-line interface and configuration for `vpn-engine`.

use std::net::Ipv4Addr;
use std::str::FromStr;

use clap::{Args, Parser, Subcommand};

pub const DEFAULT_MTU: u16 = 1400;
pub const DEFAULT_TUN_NAME: &str = "tun0";
pub const DEFAULT_METRICS_SOCK: &str = "/run/vpn/engine.sock";

#[derive(Debug, Parser)]
#[command(
    name = "vpn-engine",
    version,
    about = "Raw IPv4 TUN <-> UDP forwarder for the isolated HiKablue lab"
)]
pub struct Cli {
    #[command(subcommand)]
    pub mode: Mode,
}

#[derive(Debug, Subcommand)]
pub enum Mode {
    /// Listen on a UDP bind address and learn the single lab peer from the
    /// first structurally valid packet whose inner source IP matches --peer.
    Server(ServerArgs),
    /// Forward all tunnel traffic to one explicit remote UDP endpoint.
    Client(ClientArgs),
}

#[derive(Debug, Args)]
pub struct ServerArgs {
    /// Local UDP address to listen on, e.g. 192.168.56.11:51820.
    #[arg(long)]
    pub bind: std::net::SocketAddr,
    /// Virtual IPv4 address and prefix for the TUN interface, e.g. 10.80.1.1/24.
    #[arg(long)]
    pub tun_cidr: Ipv4Cidr,
    /// Virtual IPv4 address of the single allowed lab peer, e.g. 10.80.1.2.
    #[arg(long)]
    pub peer: Ipv4Addr,
    #[command(flatten)]
    pub common: CommonArgs,
}

#[derive(Debug, Args)]
pub struct ClientArgs {
    /// Local UDP address to bind, e.g. 0.0.0.0:0.
    #[arg(long, default_value = "0.0.0.0:0")]
    pub bind: std::net::SocketAddr,
    /// UDP endpoint of the server node, e.g. 192.168.56.11:51820.
    #[arg(long)]
    pub remote: std::net::SocketAddr,
    /// Virtual IPv4 address and prefix for the TUN interface, e.g. 10.80.1.2/24.
    #[arg(long)]
    pub tun_cidr: Ipv4Cidr,
    #[command(flatten)]
    pub common: CommonArgs,
}

#[derive(Debug, Args)]
pub struct CommonArgs {
    /// Inner MTU applied to the TUN interface (max 2048).
    #[arg(long, default_value_t = DEFAULT_MTU)]
    pub mtu: u16,
    /// Name of the TUN interface to create.
    #[arg(long, default_value = DEFAULT_TUN_NAME)]
    pub tun_name: String,
    /// Unix socket that answers GetMetrics (u32-BE length + JSON frame).
    #[arg(long, default_value = DEFAULT_METRICS_SOCK)]
    pub metrics_sock: std::path::PathBuf,
    /// Group that owns the metrics socket file (mode is 0660); without it
    /// the socket is limited to the engine's own user/group.
    #[arg(long)]
    pub metrics_group: Option<String>,
}

/// An IPv4 address with prefix length, written `A.B.C.D/N`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Ipv4Cidr {
    pub addr: Ipv4Addr,
    pub prefix: u8,
}

impl Ipv4Cidr {
    /// True when `ip` belongs to this network. A /32 matches only its own
    /// address; a /0 matches everything.
    pub fn contains(&self, ip: Ipv4Addr) -> bool {
        if self.prefix == 0 {
            return true;
        }
        let shift = 32 - self.prefix;
        (u32::from(self.addr) >> shift) == (u32::from(ip) >> shift)
    }
}

impl FromStr for Ipv4Cidr {
    type Err = String;

    fn from_str(s: &str) -> Result<Self, Self::Err> {
        let (addr, prefix) = s
            .split_once('/')
            .ok_or_else(|| format!("expected A.B.C.D/N, got {s:?}"))?;
        let addr: Ipv4Addr = addr
            .parse()
            .map_err(|_| format!("bad IPv4 address in {s:?}"))?;
        let prefix: u8 = prefix
            .parse()
            .map_err(|_| format!("bad prefix length in {s:?}"))?;
        if prefix > 32 {
            return Err(format!("prefix length {prefix} exceeds 32 in {s:?}"));
        }
        Ok(Ipv4Cidr { addr, prefix })
    }
}

impl std::fmt::Display for Ipv4Cidr {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}/{}", self.addr, self.prefix)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_cidr() {
        let c: Ipv4Cidr = "10.80.1.1/24".parse().unwrap();
        assert_eq!(c.addr, Ipv4Addr::new(10, 80, 1, 1));
        assert_eq!(c.prefix, 24);
        assert_eq!(c.to_string(), "10.80.1.1/24");
    }

    #[test]
    fn rejects_bad_cidr() {
        assert!("10.80.1.1".parse::<Ipv4Cidr>().is_err());
        assert!("10.80.1.1/33".parse::<Ipv4Cidr>().is_err());
        assert!("10.80.1.x/24".parse::<Ipv4Cidr>().is_err());
    }

    #[test]
    fn cidr_zero_prefix_matches_everything() {
        let net: Ipv4Cidr = "0.0.0.0/0".parse().unwrap();
        assert!(net.contains(Ipv4Addr::new(1, 2, 3, 4)));
        assert!(net.contains(Ipv4Addr::LOCALHOST));
    }

    #[test]
    fn cidr_contains() {
        let c: Ipv4Cidr = "10.80.1.1/24".parse().unwrap();
        assert!(c.contains(Ipv4Addr::new(10, 80, 1, 2)));
        assert!(c.contains(Ipv4Addr::new(10, 80, 1, 255)));
        assert!(!c.contains(Ipv4Addr::new(10, 80, 2, 1)));
    }

    #[test]
    fn host_cidr_matches_only_itself() {
        let c: Ipv4Cidr = "10.80.1.1/32".parse().unwrap();
        assert!(c.contains(Ipv4Addr::new(10, 80, 1, 1)));
        assert!(!c.contains(Ipv4Addr::new(10, 80, 1, 2)));
    }

    #[test]
    fn peer_must_be_inside_tunnel_subnet() {
        // The server's --peer is matched against the inner source IP, so a
        // peer outside the TUN subnet can never be learned.
        let c: Ipv4Cidr = "10.80.1.1/24".parse().unwrap();
        assert!(c.contains(Ipv4Addr::new(10, 80, 1, 2)));
        assert!(!c.contains(Ipv4Addr::new(192, 168, 56, 12)));
    }
}
