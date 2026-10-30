using System.Net;
using System.Numerics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using Autofac;
using Miningcore.Blockchain.Quantus.Configuration;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Native;
using Miningcore.Notifications.Messages;
using Miningcore.Rpc;
using Miningcore.Stratum;
using Miningcore.Time;
using NBitcoin.DataEncoders;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Quantus;

/// <summary>
/// Quantus (QTC) job manager.
///
/// Work comes from quantus-node's external-miner QUIC endpoint (see QuantusNodeClient):
/// the node hands out (mining_hash, network difficulty) and accepts a 64-byte nonce back.
/// Shares are verified locally with libquantus (Poseidon2 QPoW):
///   valid share  : hash &lt; (2^512-1) / share difficulty
///   valid block  : hash &lt; (2^512-1) / network difficulty
/// Chain state (height, peers, sync) comes from the node's Substrate JSON-RPC.
/// Block rewards are paid by the node to the wormhole address derived from its
/// --rewards-inner-hash, not to poolConfig.Address.
/// </summary>
public class QuantusJobManager : JobManagerBase<QuantusJob>
{
    public QuantusJobManager(
        IComponentContext ctx,
        IMasterClock clock,
        IMessageBus messageBus,
        IExtraNonceProvider extraNonceProvider) :
        base(ctx, messageBus)
    {
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(extraNonceProvider);

        this.clock = clock;
        this.extraNonceProvider = extraNonceProvider;
    }

    private readonly IMasterClock clock;
    private readonly IExtraNonceProvider extraNonceProvider;
    private readonly ReplaySubject<object> jobSubject = new(1);
    private static readonly object[] noParams = Array.Empty<object>();
    private static readonly byte[] ss58Pre = Encoding.ASCII.GetBytes("SS58PRE");

    private RpcClient rpc;
    private QuantusNodeClient nodeClient;
    private QuantusCoinTemplate coin;
    private QuantusPoolConfigExtra extraConfig;

    public IObservable<object> Jobs => jobSubject.AsObservable();
    public BlockchainStats BlockchainStats { get; } = new();
    public QuantusCoinTemplate Coin => coin;
    public int MaxActiveJobs => extraConfig?.MaxActiveJobs ?? 4;

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<QuantusCoinTemplate>();
        extraConfig = pc.Extra.SafeExtensionDataAs<QuantusPoolConfigExtra>() ?? new();

