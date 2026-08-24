using System.Diagnostics;
using System.Net.Http.Headers;
using SmokePing.Net.Configuration;

namespace SmokePing.Net.Probes;

/// <summary>
/// Times a full HTTP request, like upstream's Curl / EchoPingHttp probes. The whole
/// response is drained so the measurement includes transfer time, not just headers.
/// </summary>
public sealed class HttpProbe : ProbeBase
{
    /// <summary>Name of the configured client this probe requests from the factory.</summary>
    public const string HttpClientName = "probe";

    private readonly IHttpClientFactory _httpClientFactory;

    public HttpProbe(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public override string Name => "http";

    public override string Describe(MeasuredTarget target) =>
        $"{target.Pings} HTTP GETs of {ResolveUrl(target)} every {target.StepSeconds}s";

    protected override async Task<double?> MeasureOnceAsync(MeasuredTarget target, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(target.TimeoutMs);

        using var request = new HttpRequestMessage(HttpMethod.Get, ResolveUrl(target));

        // Caches would turn this into a measurement of the local disk.
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        // Taking the client from the factory each time keeps handler rotation working,
        // so a target whose address changes is not pinned to a stale connection for
        // the lifetime of the daemon.
        var httpClient = _httpClientFactory.CreateClient(HttpClientName);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            await response.Content.CopyToAsync(Stream.Null, timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();

            // A 404 or a 500 is a server answering, and answering is what is being
            // timed. Upstream counts it as a measurement too - curl exits zero and its
            // require_zero_status defaults to off - so treating it as loss here would
            // show a permanently dead target that upstream graphs perfectly normally.
            return stopwatch.Elapsed.TotalMilliseconds;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Uses an explicit URL when configured, otherwise builds one from the host.
    /// <c>%host%</c> is substituted as upstream's urlformat does, so one URL set on a
    /// folder can serve every target below it.
    /// </summary>
    private static string ResolveUrl(MeasuredTarget target) =>
        string.IsNullOrWhiteSpace(target.Url)
            ? $"http://{target.Host}/"
            : target.Url.Replace("%host%", target.Host, StringComparison.OrdinalIgnoreCase);
}
