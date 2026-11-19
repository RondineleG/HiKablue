//! Linux TUN device access (`IFF_TUN | IFF_NO_PI`) and interface setup.
//!
//! Opening `/dev/net/tun` and configuring the interface require `CAP_NET_ADMIN`
//! (or root). The descriptor is non-blocking and wrapped in `AsyncFd` so reads
//! and writes participate in `tokio::select!` and remain cancellable.
//! Closing the descriptor on shutdown destroys the transient interface.

use std::fs::{File, OpenOptions};
use std::io::{self, Read, Write};
use std::os::unix::io::AsRawFd;
use std::process::Command;

use tokio::io::unix::AsyncFd;

const IFF_TUN: libc::c_short = 0x0001;
const IFF_NO_PI: libc::c_short = 0x1000;
const IFF_TUN_EXCL: libc::c_short = 0x8000u16.cast_signed();
const TUNSETIFF: libc::c_ulong = 0x400454ca;

#[repr(C)]
struct Ifreq {
    ifr_name: [libc::c_char; libc::IFNAMSIZ],
    ifr_flags: libc::c_short,
    // struct ifreq is 40 bytes; the union member we set is the flags short.
    _pad: [u8; 40 - libc::IFNAMSIZ - 2],
}

/// A non-blocking TUN device ready for `tokio::select!` loops.
pub struct Tun {
    file: AsyncFd<File>,
}

impl Tun {
    /// Creates the TUN interface `name` in layer-3 mode without packet
    /// information (`IFF_TUN | IFF_NO_PI`): reads and writes are raw IP
    /// datagrams.
    pub fn open(name: &str) -> io::Result<Tun> {
        if name.is_empty()
            || name.len() >= libc::IFNAMSIZ
            || !name
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'_' | b'-'))
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                format!("interface name {name:?} invalid: 1-15 chars of [A-Za-z0-9._-]"),
            ));
        }
        // Refuse to reuse an interface that already exists: TUNSETIFF could
        // attach to a persistent TUN owned by another VPN and we would
        // reconfigure it (LAB-02: touch only project-owned objects). Transient
        // interfaces die with their descriptor, so any surviving `name` is
        // not ours. The sysfs check gives the friendly error; IFF_TUN_EXCL
        // below makes the kernel refuse atomically, closing the TOCTOU window
        // between this check and the ioctl.
        if std::path::Path::new(&format!("/sys/class/net/{name}")).exists() {
            return Err(io::Error::new(
                io::ErrorKind::AlreadyExists,
                format!(
                    "interface {name:?} already exists (foreign or persistent TUN); \
                     pick another --tun-name or remove it"
                ),
            ));
        }
        // coverage: lab-only begin — real /dev/net/tun open + TUNSETIFF ioctl
        // need CAP_NET_ADMIN; exercised by the lab steps, not unit tests.
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .open("/dev/net/tun")?;
        let fd = file.as_raw_fd();
        unsafe {
            let flags = libc::fcntl(fd, libc::F_GETFL);
            if flags < 0 || libc::fcntl(fd, libc::F_SETFL, flags | libc::O_NONBLOCK) < 0 {
                return Err(io::Error::last_os_error());
            }
            let mut req: Ifreq = std::mem::zeroed();
            for (slot, byte) in req.ifr_name.iter_mut().zip(name.bytes()) {
                *slot = byte as libc::c_char;
            }
            req.ifr_flags = IFF_TUN | IFF_NO_PI | IFF_TUN_EXCL;
            if libc::ioctl(fd, TUNSETIFF, &req as *const Ifreq) < 0 {
                let err = io::Error::last_os_error();
                if err.raw_os_error() == Some(libc::EBUSY) {
                    return Err(io::Error::new(
                        io::ErrorKind::AlreadyExists,
                        format!(
                            "interface {name:?} already exists (foreign or persistent \
                             TUN); pick another --tun-name or remove it"
                        ),
                    ));
                }
                return Err(err);
            }
        }
        Tun::from_file(file)
        // coverage: lab-only end
    }

    /// Wraps an already-open descriptor in the async TUN interface. Tests
    /// hand it a socketpair end so the forwarding loop runs without a real
    /// device.
    pub fn from_file(file: File) -> io::Result<Tun> {
        Ok(Tun {
            // SAFETY: `file` is an owned descriptor that stays alive inside
            // the AsyncFd for its whole registration.
            file: unsafe { AsyncFd::register(file) }.map_err(io::Error::from)?,
        })
    }

    /// Reads one IP packet into `buf`, returning its length.
    pub async fn read(&self, buf: &mut [u8]) -> io::Result<usize> {
        loop {
            let mut guard = self.file.readable().await?;
            match guard.try_io(|inner| {
                let mut file = inner.get_ref();
                file.read(buf)
            }) {
                Ok(result) => return result,
                Err(_would_block) => continue,
            }
        }
    }

    /// Writes one IP packet from `buf`.
    pub async fn write(&self, buf: &[u8]) -> io::Result<usize> {
        loop {
            let mut guard = self.file.writable().await?;
            match guard.try_io(|inner| {
                let mut file = inner.get_ref();
                file.write(buf)
            }) {
                Ok(result) => return result,
                Err(_would_block) => continue,
            }
        }
    }
}

