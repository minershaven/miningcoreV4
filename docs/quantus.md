# Quantus (QTC) support

Coin family `quantus`, algorithm `qpow` (Poseidon2 proof-of-work). Miners connect with
SRBMiner-MULTI (`--algorithm quantus`) using the XMRig-style stratum
(`login` / `job` / `submit` / `keepalived`), the same shape Kryptex uses.

## Pieces

| Path | What it is |
|---|---|
| `src/Miningcore/Blockchain/Quantus/` | Pool, job manager, QUIC node client, payout handler |
| `src/Miningcore/Native/Quantus.cs` | P/Invoke binding for `libquantus.so` |
| `src/Native/libquantus/` | Rust `cdylib` wrapping `qp-poseidon-core =3.1.0` (same version as quantus-node v1.0.5) |
| `src/Native/libquantus/verify_blocks.py`, `qtc-verify/` | Checks `libquantus` against real mainnet blocks using Quantus's own header/qpow code |
| `scripts/quantus/qtc-collect-rewards.sh` | Root-only job that withdraws block rewards from the wormhole address into the payout wallet |

## How it works

- **Work:** `quantus-node --validator --miner-listen-port 9833 --rewards-inner-hash <inner hash>` hands jobs
  (`mining_hash`, network `difficulty`) to external miners over QUIC (TLS 1.3, ALPN `quantus-miner/2`,
  cert pinned by SHA-256, token auth). The pool connects as that external miner and splits each job into
  shares. .NET's QUIC needs `libmsquic` (packages.microsoft.com).
- **Shares:** `hash = Poseidon2(mining_hash || 64-byte nonce)`; a share is valid when
  `hash < (2^512-1) / difficulty`. The first 4 bytes of every nonce are the worker's extranonce.
  Vardiff changes are applied with the next job (Quantus issues one every few seconds); re-sending the
  current job makes SRBMiner resubmit nonces and get duplicate rejections.
- **Blocks:** a share meeting network difficulty is sent back to the node as a `JobResult`; the pool then
  looks for the block (by seal nonce) at the chain tip.
- **Rewards:** the chain mints each block reward to the node's wormhole address
  (`reward = (21M - issuance) / 50,000,000`, rounded down to 0.01 QTC). `quantus wormhole collect-rewards`
  (ZK proof, 0.04% fee) moves them to the payout wallet. Use the **mainnet** indexer
  `https://sqm.quantus.com/v1/graphql` (the CLI default `sub2.quantus.com` is testnet).
- **Confirmation:** a block is confirmed once finalized (~100 blocks) and the canonical block at that
  height carries our nonce; otherwise orphaned.
- **Payouts:** `quantus batch send` from the payout wallet (`utility.batch_all`: all-or-nothing). The
  handler checks the wallet balance first and waits if the collection job hasn't moved enough in yet.

## Build

```bash
cd src/Native/libquantus && cargo build --release
cp target/release/libquantus.so <miningcore build dir>/
```

## Pool config (example)

Extra settings go directly on the pool object / `paymentProcessing` object (they are read via
`[JsonExtensionData]`), not inside a nested `"extra"` block.

```json
{
  "id": "qtc-pplns", "enabled": true, "coin": "quantus",
  "address": "<pool wormhole address>",
  "rewardRecipients": [{ "type": "op", "address": "<pool wormhole address>", "percentage": 1.0 }],
  "ports": {
    "3101": { "listenAddress": "0.0.0.0", "name": "GPU", "difficulty": 2000000000,
              "varDiff": { "minDiff": 500000000, "maxDiff": null, "targetTime": 15, "retargetTime": 90, "variancePercent": 30, "maxDelta": null } }
  },
  "daemons": [{ "host": "127.0.0.1", "port": 9944 }],
  "minerHost": "127.0.0.1", "minerPort": 9833,
  "authTokenFile": "<copy of node miner-auth-token readable by the miningcore user>",
  "tlsCertSha256File": "<copy of node miner-tls-cert-sha256>",
  "maxActiveJobs": 4,
  "paymentProcessing": {
    "enabled": true, "minimumPayment": 1, "payoutScheme": "PPLNS", "payoutSchemeConfig": { "factor": 2.0 },
    "cliPath": "/usr/local/bin/quantus", "nodeUrl": "ws://127.0.0.1:9944",
    "walletName": "qtcpool", "walletAddress": "<payout wallet address>",
    "walletPasswordFile": "<owner-only password file>",
    "maxBatchSize": 200, "feeReserve": 0.05, "exitFeeBps": 4
  }
}
```

Keep `maxDelta` null (or large): QTC difficulties are in the billions, so a small absolute cap
makes vardiff crawl.
