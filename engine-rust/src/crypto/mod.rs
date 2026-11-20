//! Data-plane cryptography: session keys, AEAD sealing and replay defense.

pub mod replay;
pub mod session;

pub use session::{CryptoError, SessionState};
