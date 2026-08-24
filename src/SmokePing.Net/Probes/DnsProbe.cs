using System.Diagnostics;
using System.Net;
using DnsClient;
using SmokePing.Net.Configuration;

namespace SmokePing.Net.Probes;

/// <summary>
/// Times a DNS lookup against a specific server, like upstream's AnotherDNS probe.
///
/// The query goes to the configured server directly rather than through the system
/// resolver, so what is measured is that server and never a local cache. Record type
/// and the fallback to TCP for a truncated answer come from the client library; a
/// hand-built query packet supported neither.
/// </summary>
public sealed class DnsProbe : ProbeBase
{
    public const int DefaultPort = 53;

    /// <summary>Record type looked up when the configuration does not name one.</summary>
    public const string DefaultRecordType = "A";

    public override string Name => "dns";

    /// <summary>
    /// Upstream looks up the target host itself when no query is configured. A fixed
    /// default would quietly measure a lookup of something else entirely.
    /// </summary>
    public static string DefaultQuery(MeasuredTarget target) => target.Host;

    public override string Describe(MeasuredTarget target) =>
        $"{target.Pings} DNS lookups of {target.Query ?? DefaultQuery(target)} " +
        $"({target.RecordType ?? DefaultRecordType}) against " +
        $"{target.Host}:{target.Port ?? DefaultPort} every {target.StepSeconds}s";

    protected override async Task<double?> MeasureOnceAsync(MeasuredTarget target, CancellationToken cancellationToken)
    {
        var endpoint = await ResolveServerAsync(target, cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            return null;
        }

        var options = new LookupClientOptions(endpoint)
        {
            // The server named in the configuration is the one being measured, so
            // there is nothing to fall back to and nothing to cache.
            UseCache = false,
            UseRandomNameServer = false,
            ContinueOnDnsError = true,
            Retries = 0,
            Timeout = TimeSpan.FromMilliseconds(target.TimeoutMs),
        };

        var client = new LookupClient(options);
        var question = new DnsQuestion(
            target.Query ?? DefaultQuery(target),
            ParseRecordType(target.RecordType ?? DefaultRecordType));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // A server that answers NXDOMAIN or SERVFAIL has still answered, and
            // answering is what is being timed. Only a failure to get any reply at
            // all is a lost probe, which arrives here as an exception or a timeout.
            _ = await client.QueryAsync(question, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            return stopwatch.Elapsed.TotalMilliseconds;
        }
        catch (DnsResponseException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Maps a configured record type name onto the query type.</summary>
    public static QueryType ParseRecordType(string recordType) =>
        Enum.TryParse<QueryType>(recordType, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException($"'{recordType}' is not a DNS record type.", nameof(recordType));

    /// <summary>True when the name is one this probe can look up.</summary>
    public static bool IsKnownRecordType(string recordType) =>
        Enum.TryParse<QueryType>(recordType, ignoreCase: true, out _);

    private static async Task<IPEndPoint?> ResolveServerAsync(MeasuredTarget target, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(target.Host, out var address))
        {
            return new IPEndPoint(address, target.Port ?? DefaultPort);
        }

        var addresses = await Dns.GetHostAddressesAsync(target.Host, cancellationToken).ConfigureAwait(false);
        return addresses.Length == 0 ? null : new IPEndPoint(addresses[0], target.Port ?? DefaultPort);
    }
}
