using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Quantus.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Block = Miningcore.Persistence.Model.Block;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Blockchain.Quantus;

/// <summary>
/// Quantus (QTC) payouts.
///
/// Block rewards are minted by the chain to the node's wormhole address
/// (--rewards-inner-hash). A separate root-only job (quantus wormhole collect-rewards)
/// proves ownership and moves them into the pool payout wallet. This handler:
///   - confirms blocks once Quantus finalizes them and the canonical block at that
///     height carries our PoW nonce, and records the reward
///     (block reward = ((21M QTC - issuance before the block) / 50,000,000), rounded down
///     to the 0.01 QTC wormhole quantum, minus the wormhole exit fee);
///   - pays miners from the payout wallet with `quantus batch send` (utility.batch_all,
///     so a batch either pays everyone in it or no-one).
/// </summary>
[CoinFamily(CoinFamily.Quantus)]
public class QuantusPayoutHandler : PayoutHandlerBase, IPayoutHandler
{
    public QuantusPayoutHandler(
        IComponentContext ctx,
        IConnectionFactory cf,
        IMapper mapper,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo,
        IPaymentRepository paymentRepo,
        IMasterClock clock,
        IMessageBus messageBus) :
        base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
    {
        Contract.RequiresNonNull(ctx);
        this.ctx = ctx;
    }

    private readonly IComponentContext ctx;
    private RpcClient rpc;
    private QuantusPaymentProcessingConfigExtra extraConfig;

    private const decimal Unit = 1_000_000_000_000m;                      // 12 decimals
    private static readonly BigInteger MaxSupply = 21_000_000 * (BigInteger) 1_000_000_000_000;
    private static readonly BigInteger EmissionDivisor = 50_000_000;
    private static readonly BigInteger LeafQuantum = 10_000_000_000;      // 0.01 QTC
    private const string TotalIssuanceKey = "0xc2261276cc9d1f8598ea4b6a74b15c2f57c875e4cff74148e4628f264b974c80";
    private static readonly Regex TxHashRegex = new("Hash:\\s*(0x[0-9a-fA-F]{64})", RegexOptions.Compiled);
    private static readonly Regex FreeRegex = new("Free:\\s*([0-9][0-9.,]*)", RegexOptions.Compiled);

    protected override string LogCategory => "Quantus Payout Handler";

    public Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        Contract.RequiresNonNull(pc);

        poolConfig = pc;
        clusterConfig = cc;
        logger = LogUtil.GetPoolScopedLogger(typeof(QuantusPayoutHandler), pc);

        extraConfig = pc.PaymentProcessing?.Extra?.SafeExtensionDataAs<QuantusPaymentProcessingConfigExtra>() ?? new();

        var endpoint = pc.Daemons.First(x => string.IsNullOrEmpty(x.Category));
        rpc = new RpcClient(endpoint, ctx.Resolve<JsonSerializerSettings>(), messageBus, pc.Id);

