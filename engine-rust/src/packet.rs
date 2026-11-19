//! Structural validation for raw IPv4 packets.
//!
//! Phase 1 carries exactly one raw IPv4 packet per TUN read and per UDP
//! datagram, so validation is strict: the header must be well-formed and the
//! declared total length must match the buffer length exactly.

use std::net::Ipv4Addr;

pub const MIN_IPV4_HEADER_LEN: usize = 20;
const IPV4_VERSION: u8 = 4;
const IPV4_SOURCE_OFFSET: usize = 12;
const IPV4_DESTINATION_OFFSET: usize = 16;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PacketError {
    /// The buffer is too large for the configured inner MTU.
    Oversized,
    /// Fewer bytes than the minimum IPv4 header, or the header length
    /// exceeds the buffer.
    Truncated,
    /// The version nibble is not 4.
    NotIpv4,
    /// The IHL field is smaller than 5 (20 bytes).
    BadHeaderLength,
    /// The total length field is smaller than the header or does not
    /// match the buffer length.
    BadTotalLength,
    /// The IPv4 header checksum does not verify.
    BadChecksum,
}

/// Validates the IPv4 structure of `packet`. `max_len` bounds the accepted
/// size to the configured inner MTU.
pub fn validate_ipv4(packet: &[u8], max_len: usize) -> Result<(), PacketError> {
    if packet.len() > max_len {
        return Err(PacketError::Oversized);
    }
    if packet.len() < MIN_IPV4_HEADER_LEN {
        return Err(PacketError::Truncated);
    }
    if packet[0] >> 4 != IPV4_VERSION {
        return Err(PacketError::NotIpv4);
    }
    let ihl = (packet[0] & 0x0f) as usize;
    if ihl < 5 {
        return Err(PacketError::BadHeaderLength);
    }
    let header_len = ihl * 4;
    if header_len > packet.len() {
        return Err(PacketError::Truncated);
    }
    let total_len = u16::from_be_bytes([packet[2], packet[3]]) as usize;
    if total_len < header_len || total_len != packet.len() {
        return Err(PacketError::BadTotalLength);
    }
    if !header_checksum_ok(&packet[..header_len]) {
        return Err(PacketError::BadChecksum);
    }
    Ok(())
}

/// Returns the source address of a packet that passed [`validate_ipv4`].
pub fn source_ip(packet: &[u8]) -> Ipv4Addr {
    Ipv4Addr::new(
        packet[IPV4_SOURCE_OFFSET],
        packet[IPV4_SOURCE_OFFSET + 1],
        packet[IPV4_SOURCE_OFFSET + 2],
        packet[IPV4_SOURCE_OFFSET + 3],
    )
}

/// Returns the destination address of a packet that passed [`validate_ipv4`].
pub fn destination_ip(packet: &[u8]) -> Ipv4Addr {
    Ipv4Addr::new(
        packet[IPV4_DESTINATION_OFFSET],
        packet[IPV4_DESTINATION_OFFSET + 1],
        packet[IPV4_DESTINATION_OFFSET + 2],
        packet[IPV4_DESTINATION_OFFSET + 3],
    )
}

