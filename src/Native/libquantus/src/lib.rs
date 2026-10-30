//! Quantus QPoW hash for Miningcore.
//!
//! Mirrors `qpow_math::get_nonce_hash` from Quantus-Network/chain:
//!   input  = mining_hash (32 bytes) || nonce (64 bytes, big-endian U512)
//!   output = qp_poseidon_core::hash_squeeze_twice(input)  (64 bytes)
//! The output is interpreted as a big-endian U512. A share / block is valid
//! when hash < U512::MAX / difficulty (strict less-than).

use std::slice;

/// Compute the QPoW hash.
///
/// # Safety
/// `mining_hash` must point to 32 readable bytes, `nonce` to 64 readable
/// bytes, and `out` to 64 writable bytes.
#[no_mangle]
pub unsafe extern "C" fn quantus_hash(mining_hash: *const u8, nonce: *const u8, out: *mut u8) {
    let mut input = [0u8; 96];
    input[..32].copy_from_slice(slice::from_raw_parts(mining_hash, 32));
    input[32..].copy_from_slice(slice::from_raw_parts(nonce, 64));
    let hash = qp_poseidon_core::hash_squeeze_twice(&input);
    slice::from_raw_parts_mut(out, 64).copy_from_slice(&hash);
}
