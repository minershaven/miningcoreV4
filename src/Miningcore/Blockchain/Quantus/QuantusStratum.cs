using Newtonsoft.Json;

namespace Miningcore.Blockchain.Quantus;

public class QuantusLoginRequest
{
    [JsonProperty("login")]
    public string Login { get; set; }

    [JsonProperty("pass")]
    public string Password { get; set; }

    [JsonProperty("agent")]
    public string UserAgent { get; set; }
}

public class QuantusSubmitRequest
{
    [JsonProperty("id")]
    public string WorkerId { get; set; }

    [JsonProperty("job_id")]
    public string JobId { get; set; }

    [JsonProperty("nonce")]
    public string Nonce { get; set; }
}

public class QuantusStratumJob
{
    [JsonProperty("difficulty")]
    public ulong Difficulty { get; set; }

    [JsonProperty("extranonce")]
    public string ExtraNonce { get; set; }

    [JsonProperty("job_id")]
    public string JobId { get; set; }

    [JsonProperty("mining_hash")]
    public string MiningHash { get; set; }

    [JsonProperty("target")]
    public string Target { get; set; }
}

public class QuantusJobNotification
{
    [JsonProperty("clean_jobs")]
    public bool CleanJobs { get; set; }

    [JsonProperty("job")]
    public QuantusStratumJob Job { get; set; }
}

public class QuantusLoginResponse
{
    [JsonProperty("extensions")]
    public string[] Extensions { get; set; } = { "keepalive" };

    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("job")]
    public QuantusStratumJob Job { get; set; }

    [JsonProperty("status")]
    public string Status { get; set; } = "OK";
}

public class QuantusStatusResponse
{
    public QuantusStatusResponse(string status = "OK")
    {
        Status = status;
    }

    [JsonProperty("status")]
    public string Status { get; set; }
}
