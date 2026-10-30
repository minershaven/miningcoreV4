using System.Collections.Concurrent;
using System.Numerics;

namespace Miningcore.Blockchain.Quantus;

public class QuantusJob
{
    public QuantusJob(string jobId, QuantusNodeJob nodeJob, ulong height, DateTime created)
    {
        JobId = jobId;
        NodeJobId = nodeJob.JobId;
        MiningHash = nodeJob.MiningHash;
        MiningHashHex = Convert.ToHexString(nodeJob.MiningHash).ToLowerInvariant();
        NetworkDifficulty = nodeJob.Difficulty;
        NetworkTarget = QuantusConstants.U512Max / nodeJob.Difficulty;
        Height = height;
        Created = created;
    }

    private readonly ConcurrentDictionary<string, bool> submissions = new(StringComparer.OrdinalIgnoreCase);

    public string JobId { get; }
    public string NodeJobId { get; }
    public byte[] MiningHash { get; }
    public string MiningHashHex { get; }
    public BigInteger NetworkDifficulty { get; }
    public BigInteger NetworkTarget { get; }
    public double NetworkDifficultyValue => (double) NetworkDifficulty;
    public ulong Height { get; }
    public DateTime Created { get; }

    /// <summary>Returns false if this nonce was already submitted for this job.</summary>
    public bool RegisterSubmission(string nonceHex) => submissions.TryAdd(nonceHex, true);

    public static BigInteger TargetFromDifficulty(ulong difficulty) =>
        QuantusConstants.U512Max / Math.Max(difficulty, 1UL);

    public static string TargetToHex(BigInteger target)
    {
        var bytes = target.ToByteArray(isUnsigned: true, isBigEndian: true);
        var padded = new byte[64];
        bytes.CopyTo(padded, 64 - bytes.Length);
        return Convert.ToHexString(padded).ToLowerInvariant();
    }
}

/// <summary>A job as handed to one worker, at the difficulty that worker had at the time.</summary>
public record QuantusWorkerJob(string Id, QuantusJob Job, ulong Difficulty);
