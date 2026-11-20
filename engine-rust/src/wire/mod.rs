//! Wire protocol: framing constants and zero-copy packet helpers.

pub mod frame;

pub use frame::{Header, HEADER_LEN, MSG_DATA, MSG_KEEPALIVE, TAG_LEN, TOTAL_OVERHEAD};
