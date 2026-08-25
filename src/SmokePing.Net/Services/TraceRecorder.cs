using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmokePing.Net.Configuration;
using SmokePing.Net.Probes;
using SmokePing.Net.Storage;

namespace SmokePing.Net.Services;

/// <summary>
/// Records every traced target once a second, continuously.
///
/// The measurement loop answers "how is this link doing", on a schedule chosen so a
/// year of history fits in a couple of megabytes. It cannot answer "what happened at
/// 21:43" - a round of twenty probes spread over five minutes has no idea which
/// second was bad, and by design it throws that away.
///
/// This runs alongside it and keeps the seconds. It is deliberately not tied to a
/// viewer: whether it records has nothing to do with whether a browser tab is open,
/// because the moment worth catching is precisely the one nobody was watching.
/// </summary>
public sealed class TraceRecorder : BackgroundService
{
    /// <summary>
    /// How often recorded probes are pushed to disk. A crash loses at most this much
    /// of the trace, which is the right trade against fsyncing once a second.
    /// </summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How stale the previous answer may be and still be worth differencing against.
    /// Jitter is the change between consecutive probes; across a gap - a run of losses,
    /// a restart - the difference is between two unrelated moments and means nothing.
    /// </summary>
    private const int MaximumJitterGapIntervals = 3;

    private readonly LoadedConfiguration _config;
    private readonly ProbeRegistry _probes;
    private readonly TraceStore _store;
    private readonly HostResolver _hostResolver;
    private readonly ILogger<TraceRecorder> _logger;
    private readonly TimeProvider _timeProvider;

    public TraceRecorder(
        LoadedConfiguration config,
        ProbeRegistry probes,
        TraceStore store,
        HostResolver hostResolver,
        ILogger<TraceRecorder> logger,
        TimeProvider timeProvider)
    {
        _config = config;
        _probes = probes;
        _store = store;
        _hostResolver = hostResolver;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var traced = _config.Targets.Where(t => t.Traced).ToArray();
        if (traced.Length == 0)
        {
            _logger.LogInformation("No targets are traced; the second-by-second recorder is idle.");
            return;
        }

        _logger.LogInformation(
            "Recording {Count} target(s) every {Interval}s, {Size:F1} MB each.",
            traced.Length,
            _store.StepSeconds,
            _store.BytesPerTarget / 1024.0 / 1024.0);

        // Opened up front so a disk or permissions problem is reported now rather than
        // being discovered when somebody comes looking for a spike that was never saved.
        foreach (var target in traced)
        {
            _store.GetOrOpen(target);
        }

        var loops = traced.Select(target => RunTargetAsync(target, stoppingToken)).ToList();
        loops.Add(FlushPeriodicallyAsync(stoppingToken));
        await Task.WhenAll(loops).ConfigureAwait(false);

        _store.Flush(toDisk: true);
    }

    private async Task RunTargetAsync(MeasuredTarget target, CancellationToken stoppingToken)
    {
        var probe = _probes.Get(target.ProbeType);
        var file = _store.GetOrOpen(target);

        // One probe per tick rather than a round: a round would take longer than the
        // interval and the point here is one reading per second, not a distribution.
        var single = target with { Pings = 1, PingIntervalMs = 0 };

        var previousRtt = double.NaN;
        var previousTick = long.MinValue;

        var intervalMs = _store.StepSeconds * 1000L;
        var stopwatch = Stopwatch.StartNew();
        var tick = 0L;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Paced from a fixed origin rather than by sleeping a second after each
            // probe, so a slow probe does not push every later one later still.
            var wait = TimeSpan.FromMilliseconds(tick * intervalMs) - stopwatch.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            tick++;

            var timestamp = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
            double? rtt = null;
            try
            {
                var resolved = target.HasDynamicHost ? _hostResolver.Resolve(target.Host) : target.Host;
                if (resolved is not null)
                {
                    var measured = await probe
                        .MeasureAsync(single with { Host = resolved }, stoppingToken)
                        .ConfigureAwait(false);

                    rtt = measured.Length > 0 ? measured[0] : null;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A probe that threw is a probe that did not come back, which is the
                // same thing as a lost one as far as the trace is concerned.
                _logger.LogDebug(ex, "Traced probe for {Target} failed.", target.Id);
            }

            var jitter = float.NaN;
            if (rtt is { } value)
            {
                if (!double.IsNaN(previousRtt) && tick - previousTick <= MaximumJitterGapIntervals)
                {
                    jitter = (float)Math.Abs(value - previousRtt);
                }

                previousRtt = value;
                previousTick = tick;
            }

            try
            {
                file.Record(timestamp, rtt, jitter);
            }
            catch (Exception ex)
            {
                // Losing the trace must never take the loop, or the daemon, down.
                _logger.LogError(ex, "Could not record a trace sample for {Target}.", target.Id);
            }
        }
    }

    private async Task FlushPeriodicallyAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FlushInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                _store.Flush(toDisk: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not flush the trace files.");
            }
        }
    }
}
