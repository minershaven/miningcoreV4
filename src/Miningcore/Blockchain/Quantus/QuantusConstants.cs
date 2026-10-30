using System.Numerics;

namespace Miningcore.Blockchain.Quantus;

public static class QuantusConstants
{
    public const string DaemonName = "quantus-node";

    // External-miner QUIC protocol (Quantus-Network/chain miner-api)
    public const string MinerAlpn = "quantus-miner/2";
    public const int DefaultMinerPort = 9833;
    public const int MaxNodeMessageSize = 1024;

    public const int MiningHashLength = 32;
    public const int NonceLength = 64;
    public const int ExtraNonceLength = 4;

    // SS58 address format registered by the Quantus chain spec
    public const int Ss58Prefix = 189;

    // Target block time used to estimate network hashrate from difficulty
    public const double BlockTimeSeconds = 12;

    public static readonly BigInteger U512Max = (BigInteger.One << 512) - 1;

    // Stratum (XMRig-style, as spoken by SRBMiner-MULTI --algorithm quantus)
    public const string StratumLogin = "login";
    public const string StratumGetJob = "getjob";
    public const string StratumSubmit = "submit";
    public const string StratumKeepAlive = "keepalived";
    public const string StratumJobNotify = "job";

    // Substrate JSON-RPC
    public const string RpcSystemHealth = "system_health";
    public const string RpcSystemChain = "system_chain";
    public const string RpcSystemVersion = "system_version";
    public const string RpcGetHeader = "chain_getHeader";
    public const string RpcGetBlockHash = "chain_getBlockHash";
}
