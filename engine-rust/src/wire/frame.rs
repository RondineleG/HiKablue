//! Wire framing: the 14-byte cleartext prefix every datagram carries
//! (vpn-debian-specification.md, wire layout version 1):
//!
//!   u8  version | type  (high nibble = version 1; low nibble:
//!                        0x01 DATA, 0x02 KEEPALIVE — anything else
//!                        is dropped)
//!   u8  reserved        (0x00)
//!   u32 session_id      (node-local session identifier, big-endian)
//!   u64 counter         (strictly monotonic send counter, big-endian)
//!
//! The header doubles as the AEAD associated data, so tampering with
//! type, session_id or counter fails tag verification.

use std::io;

pub const HEADER_LEN: usize = 14;
pub const TAG_LEN: usize = 16;
/// Header + tag: what encryption adds on top of the inner packet.
pub const TOTAL_OVERHEAD: usize = HEADER_LEN + TAG_LEN;

/// Wire protocol version, carried in the header's high nibble.
pub const VERSION: u8 = 1;

pub const MSG_DATA: u8 = 0x01;
pub const MSG_KEEPALIVE: u8 = 0x02;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Header {
    /// Low nibble of byte 0: `MSG_DATA` or `MSG_KEEPALIVE`.
    pub message_type: u8,
    /// The spec's node-local session ID; the engine names it for the
    /// peer session index it is bound to.
    pub sender_index: u32,
    /// Per-direction counter — the AEAD nonce source.
    pub nonce: u64,
}

/// Serializes `header` into `buf[..HEADER_LEN]`.
pub fn encode(header: &Header, buf: &mut [u8]) {
    buf[0] = (VERSION << 4) | (header.message_type & 0x0F);
    buf[1] = 0; // reserved
    buf[2..6].copy_from_slice(&header.sender_index.to_be_bytes());
    buf[6..14].copy_from_slice(&header.nonce.to_be_bytes());
}

/// Parses the cleartext prefix of a received datagram.
pub fn parse(buf: &[u8]) -> io::Result<Header> {
    if buf.len() < HEADER_LEN {
        return Err(invalid("datagram shorter than wire header".into()));
    }
    let version = buf[0] >> 4;
    if version != VERSION {
        return Err(invalid(format!("unsupported wire version {version}")));
    }
    if buf[1] != 0 {
        return Err(invalid("nonzero reserved byte in wire header".into()));
    }
    let message_type = buf[0] & 0x0F;
    if message_type != MSG_DATA && message_type != MSG_KEEPALIVE {
        return Err(invalid(format!("unknown message type {message_type:#04x}")));
    }
    Ok(Header {
        message_type,
        sender_index: u32::from_be_bytes(buf[2..6].try_into().unwrap()),
        nonce: u64::from_be_bytes(buf[6..14].try_into().unwrap()),
    })
}

/// The 12-byte AEAD IV: four zero bytes followed by the 8-byte
/// little-endian counter (RFC 8439 nonce layout).
pub fn aead_nonce(counter: u64) -> [u8; 12] {
    let mut iv = [0u8; 12];
    iv[4..].copy_from_slice(&counter.to_le_bytes());
    iv
}

fn invalid(msg: String) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, msg)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn encode_parse_roundtrip() {
        let header = Header {
            message_type: MSG_DATA,
            sender_index: 0xAABBCCDD,
            nonce: 0x0102030405060708,
        };
        let mut buf = [0u8; HEADER_LEN];
        encode(&header, &mut buf);
        assert_eq!(buf[0], (VERSION << 4) | MSG_DATA);
        assert_eq!(buf[1], 0);
        assert_eq!(&buf[2..6], &0xAABBCCDDu32.to_be_bytes());
        assert_eq!(&buf[6..14], &0x0102030405060708u64.to_be_bytes());
        assert_eq!(parse(&buf).unwrap(), header);
    }

    #[test]
    fn parse_rejects_bad_inputs() {
        assert!(parse(&[0u8; 5]).is_err()); // short
        let mut buf = [0u8; HEADER_LEN];
        buf[1] = 1; // reserved byte set
        assert!(parse(&buf).is_err());
        buf[1] = 0;
        buf[0] = (VERSION << 4) | 0x0F; // unknown type
        assert!(parse(&buf).is_err());
        buf[0] = (VERSION << 4) | MSG_KEEPALIVE; // the other valid type parses
        assert!(parse(&buf).is_ok());
        buf[0] = 0x21; // version 2: rejected before anything else
        assert!(parse(&buf).is_err());
    }

    #[test]
    fn aead_nonce_maps_counter_to_last_eight_bytes() {
        let iv = aead_nonce(0x0102030405060708);
        assert_eq!(&iv[..4], &[0, 0, 0, 0]);
        assert_eq!(&iv[4..], &0x0102030405060708u64.to_le_bytes());
    }

    #[test]
    fn overhead_constants_match_spec() {
        // 1400-byte inner packet -> exactly 1430 bytes of UDP payload.
        assert_eq!(HEADER_LEN + TAG_LEN, TOTAL_OVERHEAD);
        assert_eq!(1400 + TOTAL_OVERHEAD, 1430);
    }
}
