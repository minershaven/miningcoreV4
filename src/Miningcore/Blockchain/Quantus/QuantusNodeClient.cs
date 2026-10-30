using System.Buffers.Binary;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Numerics;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

#pragma warning disable CA1416 // QUIC availability is checked at runtime

namespace Miningcore.Blockchain.Quantus;

/// <summary>A mining job as handed out by quantus-node.</summary>
public record QuantusNodeJob(string JobId, byte[] MiningHash, BigInteger Difficulty);

/// <summary>
/// Client for quantus-node's external-miner endpoint (Quantus-Network/chain miner-api).
///
/// QUIC + TLS 1.3, ALPN "quantus-miner/2", node certificate pinned by SHA-256.
/// One bidirectional stream carrying 4-byte big-endian length-prefixed JSON:
///   miner -> node  {"Ready":{"token":"..."}}       (immediately after connect)
///   node  -> miner {"NewJob":{"job_id","mining_hash","difficulty"}}
///   miner -> node  {"JobResult":{"status":"completed","job_id","nonce","work",...}}
/// The node implicitly cancels the previous job whenever it sends a new one.
/// </summary>
public class QuantusNodeClient
{
    public QuantusNodeClient(ILogger logger, EndPoint endpoint, string authToken, string certSha256)
    {
        this.logger = logger;
        this.endpoint = endpoint;
        this.authToken = authToken;
        this.certSha256 = certSha256.Trim().ToLowerInvariant();
    }

    private readonly ILogger logger;
    private readonly EndPoint endpoint;
    private readonly string authToken;
    private readonly string certSha256;
    private readonly Subject<QuantusNodeJob> jobs = new();
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private volatile QuicStream stream;

    public IObservable<QuantusNodeJob> Jobs => jobs;
    public bool IsConnected => stream != null;

    public static bool IsSupported => QuicConnection.IsSupported;

    /// <summary>Connects, reads jobs and reconnects with backoff until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);

        while(!ct.IsCancellationRequested)
        {
            try
            {
                logger.Info(() => $"Connecting to {QuantusConstants.DaemonName} miner endpoint {endpoint} ...");

                await using var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
                {
                    RemoteEndPoint = endpoint,
                    DefaultStreamErrorCode = 0,
                    DefaultCloseErrorCode = 0,
                    IdleTimeout = TimeSpan.FromSeconds(15),
                    KeepAliveInterval = TimeSpan.FromSeconds(5),
                    ClientAuthenticationOptions = new SslClientAuthenticationOptions
                    {
                        ApplicationProtocols = [new SslApplicationProtocol(QuantusConstants.MinerAlpn)],
                        TargetHost = "localhost",
                        RemoteCertificateValidationCallback = (_, cert, _, _) =>
                            cert != null && Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())).ToLowerInvariant() == certSha256,
                    },
                }, ct);

                await using var s = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);
                await WriteMessageAsync(s, new JObject { ["Ready"] = new JObject { ["token"] = authToken } }, ct);

                stream = s;
                delay = TimeSpan.FromSeconds(1);
                logger.Info(() => $"Connected to {QuantusConstants.DaemonName} miner endpoint {endpoint}");

                while(!ct.IsCancellationRequested)
                {
                    var msg = await ReadMessageAsync(s, ct);

                    if(msg["NewJob"] is JObject job)
                    {
                        var parsed = ParseJob(job);

                        if(parsed != null)
                            jobs.OnNext(parsed);
                    }

                    else
                        logger.Debug(() => $"Ignoring unexpected message from node: {msg.ToString(Formatting.None)}");
                }
            }

            catch(OperationCanceledException) when(ct.IsCancellationRequested)
            {
                break;
            }

            catch(Exception ex)
            {
                logger.Warn(() => $"{QuantusConstants.DaemonName} miner connection lost: {ex.Message}. Reconnecting in {delay.TotalSeconds}s");
            }

            finally
            {
                stream = null;
            }

            try
            {
                await Task.Delay(delay, ct);
            }

            catch(OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }
    }

    /// <summary>Hands a block-meeting nonce to the node. Returns false if it could not be written.</summary>
    public async Task<bool> SubmitSolutionAsync(string nodeJobId, byte[] nonce, CancellationToken ct)
    {
        var s = stream;

        if(s == null)
            return false;

        var work = Convert.ToHexString(nonce).ToLowerInvariant();
        var nonceHex = work.TrimStart('0');

        var msg = new JObject
        {
            ["JobResult"] = new JObject
            {
                ["status"] = "completed",
                ["job_id"] = nodeJobId,
                ["nonce"] = nonceHex.Length > 0 ? nonceHex : "0",
                ["work"] = work,
                ["hash_count"] = 0,
                ["elapsed_time"] = 0.0,
            }
        };

        try
        {
            await WriteMessageAsync(s, msg, ct);
            return true;
        }

        catch(Exception ex)
        {
            logger.Error(() => $"Failed to submit solution for node job {nodeJobId}: {ex.Message}");
            return false;
        }
    }

    private QuantusNodeJob ParseJob(JObject job)
    {
        try
        {
            var jobId = job.Value<string>("job_id");
            var miningHash = Convert.FromHexString(job.Value<string>("mining_hash"));
            var difficulty = BigInteger.Parse(job.Value<string>("difficulty"));

            if(string.IsNullOrEmpty(jobId) || miningHash.Length != QuantusConstants.MiningHashLength || difficulty <= 0)
                throw new FormatException("invalid job fields");

            return new QuantusNodeJob(jobId, miningHash, difficulty);
        }

        catch(Exception ex)
        {
            logger.Warn(() => $"Ignoring malformed job from node: {ex.Message}");
            return null;
        }
    }

    private async Task WriteMessageAsync(Stream s, JObject msg, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(msg.ToString(Formatting.None));

        if(body.Length > QuantusConstants.MaxNodeMessageSize)
            throw new InvalidOperationException($"message exceeds {QuantusConstants.MaxNodeMessageSize} bytes");

        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint) body.Length);
        body.CopyTo(frame, 4);

        await writeLock.WaitAsync(ct);

        try
        {
            await s.WriteAsync(frame, ct);
            await s.FlushAsync(ct);
        }

        finally
        {
            writeLock.Release();
        }
    }

    private static async Task<JObject> ReadMessageAsync(Stream s, CancellationToken ct)
    {
        var header = new byte[4];
        await s.ReadExactlyAsync(header, ct);

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);

        if(length > QuantusConstants.MaxNodeMessageSize)
            throw new InvalidDataException($"node message of {length} bytes exceeds maximum");

        var body = new byte[length];
        await s.ReadExactlyAsync(body, ct);

        return JObject.Parse(Encoding.UTF8.GetString(body));
    }
}