/// Applies MTU, IPv4 address and the up state to `ifname` using `ip(8)`.
/// `addr replace` keeps the call idempotent when the engine restarts.
// coverage: lab-only begin — needs a real interface + CAP_NET_ADMIN.
pub fn configure_interface(ifname: &str, cidr: &str, mtu: u16) -> io::Result<()> {
    ip(&["link", "set", "dev", ifname, "mtu", &mtu.to_string()])?;
    ip(&["-4", "addr", "replace", cidr, "dev", ifname])?;
    ip(&["link", "set", "dev", ifname, "up"])?;
    Ok(())
}
// coverage: lab-only end

/// Candidate absolute locations for iproute2's `ip`. Resolving it ourselves
/// keeps a root-run engine immune to a hostile PATH.
const IP_CANDIDATES: &[&str] = &["/sbin/ip", "/usr/sbin/ip", "/bin/ip", "/usr/bin/ip"];

fn ip_binary() -> io::Result<&'static str> {
    IP_CANDIDATES
        .iter()
        .copied()
        .find(|p| std::path::Path::new(p).is_file())
        .ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::NotFound,
                "iproute2 `ip` not found under /sbin, /usr/sbin, /bin or /usr/bin",
            )
        })
}

fn ip(args: &[&str]) -> io::Result<()> {
    // A harmless `ip -V` exercises the success path without privileges; a
    // bogus device exercises the error path. Both are unit-tested.
    let output = Command::new(ip_binary()?).args(args).output()?;
    if !output.status.success() {
        return Err(io::Error::other(format!(
            "ip {} failed: {}",
            args.join(" "),
            String::from_utf8_lossy(&output.stderr).trim()
        )));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::os::unix::io::FromRawFd;

    #[test]
    fn open_rejects_invalid_names() {
        for name in ["", "this-name-is-far-too-long", "bad name!", "na/me"] {
            let err = Tun::open(name).map(|_| ()).unwrap_err();
            assert_eq!(err.kind(), io::ErrorKind::InvalidInput, "name {name:?}");
        }
    }

    #[test]
    fn open_rejects_interface_that_already_exists() {
        // "lo" always exists; the sysfs check must refuse before /dev/net/tun
        // is touched — no privileges required to observe the refusal.
        let err = Tun::open("lo").map(|_| ()).unwrap_err();
        assert_eq!(err.kind(), io::ErrorKind::AlreadyExists);
    }

    fn socketpair_tun() -> (Tun, File) {
        let mut fds = [0; 2];
        unsafe {
            assert_eq!(
                libc::socketpair(
                    libc::AF_UNIX,
                    libc::SOCK_DGRAM | libc::SOCK_NONBLOCK,
                    0,
                    fds.as_mut_ptr()
                ),
                0
            );
            (
                Tun::from_file(File::from_raw_fd(fds[0])).unwrap(),
                File::from_raw_fd(fds[1]),
            )
        }
    }

    #[tokio::test]
    async fn read_write_roundtrip_over_socketpair() {
        let (tun, mut peer) = socketpair_tun();
        peer.write_all(b"packet").unwrap();
        let mut buf = [0u8; 16];
        let n = tun.read(&mut buf).await.unwrap();
        assert_eq!(&buf[..n], b"packet");
        let written = tun.write(b"reply").await.unwrap();
        assert_eq!(written, 5);
        let mut back = [0u8; 16];
        let n = peer.read(&mut back).unwrap();
        assert_eq!(&back[..n], b"reply");
    }

    #[test]
    fn ip_binary_finds_iproute2() {
        assert!(ip_binary().is_ok());
    }

    #[test]
    fn ip_succeeds_on_harmless_query_and_fails_on_bogus_device() {
        assert!(ip(&["-V"]).is_ok());
        let err = ip(&["link", "set", "dev", "hkcov-no-such-dev", "up"]).unwrap_err();
        assert!(err
            .to_string()
            .contains("ip link set dev hkcov-no-such-dev up"));
    }
}
