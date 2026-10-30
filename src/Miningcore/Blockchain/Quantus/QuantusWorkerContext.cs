using Miningcore.Mining;

namespace Miningcore.Blockchain.Quantus;

public class QuantusWorkerContext : WorkerContextBase
{
    /// <summary>Miner SS58 address (set on login)</summary>
    public override string Miner { get; set; }

    /// <summary>Worker name (suffix after the dot in the login)</summary>
    public override string Worker { get; set; }

    /// <summary>Pool-assigned 4-byte nonce prefix (hex), unique per connection</summary>
    public string ExtraNonce { get; set; }

    private readonly List<QuantusWorkerJob> validJobs = new();

    public void AddJob(QuantusWorkerJob job, int maxActiveJobs)
    {
        validJobs.RemoveAll(x => x.Id == job.Id);
        validJobs.Insert(0, job);

        while(validJobs.Count > maxActiveJobs)
            validJobs.RemoveAt(validJobs.Count - 1);
    }

    public QuantusWorkerJob FindJob(string id) => validJobs.FirstOrDefault(x => x.Id == id);
}
