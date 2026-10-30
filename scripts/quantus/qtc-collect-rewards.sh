#!/bin/bash
# Moves Quantus mining rewards from the pool node's wormhole address into the
# pool payout wallet (ZK-proved withdrawal via quantus-cli). Runs as root from
# qtc-collect-rewards.timer; the wormhole mnemonic never leaves /root.
set -euo pipefail

MNEMONIC=/root/quantus-secrets/pool-rewards-mnemonic.txt
DEST=qzktiwi77CXvMxUEBKxv1ChgWkLtKNAt5XcNcZjqCZYjUspAY          # qtcpool payout wallet
INDEXER=https://sqm.quantus.com/v1/graphql                       # mainnet indexer (sub2 = testnet)
NODE=ws://127.0.0.1:9944

exec 9>/run/qtc-collect-rewards.lock
flock -n 9 || { echo "already running"; exit 0; }

cd /root
echo "$(date -Is) collecting rewards -> $DEST"
quantus wormhole collect-rewards \
  --mnemonic-file "$MNEMONIC" --wormhole-index 0 \
  --destination "$DEST" \
  --subsquid-url "$INDEXER" \
  --node-url "$NODE" 2>&1 | grep -vE '^(Building|Generating|Publishing|Serializing|Saved|Common data|Verifier data|Config saved|Public-batch|Circuit built)'
quantus balance --address "$DEST" --node-url "$NODE" 2>&1 | grep -E 'Free' || true
