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
    private readonly ILogger<PollingService> _logger;
    private readonly TimeProvider _timeProvider;

    public PollingService(
        LoadedConfiguration config,
        ProbeRegistry probes,
        DataStore store,
        AlertEngine alertEngine,
        AlertNotifier notifier,
        ILogger<PollingService> logger,
        TimeProvider timeProvider)
    {
        _config = config;
        _probes = probes;
        _store = store;
        _alertEngine = alertEngine;
        _notifier = notifier;
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
                var measurements = await probe.MeasureAsync(target, stoppingToken).ConfigureAwait(false);
                var (sent, lost, quantiles) = Quantiles.Compute(measurements);

                // Jitter depends on the order the probes came back in, which the
                // stored quantiles do not preserve, so it is computed here.
                var jitter = Jitter.Compute(measurements);
                database.Write(slotStart.Value, sent, lost, quantiles, jitter);

                var sample = new Sample
                {
                    Timestamp = slotStart.Value,
                    Sent = sent,
                    Lost = lost,
                    Quantiles = quantiles,
                    Jitter = jitter,
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
