//! Command-line interface and configuration for `vpn-engine`.

use std::net::Ipv4Addr;
use std::path::Path;
use std::str::FromStr;

use clap::{Args, Parser, Subcommand};
use zeroize::Zeroizing;

pub const DEFAULT_MTU: u16 = 1400;
pub const DEFAULT_TUN_NAME: &str = "tun0";
pub const DEFAULT_METRICS_SOCK: &str = "/run/vpn/engine.sock";

#[derive(Parser)]
#[command(
    name = "vpn-engine",
    version,
    about = "Raw IPv4 TUN <-> UDP forwarder for the isolated HiKablue lab"
)]
pub struct Cli {
    #[command(subcommand)]
    pub mode: Mode,
}

#[derive(Subcommand)]
pub enum Mode {
    /// Listen on a UDP bind address and learn the single lab peer from the
    /// first structurally valid packet whose inner source IP matches --peer.
    Server(ServerArgs),
    /// Forward all tunnel traffic to one explicit remote UDP endpoint.
    Client(ClientArgs),
}

#[derive(Args)]
pub struct ServerArgs {
    /// Local UDP address to listen on, e.g. 192.168.56.11:51820.
    #[arg(long)]
    pub bind: std::net::SocketAddr,
    /// Virtual IPv4 address and prefix for the TUN interface, e.g. 10.80.1.1/24.
    #[arg(long)]
    pub tun_cidr: Ipv4Cidr,
    /// Virtual IPv4 address of the single allowed lab peer, e.g. 10.80.1.2.
    /// Required for plaintext static-key server mode; omit it when the
    /// control socket provisions peers dynamically.
    #[arg(long)]
    pub peer: Option<Ipv4Addr>,
    #[command(flatten)]
    pub common: CommonArgs,
}

#[derive(Args)]
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
    /// Virtual IPv4 address of the server inside the tunnel, e.g. 10.80.1.1.
    /// The client requires every accepted inner destination to be its own
    /// assigned tunnel address; cryptographic authentication alone does not
    /// authorize inner IPs.
    #[arg(long)]
    pub peer: Ipv4Addr,
    #[command(flatten)]
    pub common: CommonArgs,
}

#[derive(Args)]
pub struct CommonArgs {
    /// Inner MTU applied to the TUN interface. Capped at 1442 over IPv4
    /// or 1422 over IPv6 when --key is set (1500-byte path minus outer
    /// overhead), 2018 otherwise (the 2048-byte buffer carries 30 bytes
    /// of wire overhead).
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
    /// Unix socket that accepts key-lifecycle ops (StagePeer, Activate,
    /// Retire, RevokePeer) from the node agent, same u32-BE length + JSON
    /// envelope as the metrics socket. Carries key material, so it runs
    /// mode 0600 with a SO_PEERCRED check — separate from telemetry.
    /// Without this flag the control socket is not opened.
    #[arg(long)]
    pub control_sock: Option<std::path::PathBuf>,
    /// UID of the node agent allowed to connect to the control socket.
    /// Root always passes the peer-credential check. Requires --control-sock.
    #[arg(long)]
    pub control_uid: Option<u32>,
    /// Group that owns the control socket file with mode 0660. Use this
    /// for unprivileged service deployments: membership grants connect
    /// permission while --control-uid remains the identity check. Without
    /// it, --control-uid preserves the legacy root-oriented chown behavior.
    #[arg(long)]
    pub control_group: Option<String>,
    /// 32-byte ChaCha20-Poly1305 session key as 64 hex chars. Legacy lab
    /// option: the value is visible in the process command line; prefer
    /// --key-file. Without either, the engine speaks the M2 plaintext format.
    #[arg(long, conflicts_with = "key_file")]
    pub key: Option<String>,
    /// Read --key from a private regular file (mode 0600 or stricter).
    /// The file path, not the key, appears in the process command line.
    #[arg(long)]
    pub key_file: Option<std::path::PathBuf>,
    /// Session index stamped on outbound packets; also selects this
    /// side's derived direction key. Defaults to 1.
    #[arg(long, default_value_t = 1)]
    pub sender_index: u32,
    /// Session index the peer stamps on its packets. Required when --key
    /// is set and must differ from --sender-index: the two indexes pick
    /// the two direction keys.
    #[arg(long, default_value_t = 0)]
    pub peer_index: u32,
    /// Exact 32-byte transmit key as hex for lab client bootstrap. Legacy
    /// option: visible in the process command line; prefer --tx-key-file.
    /// Mutually exclusive with --key; unlike --key it is not HKDF-derived.
    #[arg(long, conflicts_with = "tx_key_file")]
    pub tx_key: Option<String>,
    /// Read the exact transmit key from a private regular file.
    #[arg(long)]
    pub tx_key_file: Option<std::path::PathBuf>,
    /// Exact 32-byte receive key as hex for lab client bootstrap. Legacy
    /// option: visible in the process command line; prefer --rx-key-file.
    #[arg(long, conflicts_with = "rx_key_file")]
    pub rx_key: Option<String>,
    /// Read the exact receive key from a private regular file.
    #[arg(long)]
    pub rx_key_file: Option<std::path::PathBuf>,
    /// Idle seconds before a KEEPALIVE datagram is sent (encrypted mode
    /// only). Defaults to 25.
    #[arg(long, default_value_t = 25)]
    pub keepalive_secs: u64,
}

