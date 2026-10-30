#!/usr/bin/env python3
"""Check libquantus.so against real mainnet blocks from the local quantus-node.

qtc-verify (built from Quantus's own v1.0.5 source) recomputes each block's
Poseidon header hash, the mining pre-hash and the official qpow-math nonce
hash. This script then hashes the same pre-hash + nonce with libquantus.so
and checks that both agree and that the result meets real PoW difficulty.
"""
import ctypes, json, os, subprocess, sys, urllib.request

LIB = ctypes.CDLL(sys.argv[1])
TOOL = sys.argv[2]
RPC = "http://127.0.0.1:9944"

def rpc(method, params=None):
    req = urllib.request.Request(RPC, json.dumps({"id": 1, "jsonrpc": "2.0", "method": method, "params": params or []}).encode(),
                                 {"Content-Type": "application/json"})
    r = json.load(urllib.request.urlopen(req))
    if "error" in r: raise RuntimeError(r["error"])
    return r["result"]

def qhash(pre, nonce):
    out = ctypes.create_string_buffer(64)
    LIB.quantus_hash(ctypes.c_char_p(pre), ctypes.c_char_p(nonce), out)
    return out.raw

best = int(rpc("chain_getHeader")["number"], 16)
nums = [best - 1, best - 50, best - 2000, best // 2, best // 4, 5000, 1000, 1]
lines = []
for n in nums:
    bh = rpc("chain_getBlockHash", [n])
    lines.append(bh + " " + json.dumps(rpc("chain_getHeader", [bh]), separators=(",", ":")))
out = subprocess.run([TOOL], input="\n".join(lines) + "\n", capture_output=True, text=True, check=True).stdout.split("\n")

ok = True
for n, row in zip(nums, out):
    full_ok, pre, nonce, official = row.split()
    pre, nonce = bytes.fromhex(pre), bytes.fromhex(nonce)
    mine = qhash(pre, nonce)
    zeros = 512 - int.from_bytes(mine, "big").bit_length()
    ctrl = 512 - int.from_bytes(qhash(pre, os.urandom(64)), "big").bit_length()
    good = full_ok == "true" and mine.hex() == official and zeros >= 16
    ok &= good
    print(f"block {n:>7}  header-hash={'OK' if full_ok == 'true' else 'BAD'}  "
          f"lib==qpow-math={'YES' if mine.hex() == official else 'NO'}  "
          f"leading-zero-bits={zeros:>3} (random nonce: {ctrl})  {'PASS' if good else 'FAIL'}")
print("ALL PASS" if ok else "SOME FAILED")
