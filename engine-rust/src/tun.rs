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
        if name.len() >= libc::IFNAMSIZ {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                format!("interface name {name:?} exceeds IFNAMSIZ"),
            ));
        }
        // Refuse to reuse an interface that already exists: TUNSETIFF could
        // attach to a persistent TUN owned by another VPN and we would
        // reconfigure it (LAB-02: touch only project-owned objects). Transient
        // interfaces die with their descriptor, so any surviving `name` is
        // not ours.
        if std::path::Path::new(&format!("/sys/class/net/{name}")).exists() {
            return Err(io::Error::new(
                io::ErrorKind::AlreadyExists,
                format!(
                    "interface {name:?} already exists (foreign or persistent TUN); \
                     pick another --tun-name or remove it"
                ),
            ));
        }
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
            req.ifr_flags = IFF_TUN | IFF_NO_PI;
            if libc::ioctl(fd, TUNSETIFF, &req as *const Ifreq) < 0 {
                return Err(io::Error::last_os_error());
            }
        }
        Ok(Tun {
            // SAFETY: `file` is a freshly opened, owned descriptor that stays
            // alive inside the AsyncFd for its whole registration.
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
pub fn configure_interface(ifname: &str, cidr: &str, mtu: u16) -> io::Result<()> {
    ip(&["link", "set", "dev", ifname, "mtu", &mtu.to_string()])?;
    ip(&["-4", "addr", "replace", cidr, "dev", ifname])?;
    ip(&["link", "set", "dev", ifname, "up"])?;
    Ok(())
}

fn ip(args: &[&str]) -> io::Result<()> {
    let output = Command::new("ip").args(args).output()?;
    if !output.status.success() {
        return Err(io::Error::new(
            io::ErrorKind::Other,
            format!(
                "ip {} failed: {}",
                args.join(" "),
                String::from_utf8_lossy(&output.stderr).trim()
            ),
        ));
    }
    Ok(())
}