        base.Configure(pc, cc);
    }

    public override QuantusJob GetJobForStratum()
    {
        lock(jobLock)
        {
            return currentJob;
        }
    }

    public string NextExtraNonce() => extraNonceProvider.Next().ToLowerInvariant();

    #region Address validation

    /// <summary>SS58 address with the Quantus prefix (189) and a valid blake2b checksum.</summary>
    public bool ValidateAddress(string address)
    {
        if(string.IsNullOrWhiteSpace(address))
            return false;

        try
        {
            var data = Encoders.Base58.DecodeData(address.Trim());

            // 2-byte prefix + 32-byte account id + 2-byte checksum
            if(data.Length != 36 || data[0] < 64)
                return false;

            var prefix = ((data[0] & 0x3f) << 2) | (data[1] >> 6) | ((data[1] & 0x3f) << 8);

            if(prefix != QuantusConstants.Ss58Prefix)
                return false;

            var input = new byte[ss58Pre.Length + 34];
            ss58Pre.CopyTo(input, 0);
            Array.Copy(data, 0, input, ss58Pre.Length, 34);

            Span<byte> checksum = stackalloc byte[64];
            new Blake2b().Digest(input, checksum);

            return checksum[0] == data[34] && checksum[1] == data[35];
        }

        catch
        {
            return false;
        }
    }

    #endregion

    #region Shares

    public async ValueTask<Share> SubmitShareAsync(StratumConnection worker, QuantusSubmitRequest request, CancellationToken ct)
    {
        Contract.RequiresNonNull(worker);
        Contract.RequiresNonNull(request);

        var context = worker.ContextAs<QuantusWorkerContext>();

        if(string.IsNullOrEmpty(request.JobId) || string.IsNullOrEmpty(request.Nonce))
            throw new StratumException(StratumError.Other, "missing job_id or nonce");

        QuantusWorkerJob workerJob;

        lock(context)
        {
            workerJob = context.FindJob(request.JobId);
        }

        if(workerJob == null)
            throw new StratumException(StratumError.JobNotFound, "job not found");

        var nonceHex = request.Nonce.Trim().ToLowerInvariant();

        if(nonceHex.StartsWith("0x"))
            nonceHex = nonceHex[2..];

        if(nonceHex.Length != QuantusConstants.NonceLength * 2 || !nonceHex.All(Uri.IsHexDigit))
            throw new StratumException(StratumError.Other, "malformed nonce");

        // the first 4 bytes of every nonce must be this worker's extranonce
        if(!nonceHex.StartsWith(context.ExtraNonce, StringComparison.Ordinal))
            throw new StratumException(StratumError.Other, "invalid nonce prefix");

        var job = workerJob.Job;

        if(!job.RegisterSubmission(nonceHex))
            throw new StratumException(StratumError.DuplicateShare, "duplicate share");

        var nonce = Convert.FromHexString(nonceHex);

        Span<byte> hashBytes = stackalloc byte[64];
        QuantusBindings.Hash(job.MiningHash, nonce, hashBytes);
        var hash = new BigInteger(hashBytes, isUnsigned: true, isBigEndian: true);

        if(hash >= QuantusJob.TargetFromDifficulty(workerJob.Difficulty))
            throw new StratumException(StratumError.LowDifficultyShare, "low difficulty share");

        var share = new Share
        {
            PoolId = poolConfig.Id,
            BlockHeight = (long) job.Height,
            NetworkDifficulty = job.NetworkDifficultyValue,
            Difficulty = workerJob.Difficulty,
            IpAddress = worker.RemoteEndpoint?.Address?.ToString(),
            Miner = context.Miner,
            Worker = context.Worker,
            UserAgent = context.UserAgent,
            Source = clusterConfig.ClusterName,
            Created = clock.Now,
        };

        if(hash < job.NetworkTarget)
            await SubmitBlockAsync(share, job, nonce, nonceHex, ct);

        return share;
    }

    private async Task SubmitBlockAsync(Share share, QuantusJob job, byte[] nonce, string nonceHex, CancellationToken ct)
    {
        logger.Info(() => $"Block candidate {job.Height} found by {share.Miner} (node job {job.NodeJobId}), submitting to node");

        if(!await nodeClient.SubmitSolutionAsync(job.NodeJobId, nonce, ct))
        {
            logger.Error(() => $"Could not hand block {job.Height} to {QuantusConstants.DaemonName} (miner connection down)");

            messageBus.SendMessage(new AdminNotification("Block submission failed",
                $"Pool {poolConfig.Id} could not submit block {job.Height}: node miner connection down"));

            return;
        }

        share.IsBlockCandidate = true;
        share.TransactionConfirmationData = nonceHex;
        share.BlockHash = "0x" + job.MiningHashHex;

        var (found, blockHash, height) = await FindSealedBlockAsync(nonceHex, ct);

        if(found)
        {
            share.BlockHash = blockHash;
            share.BlockHeight = (long) height;

            logger.Info(() => $"Node imported block {height} [{blockHash}] submitted by {share.Miner}");

            OnBlockFound();
        }

        else
            logger.Warn(() => $"Block {job.Height} submitted but not seen at the chain tip yet (may be orphaned or stale)");
    }

    /// <summary>Looks for a recent block whose PoW seal carries this nonce.</summary>
    private async Task<(bool Found, string Hash, ulong Height)> FindSealedBlockAsync(string nonceHex, CancellationToken ct)
    {
        var deadline = clock.Now.AddMilliseconds(extraConfig.BlockConfirmTimeout ?? 8000);

        while(clock.Now < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct);

            try
            {
                var header = (await rpc.ExecuteAsync<JToken>(logger, QuantusConstants.RpcGetHeader, ct, noParams)).Response;

                // check the tip and its parent
                for(var i = 0; i < 2 && header != null; i++)
                {
                    var logs = header["digest"]?["logs"] as JArray;
                    var seal = logs?.LastOrDefault()?.Value<string>();

                    if(seal != null && seal.EndsWith(nonceHex, StringComparison.OrdinalIgnoreCase))
                    {
                        var height = Convert.ToUInt64(header.Value<string>("number"), 16);
                        var hash = (await rpc.ExecuteAsync<JToken>(logger, QuantusConstants.RpcGetBlockHash, ct, new object[] { height })).Response?.Value<string>();

                        return (true, hash, height);
                    }

                    var parent = header.Value<string>("parentHash");
                    header = parent == null ? null : (await rpc.ExecuteAsync<JToken>(logger, QuantusConstants.RpcGetHeader, ct, new object[] { parent })).Response;
                }
            }

            catch(Exception ex) when(ex is not OperationCanceledException)
            {
                logger.Debug(() => $"{nameof(FindSealedBlockAsync)}: {ex.Message}");
            }
        }

        return (false, null, 0);
    }

    #endregion

    #region Jobs and stats

    private async Task OnNodeJobAsync(QuantusNodeJob nodeJob, CancellationToken ct)
    {
        var height = await GetBestHeightAsync(ct) is { } best ? best + 1 : BlockchainStats.BlockHeight + 1;
        var job = new QuantusJob(NextJobId(), nodeJob, height, clock.Now);

        bool isNewBlock;

        lock(jobLock)
        {
            isNewBlock = currentJob == null || currentJob.Height != height;
            currentJob = job;
        }

        if(isNewBlock)
        {
            messageBus.NotifyChainHeight(poolConfig.Id, height, poolConfig.Template);
            BlockchainStats.LastNetworkBlockTime = clock.Now;
            logger.Info(() => $"New job {job.JobId} for block {height} (network difficulty {job.NetworkDifficulty})");
        }

        else
            logger.Debug(() => $"Job update {job.JobId} for block {height}");

        BlockchainStats.BlockHeight = height - 1;
        BlockchainStats.NetworkDifficulty = job.NetworkDifficultyValue;
        BlockchainStats.NetworkHashrate = job.NetworkDifficultyValue / QuantusConstants.BlockTimeSeconds;

        hasInitialBlockTemplate = true;
        jobSubject.OnNext(job);
    }

    private async Task<ulong?> GetBestHeightAsync(CancellationToken ct)
    {
        var response = await rpc.ExecuteAsync<JToken>(logger, QuantusConstants.RpcGetHeader, ct, noParams);
        var number = response.Response?.Value<string>("number");

        return number != null ? Convert.ToUInt64(number, 16) : null;
    }

    private async Task UpdateNetworkStatsAsync(CancellationToken ct)
    {
        var health = await rpc.ExecuteAsync<JToken>(logger, QuantusConstants.RpcSystemHealth, ct, noParams);

        if(health.Response != null)
            BlockchainStats.ConnectedPeers = health.Response.Value<int>("peers");

        if(await GetBestHeightAsync(ct) is { } height)
            BlockchainStats.BlockHeight = height;
    }

    #endregion

    #region JobManagerBase overrides

    protected override void ConfigureDaemons()
    {
        var endpoint = poolConfig.Daemons.First(x => string.IsNullOrEmpty(x.Category));
        rpc = new RpcClient(endpoint, ctx.Resolve<JsonSerializerSettings>(), messageBus, poolConfig.Id);
    }

    protected override async Task<bool> AreDaemonsHealthyAsync(CancellationToken ct)
    {
        var response = await rpc.ExecuteAsync<JToken>(logger, QuantusConstants.RpcSystemHealth, ct, noParams);

        if(response.Error != null)
            logger.Warn(() => $"'{QuantusConstants.DaemonName}' system_health: {response.Error.Message}");

        return response.Error == null && response.Response != null;
    }

    protected override async Task<bool> AreDaemonsConnectedAsync(CancellationToken ct)
    {
        var response = await rpc.ExecuteAsync<JToken>(logger, QuantusConstants.RpcSystemHealth, ct, noParams);

        return response.Response?.Value<int>("peers") > 0;
    }

    protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        do
        {
            var response = await rpc.ExecuteAsync<JToken>(logger, QuantusConstants.RpcSystemHealth, ct, noParams);

            if(response.Response?.Value<bool>("isSyncing") == false)
            {
                logger.Info(() => $"'{QuantusConstants.DaemonName}' is synced with the network");
                break;
            }

            logger.Info(() => $"'{QuantusConstants.DaemonName}' is still syncing. Pool will start once synced.");
        } while(await timer.WaitForNextTickAsync(ct));
    }

    protected override async Task PostStartInitAsync(CancellationToken ct)
    {
        if(!QuantusNodeClient.IsSupported)
            throw new PoolStartupException("QUIC is not supported on this host (install libmsquic)", poolConfig.Id);

        if(!ValidateAddress(poolConfig.Address))
            logger.Warn(() => $"Pool address {poolConfig.Address} is not a valid Quantus SS58 address. " +
                "Note: block rewards go to the node's --rewards-inner-hash wormhole address regardless.");

        var tokenFile = extraConfig.AuthTokenFile ?? "/var/lib/quantus/chains/mainnet/miner-auth-token";
        var pinFile = extraConfig.TlsCertSha256File ?? "/var/lib/quantus/chains/mainnet/miner-tls-cert-sha256";

        string token, pin;

        try
        {
            token = (await File.ReadAllTextAsync(tokenFile, ct)).Trim();
            pin = (await File.ReadAllTextAsync(pinFile, ct)).Trim();
        }

        catch(Exception ex)
        {
            throw new PoolStartupException($"Cannot read node miner credentials ({tokenFile}, {pinFile}): {ex.Message}", poolConfig.Id);
        }

        var host = extraConfig.MinerHost ?? "127.0.0.1";
        var port = extraConfig.MinerPort ?? QuantusConstants.DefaultMinerPort;
        EndPoint endpoint = IPAddress.TryParse(host, out var ip) ? new IPEndPoint(ip, port) : new DnsEndPoint(host, port);

        var chain = await rpc.ExecuteAsync<JToken>(logger, QuantusConstants.RpcSystemChain, ct, noParams);
        var version = await rpc.ExecuteAsync<JToken>(logger, QuantusConstants.RpcSystemVersion, ct, noParams);

        BlockchainStats.NetworkType = chain.Response?.Value<string>() ?? "unknown";
        BlockchainStats.NodeVersion = version.Response?.Value<string>() ?? "Unknown";
        BlockchainStats.RewardType = "POW";

        await UpdateNetworkStatsAsync(ct);

        Observable.Interval(TimeSpan.FromSeconds(30))
            .Select(_ => Observable.FromAsync(() => Guard(() => UpdateNetworkStatsAsync(ct), ex => logger.Error(ex))))
            .Concat()
            .Subscribe();

        nodeClient = new QuantusNodeClient(logger, endpoint, token, pin);

        nodeClient.Jobs
            .Select(x => Observable.FromAsync(() => Guard(() => OnNodeJobAsync(x, ct), ex => logger.Error(ex, nameof(OnNodeJobAsync)))))
            .Concat()
            .Subscribe();

        _ = Task.Run(() => nodeClient.RunAsync(ct), ct);

        logger.Info(() => $"Waiting for first job from {QuantusConstants.DaemonName} ({BlockchainStats.NetworkType}, {BlockchainStats.NodeVersion})");
    }

    #endregion
}
