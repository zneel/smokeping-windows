using SmokePing.Net.Configuration;

namespace SmokePing.Net.Probes;

/// <summary>
/// Shared scaffolding for probes that send their measurements one after another:
/// handles pacing, cancellation and turning exceptions into lost probes.
/// </summary>
public abstract class ProbeBase : IProbe
{
    public abstract string Name { get; }

    public abstract string Describe(MeasuredTarget target);

    /// <summary>Performs a single measurement, returning null when the probe was lost.</summary>
    protected abstract Task<double?> MeasureOnceAsync(MeasuredTarget target, CancellationToken cancellationToken);

    public async Task<double?[]> MeasureAsync(MeasuredTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var results = new double?[target.Pings];
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < target.Pings; i++)
        {
            // The interval is measured send to send, as upstream's fping and DNS
            // probes both do. Sleeping after each measurement instead would stretch
            // every round by the round-trip time, and on a slow link a round would
            // quietly grow past its step.
            var due = TimeSpan.FromMilliseconds((long)i * target.PingIntervalMs);
            var wait = due - stopwatch.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                results[i] = await MeasureOnceAsync(target, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Any failure to reach the target is simply a lost probe; SmokePing's
                // job is to plot that loss, not to give up on the target.
                results[i] = null;
            }
        }

        return results;
    }
}