fn hex_val(b: u8) -> Option<u8> {
    match b {
        b'0'..=b'9' => Some(b - b'0'),
        b'a'..=b'f' => Some(b - b'a' + 10),
        b'A'..=b'F' => Some(b - b'A' + 10),
        _ => None,
    }
}

/// Parses a 64-hex-char string into a 32-byte key. Works on bytes so a
/// multibyte UTF-8 char is a parse error, never a slice panic.
pub fn parse_key_hex(s: &str) -> Result<Zeroizing<[u8; 32]>, String> {
    let s = s.trim();
    let bytes = s.as_bytes();
    if bytes.len() != 64 {
        return Err(format!("key must be 64 hex chars, got {}", s.len()));
    }
    let mut key = Zeroizing::new([0u8; 32]);
    for (i, pair) in bytes.as_chunks::<2>().0.iter().enumerate() {
        let hi = hex_val(pair[0]);
        let lo = hex_val(pair[1]);
        key[i] = match (hi, lo) {
            (Some(h), Some(l)) => (h << 4) | l,
            _ => return Err(format!("key has a non-hex byte at offset {}", i * 2)),
        };
    }
    Ok(key)
}

/// Reads a hex key from a regular file whose permissions deny group/other
/// access. Error text includes the flag/path only — never file contents.
pub fn parse_key_file(path: &Path, flag: &str) -> Result<Zeroizing<[u8; 32]>, String> {
    let metadata = std::fs::metadata(path)
        .map_err(|err| format!("{flag}: cannot stat {}: {err}", path.display()))?;
    if !metadata.is_file() {
        return Err(format!("{flag}: {} is not a regular file", path.display()));
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        if metadata.permissions().mode() & 0o077 != 0 {
            return Err(format!(
                "{flag}: {} must not be readable by group/other",
                path.display()
            ));
        }
    }
    let raw = std::fs::read(path)
        .map_err(|err| format!("{flag}: cannot read {}: {err}", path.display()))?;
    let text = String::from_utf8(raw)
        .map_err(|_| format!("{flag}: {} is not UTF-8 hex", path.display()))?;
    parse_key_hex(&text).map_err(|err| format!("{flag}: {err}"))
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
    fn parse_key_hex_accepts_64_hex_chars() {
        let key = parse_key_hex("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff")
            .unwrap();
        assert_eq!(key[0], 0x00);
        assert_eq!(key[1], 0x11);
        assert_eq!(key[31], 0xff);
    }

    #[test]
    fn parse_key_hex_rejects_wrong_length_and_non_hex() {
        assert!(parse_key_hex("abcd").is_err());
        assert!(parse_key_hex(&"00".repeat(33)).is_err());
        assert!(parse_key_hex(&format!("{}zz", "00".repeat(31))).is_err());
        // Surrounding whitespace is trimmed before the length check.
        assert!(parse_key_hex(&format!("  {}  ", "ab".repeat(32))).is_ok());
        // Multibyte UTF-8 inside a 64-byte string is a parse error — the
        // parser works on bytes and never splits a char boundary.
        let mut unicode = "00".repeat(31);
        unicode.push('é'); // 2 bytes: brings the byte length to 64
        assert_eq!(unicode.len(), 64);
        assert!(parse_key_hex(&unicode).is_err());
    }

    #[test]
    fn parse_key_file_reads_private_hex_file() {
        let dir =
            std::env::temp_dir().join(format!("vpn-engine-key-test-{}-good", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("key.hex");
        std::fs::write(&path, format!("{}\n", "ab".repeat(32))).unwrap();
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o600)).unwrap();
        }
        let key = parse_key_file(&path, "--tx-key-file").unwrap();
        assert_eq!(key[0], 0xab);
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn parse_key_file_rejects_group_or_other_access() {
        use std::os::unix::fs::PermissionsExt;
        let dir = std::env::temp_dir().join(format!(
            "vpn-engine-key-test-{}-bad-mode",
            std::process::id()
        ));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("key.hex");
        std::fs::write(&path, "ab".repeat(32)).unwrap();
        std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o640)).unwrap();
        let err = parse_key_file(&path, "--tx-key-file").unwrap_err();
        assert!(err.contains("must not be readable"));
        std::fs::remove_dir_all(&dir).unwrap();
    }

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
