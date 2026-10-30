namespace Miningcore.Blockchain.Quantus.Configuration;

public class QuantusPoolConfigExtra
{
    /// <summary>Host of the quantus-node external-miner QUIC endpoint (default 127.0.0.1)</summary>
    public string MinerHost { get; set; }

    /// <summary>Port passed to quantus-node --miner-listen-port (default 9833)</summary>
    public int? MinerPort { get; set; }

    /// <summary>File holding the node's miner auth token (node writes it to &lt;base-path&gt;/chains/&lt;chain&gt;/miner-auth-token)</summary>
    public string AuthTokenFile { get; set; }

    /// <summary>File holding the SHA-256 fingerprint of the node's miner TLS certificate (miner-tls-cert-sha256)</summary>
    public string TlsCertSha256File { get; set; }

    /// <summary>Number of past jobs per worker that still accept shares</summary>
    public int? MaxActiveJobs { get; set; }

    /// <summary>How long to look for a submitted block on chain before giving up (ms)</summary>
    public int? BlockConfirmTimeout { get; set; }
}

public class QuantusPaymentProcessingConfigExtra
{
    /// <summary>Path to the quantus CLI binary (default /usr/local/bin/quantus)</summary>
    public string CliPath { get; set; }

    /// <summary>Node websocket endpoint for the CLI (default ws://127.0.0.1:9944)</summary>
    public string NodeUrl { get; set; }

    /// <summary>quantus-cli wallet that holds collected rewards and pays miners</summary>
    public string WalletName { get; set; }

    /// <summary>SS58 address of that wallet (used for balance checks)</summary>
    public string WalletAddress { get; set; }

    /// <summary>Owner-only file with the wallet password</summary>
    public string WalletPasswordFile { get; set; }

    /// <summary>Recipients per batch_all transaction (default 200)</summary>
    public int? MaxBatchSize { get; set; }

    /// <summary>Balance kept back for transaction fees and the existential deposit (default 0.05 QTC)</summary>
    public decimal? FeeReserve { get; set; }

    /// <summary>Wormhole exit fee in basis points deducted from block rewards (default 4 = 0.04%)</summary>
    public int? ExitFeeBps { get; set; }

    /// <summary>Timeout for a single CLI call (default 10 minutes)</summary>
    public int? CliTimeoutMinutes { get; set; }
}
