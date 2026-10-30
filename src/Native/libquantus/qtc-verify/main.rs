//! Reads lines of "<0x block hash> <header JSON from chain_getHeader>" on stdin.
//! Prints: <full-hash-matches> <pre_hash hex> <nonce hex> <qpow_math hash hex>
use std::io::BufRead;

use qp_header::Header;
use sp_runtime::{generic::DigestItem, traits::BlakeTwo256};

type H = Header<u32, BlakeTwo256>;

fn main() {
	for line in std::io::stdin().lock().lines() {
		let line = line.unwrap();
		let (block_hash, json) = line.split_once(' ').expect("bad input line");
		let mut hdr: H = serde_json::from_str(json).expect("header json");
		let full = format!("0x{}", hex::encode(hdr.hash().0));
		let nonce = match hdr.digest.logs.pop() {
			Some(DigestItem::Seal(_, n)) => n,
			other => panic!("last digest item is not a seal: {:?}", other),
		};
		let pre = hdr.hash().0;
		let nonce64: [u8; 64] = nonce.clone().try_into().expect("nonce is not 64 bytes");
		let q = qpow_math::get_nonce_hash(pre, nonce64);
		println!(
			"{} {} {} {}",
			full == block_hash,
			hex::encode(pre),
			hex::encode(&nonce),
			hex::encode(q.to_big_endian())
		);
	}
}