        return Task.CompletedTask;
    }

    #region Blocks

    public async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        Contract.RequiresNonNull(blocks);

        var result = new List<Block>();

        var bestHeight = await GetHeightAsync(null, ct);
        var finalizedHash = (await rpc.ExecuteAsync<JToken>(logger, "chain_getFinalizedHead", ct, Array.Empty<object>())).Response?.Value<string>();
        var finalizedHeight = finalizedHash != null ? await GetHeightAsync(finalizedHash, ct) : null;

        if(bestHeight == null || finalizedHeight == null)
        {
            logger.Warn(() => $"[{LogCategory}] Node unavailable, skipping block classification");
            return Array.Empty<Block>();
        }

        foreach(var block in blocks)
        {
            try
            {
                var nonce = block.TransactionConfirmationData;

                if(block.BlockHeight > bestHeight || string.IsNullOrEmpty(nonce))
                {
                    if(string.IsNullOrEmpty(nonce))
                        logger.Warn(() => $"[{LogCategory}] Block {block.BlockHeight} has no nonce recorded");

                    continue;
                }

                var canonicalHash = (await rpc.ExecuteAsync<JToken>(logger, QuantusConstants.RpcGetBlockHash, ct, new object[] { block.BlockHeight })).Response?.Value<string>();
                var header = canonicalHash != null ? (await rpc.ExecuteAsync<JToken>(logger, QuantusConstants.RpcGetHeader, ct, new object[] { canonicalHash })).Response : null;

                if(header == null)
                    continue;

                var seal = (header["digest"]?["logs"] as JArray)?.LastOrDefault()?.Value<string>();
                var isOurs = seal != null && seal.EndsWith(nonce, StringComparison.OrdinalIgnoreCase);
                var isFinal = block.BlockHeight <= finalizedHeight;

                if(!isOurs)
                {
                    if(isFinal)
                    {
                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;
                        result.Add(block);

                        logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} orphaned (finalized chain has a different block)");
                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, poolConfig.Template);
                    }

                    continue;
                }

                block.Hash = canonicalHash;

                if(!isFinal)
                {
                    var window = Math.Max(1d, (double) (bestHeight.Value - finalizedHeight.Value));
                    block.ConfirmationProgress = Math.Min(0.99, (bestHeight.Value - block.BlockHeight) / window);
                    result.Add(block);

                    messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, poolConfig.Template);
                    continue;
                }

                var reward = await GetBlockRewardAsync(header.Value<string>("parentHash"), ct);

                if(reward == null)
                {
                    logger.Warn(() => $"[{LogCategory}] Could not determine reward for block {block.BlockHeight}, will retry");
                    continue;
                }

                block.Status = BlockStatus.Confirmed;
                block.ConfirmationProgress = 1;
                block.Reward = reward.Value;
                result.Add(block);

                logger.Info(() => $"[{LogCategory}] Unlocked block {block.BlockHeight} worth {FormatAmount(block.Reward)}");
                messageBus.NotifyBlockUnlocked(poolConfig.Id, block, poolConfig.Template);
            }

            catch(Exception ex)
            {
                logger.Warn(() => $"[{LogCategory}] Error classifying block {block.BlockHeight}: {ex.Message}");
            }
        }

        return result.ToArray();
    }

    /// <summary>Mirrors pallet-mining-rewards: reward = (MaxSupply - issuance) / EmissionDivisor, quantized.</summary>
    private async Task<decimal?> GetBlockRewardAsync(string parentHash, CancellationToken ct)
    {
        var raw = (await rpc.ExecuteAsync<JToken>(logger, "state_getStorage", ct, new object[] { TotalIssuanceKey, parentHash })).Response?.Value<string>();

        if(string.IsNullOrEmpty(raw))
            return null;

        var issuance = new BigInteger(Convert.FromHexString(raw[2..]), isUnsigned: true, isBigEndian: false);
        var gross = (MaxSupply - issuance) / EmissionDivisor;
        gross -= gross % LeafQuantum;

        var exitFeeBps = extraConfig.ExitFeeBps ?? 4;
        var net = gross - gross * exitFeeBps / 10_000;

        return (decimal) net / Unit;
    }

    private async Task<ulong?> GetHeightAsync(string hash, CancellationToken ct)
    {
        var args = hash != null ? new object[] { hash } : Array.Empty<object>();
        var number = (await rpc.ExecuteAsync<JToken>(logger, QuantusConstants.RpcGetHeader, ct, args)).Response?.Value<string>("number");

        return number != null ? Convert.ToUInt64(number, 16) : null;
    }

    public double AdjustBlockEffort(double effort) => effort;

    #endregion

    #region Payouts

    public async Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
    {
        Contract.RequiresNonNull(balances);

        var payable = balances.Where(x => x.Amount > 0).ToArray();

        if(payable.Length == 0)
            return;

        if(string.IsNullOrEmpty(extraConfig.WalletName) || string.IsNullOrEmpty(extraConfig.WalletPasswordFile) || string.IsNullOrEmpty(extraConfig.WalletAddress))
        {
            logger.Error(() => $"[{LogCategory}] paymentProcessing.extra needs walletName, walletPasswordFile and walletAddress");
            return;
        }

        var invalid = payable.Where(x => !IsValidAddress(x.Address)).ToArray();

        foreach(var b in invalid)
            logger.Warn(() => $"[{LogCategory}] Skipping payment to invalid address {b.Address}");

        payable = payable.Except(invalid).ToArray();

        var total = payable.Sum(x => x.Amount);
        var available = await GetWalletFreeAsync(ct);

        if(available == null)
        {
            logger.Warn(() => $"[{LogCategory}] Could not read payout wallet balance, skipping payout run");
            return;
        }

        // leave room for the batch fee and the existential deposit
        var reserve = extraConfig.FeeReserve ?? 0.05m;

        if(available.Value < total + reserve)
        {
            logger.Warn(() => $"[{LogCategory}] Payout wallet holds {FormatAmount(available.Value)} but {FormatAmount(total)} (+{FormatAmount(reserve)} reserve) is due. " +
                "Waiting for the reward collection job to move more funds in.");
            return;
        }

        var batchSize = Math.Max(1, extraConfig.MaxBatchSize ?? 200);

        foreach(var chunk in payable.Chunk(batchSize))
        {
            ct.ThrowIfCancellationRequested();

            if(!await PayBatchAsync(chunk, ct))
                break;
        }
    }

    private async Task<bool> PayBatchAsync(Balance[] chunk, CancellationToken ct)
    {
        var chunkTotal = chunk.Sum(x => x.Amount);
        var file = Path.Combine(Path.GetTempPath(), $"qtc-payout-{Guid.NewGuid():N}.json");

        var transfers = chunk.Select(x => new
        {
            to = x.Address,
            amount = ((BigInteger) decimal.Truncate(x.Amount * Unit)).ToString(CultureInfo.InvariantCulture),
        });

        await File.WriteAllTextAsync(file, JsonConvert.SerializeObject(transfers), ct);

        logger.Info(() => $"[{LogCategory}] Paying {FormatAmount(chunkTotal)} to {chunk.Length} addresses");

        try
        {
            var before = await GetWalletFreeAsync(ct);

            var (exitCode, output) = await RunCliAsync(ct,
                "batch", "send",
                "--from", extraConfig.WalletName,
                "--password-file", extraConfig.WalletPasswordFile,
                "--batch-file", file,
                "--wait-for-transaction");

            var hash = TxHashRegex.Match(output).Groups[1].Value;

            if(exitCode == 0 && !string.IsNullOrEmpty(hash))
            {
                logger.Info(() => $"[{LogCategory}] Batch payout included in block, tx {hash}");

                await PersistPaymentsAsync(chunk, hash);
                NotifyPayoutSuccess(poolConfig.Id, chunk, new[] { hash }, null);
                return true;
            }

            // The CLI did not confirm inclusion. Check whether the funds left anyway
            // (e.g. a timeout after submission) before deciding the miners were not paid.
            var after = await GetWalletFreeAsync(ct);

            if(before != null && after != null && before.Value - after.Value >= chunkTotal)
            {
                var reference = !string.IsNullOrEmpty(hash) ? hash : $"unconfirmed-{clock.Now:yyyyMMddHHmmss}";

                logger.Warn(() => $"[{LogCategory}] CLI reported failure but {FormatAmount(before.Value - after.Value)} left the wallet; recording payment as {reference}");

                await PersistPaymentsAsync(chunk, reference);
                messageBus.SendMessage(new Notifications.Messages.AdminNotification("QTC payout needs review",
                    $"Pool {poolConfig.Id}: batch of {chunk.Length} payments recorded as {reference} although the CLI did not confirm inclusion."));
                return true;
            }

            var tail = string.Join(" | ", output.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(5));
            logger.Error(() => $"[{LogCategory}] Batch payout failed (exit {exitCode}); balances NOT deducted: {tail}");
            NotifyPayoutFailure(poolConfig.Id, chunk, $"quantus batch send failed: {tail}", null);
            return false;
        }

        finally
        {
            try
            {
                File.Delete(file);
            }

            catch
            {
                // ignored
            }
        }
    }

    private async Task<decimal?> GetWalletFreeAsync(CancellationToken ct)
    {
        var (exitCode, output) = await RunCliAsync(ct, "balance", "--address", extraConfig.WalletAddress);
        var m = FreeRegex.Match(output);

        if(exitCode != 0 || !m.Success)
            return null;

        return decimal.Parse(m.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture);
    }

    private async Task<(int ExitCode, string Output)> RunCliAsync(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo(extraConfig.CliPath ?? "/usr/local/bin/quantus")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        };

        foreach(var arg in args)
            psi.ArgumentList.Add(arg);

        psi.ArgumentList.Add("--node-url");
        psi.ArgumentList.Add(extraConfig.NodeUrl ?? "ws://127.0.0.1:9944");
        psi.Environment["NO_COLOR"] = "1";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start quantus cli");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(extraConfig.CliTimeoutMinutes ?? 10));

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }

        catch(OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }

            catch
            {
                // ignored
            }

            return (-1, "timed out");
        }

        // strip ANSI colour codes in case the CLI ignores NO_COLOR
        var output = Regex.Replace(await stdout + "\n" + await stderr, "\u001b\\[[0-9;]*m", "");

        return (process.ExitCode, output);
    }

    private static bool IsValidAddress(string address)
    {
        // same SS58 / prefix-189 check the stratum login uses
        try
        {
            var data = NBitcoin.DataEncoders.Encoders.Base58.DecodeData(address);
            return data.Length == 36 && data[0] >= 64 &&
                (((data[0] & 0x3f) << 2) | (data[1] >> 6) | ((data[1] & 0x3f) << 8)) == QuantusConstants.Ss58Prefix;
        }

        catch
        {
            return false;
        }
    }

    #endregion
}
