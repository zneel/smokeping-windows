using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmokePing.Net.Alerting;
using SmokePing.Net.Configuration;
using SmokePing.Net.Probes;
using SmokePing.Net.Storage;

namespace SmokePing.Net.Services;

/// <summary>
/// Runs one measurement loop per target. Rounds are aligned to absolute multiples of
/// the step, so every target's samples land in the same buckets and graphs from
/// different targets line up - the same scheduling upstream gets from its main loop.
/// </summary>
public sealed class PollingService : BackgroundService
{
    private readonly LoadedConfiguration _config;
    private readonly ProbeRegistry _probes;
    private readonly DataStore _store;
    private readonly AlertEngine _alertEngine;
    private readonly AlertNotifier _notifier;
    private readonly HostResolver _hostResolver;
    private readonly ILogger<PollingService> _logger;
    private readonly TimeProvider _timeProvider;

    public PollingService(
        LoadedConfiguration config,
        ProbeRegistry probes,
        DataStore store,
        AlertEngine alertEngine,
        AlertNotifier notifier,
        HostResolver hostResolver,
        ILogger<PollingService> logger,
        TimeProvider timeProvider)
    {
        _config = config;
        _probes = probes;
        _store = store;
        _alertEngine = alertEngine;
        _notifier = notifier;
        _hostResolver = hostResolver;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Measuring {Count} target(s) from {Config}.",
            _config.Targets.Count,
            _config.ConfigFilePath);

        // Open every database up front so a permissions or disk problem is reported
        // immediately rather than at the first round of some target hours later.
        foreach (var target in _config.Targets)
        {
            _store.GetOrOpen(target);
        }

        var loops = _config.Targets.Select(target => RunTargetAsync(target, stoppingToken));
        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    private async Task RunTargetAsync(MeasuredTarget target, CancellationToken stoppingToken)
    {
        var probe = _probes.Get(target.ProbeType);
        var database = _store.GetOrOpen(target);

        while (!stoppingToken.IsCancellationRequested)
        {
            var slotStart = await WaitForNextSlotAsync(target.StepSeconds, stoppingToken).ConfigureAwait(false);
            if (slotStart is null)
            {
                return;
            }

            try
            {
                // Resolved every round rather than once at start-up, so a machine that
                // moves to a different network starts measuring its new gateway.
                var measured = ResolveHost(target);
                if (measured is null)
                {
                    // Nothing was measured, so nothing is recorded. Writing an
                    // all-lost round would claim the target was unreachable, when in
                    // fact we never found an address to reach; the original leaves a
                    // gap in the same situation.
                    continue;
                }

                var measurements = await probe.MeasureAsync(measured, stoppingToken).ConfigureAwait(false);
                var (sent, lost, median, quantiles) = Quantiles.Compute(measurements);

                // Jitter depends on the order the probes came back in, which the
                // stored quantiles do not preserve, so it is computed here.
                var jitter = Jitter.Compute(measurements);
                database.Write(slotStart.Value, sent, lost, quantiles, jitter, median);

                var sample = new Sample
                {
                    Timestamp = slotStart.Value,
                    Sent = sent,
                    Lost = lost,
                    Quantiles = quantiles,
                    Jitter = jitter,
                    MedianValue = median,
                };

                _logger.LogDebug(
                    "{Target}: {Lost}/{Sent} lost, median {Median:F1}ms, jitter {Jitter:F1}ms.",
                    target.Id,
                    lost,
                    sent,
                    sample.Median ?? double.NaN,
                    sample.JitterMilliseconds ?? double.NaN);

                await RaiseAlertsAsync(target, sample, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad round must never take the target's loop down for good.
                _logger.LogError(ex, "Measurement round for {Target} failed.", target.Id);
            }
        }
    }

    /// <summary>
    /// Substitutes a dynamic host token for the address it currently names. Returns
    /// null when the token cannot be resolved, which is recorded as a lost round
    /// rather than being allowed to stop the target's loop.
    /// </summary>
    private MeasuredTarget? ResolveHost(MeasuredTarget target)
    {
        if (!target.HasDynamicHost)
        {
            return target;
        }

        var resolved = _hostResolver.Resolve(target.Host);
        return resolved is null ? null : target with { Host = resolved };
    }

    private async Task RaiseAlertsAsync(MeasuredTarget target, Sample sample, CancellationToken cancellationToken)
    {
        var events = _alertEngine.Evaluate(target, sample, _timeProvider.GetUtcNow());
        foreach (var alertEvent in events)
        {
            if (_config.Alerts.TryGetValue(alertEvent.AlertName, out var rule))
            {
                await _notifier.NotifyAsync(alertEvent, rule.Config, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Sleeps until the start of the next step boundary and returns that boundary as a
    /// unix timestamp, or null when shutdown was requested while waiting.
    /// </summary>
    private async Task<long?> WaitForNextSlotAsync(int stepSeconds, CancellationToken stoppingToken)
    {
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var stepMs = stepSeconds * 1000L;
        var nextSlotMs = ((now / stepMs) + 1) * stepMs;

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(nextSlotMs - now), _timeProvider, stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        return nextSlotMs / 1000;
    }
}