/// The one's-complement sum of a valid IPv4 header (checksum field included)
/// folds to all ones.
fn header_checksum_ok(header: &[u8]) -> bool {
    let mut sum: u32 = 0;
    for word in header.as_chunks::<2>().0 {
        sum += u16::from_be_bytes(*word) as u32;
    }
    while sum >> 16 != 0 {
        sum = (sum & 0xffff) + (sum >> 16);
    }
    sum == 0xffff
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A minimal valid IPv4 header plus an 8-byte payload, checksum included.
    fn valid_packet() -> Vec<u8> {
        let mut p = vec![
            0x45, 0x00, 0x00, 0x1c, // version/IHL, DSCP, total length 28
            0x00, 0x00, 0x00, 0x00, // id, flags/fragment
            0x40, 0x01, 0x00, 0x00, // ttl, proto icmp, checksum (filled below)
            10, 80, 1, 1, // src
            10, 80, 1, 2, // dst
        ];
        p.extend_from_slice(&[8, 0, 0xf7, 0xff, 0, 0, 0, 0]); // payload
        let sum = compute_checksum(&p[..20]);
        p[10] = (sum >> 8) as u8;
        p[11] = (sum & 0xff) as u8;
        p
    }

    fn compute_checksum(header: &[u8]) -> u16 {
        let mut sum: u32 = 0;
        for word in header.as_chunks::<2>().0 {
            sum += u16::from_be_bytes(*word) as u32;
        }
        while sum >> 16 != 0 {
            sum = (sum & 0xffff) + (sum >> 16);
        }
        !(sum as u16)
    }

    const MTU: usize = 1400;

    #[test]
    fn accepts_valid_packet() {
        assert_eq!(validate_ipv4(&valid_packet(), MTU), Ok(()));
    }

    #[test]
    fn rejects_oversized() {
        let p = vec![0u8; MTU + 1];
        assert_eq!(validate_ipv4(&p, MTU), Err(PacketError::Oversized));
    }

    #[test]
    fn rejects_shorter_than_header() {
        assert_eq!(
            validate_ipv4(&valid_packet()[..10], MTU),
            Err(PacketError::Truncated)
        );
    }

    #[test]
    fn rejects_non_ipv4() {
        let mut p = valid_packet();
        p[0] = 0x65; // version 6
                     // fix checksum so only the version check can fail
        p[10] = 0;
        p[11] = 0;
        let sum = compute_checksum(&p[..20]);
        p[10] = (sum >> 8) as u8;
        p[11] = (sum & 0xff) as u8;
        assert_eq!(validate_ipv4(&p, MTU), Err(PacketError::NotIpv4));
    }

    #[test]
    fn rejects_ihl_below_five() {
        let mut p = valid_packet();
        p[0] = 0x44;
        p[10] = 0;
        p[11] = 0;
        let sum = compute_checksum(&p[..20]);
        p[10] = (sum >> 8) as u8;
        p[11] = (sum & 0xff) as u8;
        assert_eq!(validate_ipv4(&p, MTU), Err(PacketError::BadHeaderLength));
    }

    #[test]
    fn rejects_truncated_by_ihl() {
        // IHL claims 24 bytes but the packet is exactly 20.
        let mut p = valid_packet()[..20].to_vec();
        p[0] = 0x46;
        p[2] = 0;
        p[3] = 20;
        p[10] = 0;
        p[11] = 0;
        let sum = compute_checksum(&p[..20]);
        p[10] = (sum >> 8) as u8;
        p[11] = (sum & 0xff) as u8;
        assert_eq!(validate_ipv4(&p, MTU), Err(PacketError::Truncated));
    }

    #[test]
    fn rejects_total_length_mismatch() {
        let mut p = valid_packet();
        p[3] = 0x20; // declares 32, buffer is 28
        p[10] = 0;
        p[11] = 0;
        let sum = compute_checksum(&p[..20]);
        p[10] = (sum >> 8) as u8;
        p[11] = (sum & 0xff) as u8;
        assert_eq!(validate_ipv4(&p, MTU), Err(PacketError::BadTotalLength));
    }

    #[test]
    fn rejects_bad_checksum() {
        let mut p = valid_packet();
        p[11] ^= 0xff; // corrupt checksum field only
        assert_eq!(validate_ipv4(&p, MTU), Err(PacketError::BadChecksum));
    }

    #[test]
    fn extracts_source_ip() {
        assert_eq!(source_ip(&valid_packet()), Ipv4Addr::new(10, 80, 1, 1));
    }

    #[test]
    fn extracts_destination_ip() {
        assert_eq!(destination_ip(&valid_packet()), Ipv4Addr::new(10, 80, 1, 2));
    }

    #[test]
    fn accepts_header_with_options() {
        // IHL 6: 24-byte header, options word zeroed, total length 32.
        let mut p = valid_packet();
        p[0] = 0x46;
        p.splice(20..20, [0u8; 4]);
        p[2] = 0;
        p[3] = 32;
        p[10] = 0;
        p[11] = 0;
        let sum = compute_checksum(&p[..24]);
        p[10] = (sum >> 8) as u8;
        p[11] = (sum & 0xff) as u8;
        assert_eq!(validate_ipv4(&p, MTU), Ok(()));
    }
}
