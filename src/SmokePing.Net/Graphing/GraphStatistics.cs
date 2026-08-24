using SmokePing.Net.Storage;

namespace SmokePing.Net.Graphing;

/// <summary>
/// The figures printed under a graph, and the values the "top N" charts sort by.
///
/// These follow the original: the round-trip figures describe the series of per-round
/// medians (its average, extremes and most recent value), not the spread of individual
/// probes, which is what the smoke already shows.
/// </summary>
public sealed class GraphStatistics
{
    public required bool HasData { get; init; }

    /// <summary>Mean of the per-round medians, in milliseconds.</summary>
    public required double MedianAverage { get; init; }

    /// <summary>Highest per-round median in the period, in milliseconds.</summary>
    public required double MedianMaximum { get; init; }

    /// <summary>Lowest per-round median in the period, in milliseconds.</summary>
    public required double MedianMinimum { get; init; }

    /// <summary>Most recent per-round median, in milliseconds.</summary>
    public required double MedianNow { get; init; }

    /// <summary>Standard deviation of the per-round medians, in milliseconds.</summary>
    public required double StandardDeviation { get; init; }

    /// <summary>
    /// Average median divided by its standard deviation - upstream's "am/s". A high
    /// number means the latency that is there is at least steady.
    /// </summary>
    public required double SignalToNoise { get; init; }

    /// <summary>
    /// Mean jitter across the period, in milliseconds. Where the standard deviation
    /// describes how the median moved between rounds, this describes variation
    /// between the probes inside each round.
    /// </summary>
    public required double Jitter { get; init; }

    public required double LossAverage { get; init; }

    public required double LossMaximum { get; init; }

    public required double LossMinimum { get; init; }

    public required double LossNow { get; init; }

    /// <summary>Rounds that produced at least one response.</summary>
    public required int RoundsWithData { get; init; }

    public static GraphStatistics Compute(IReadOnlyList<Sample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var medians = new List<double>();
        var jitters = new List<double>();
        var losses = new List<double>();
        var lastMedian = double.NaN;
        var lastLoss = double.NaN;

        foreach (var sample in samples)
        {
            if (sample.Sent == 0)
            {
                continue;
            }

            var loss = sample.LossFraction * 100;
            losses.Add(loss);
            lastLoss = loss;

            if (sample.JitterMilliseconds is { } jitter)
            {
                jitters.Add(jitter);
            }

            if (sample.Median is { } median)
            {
                medians.Add(median);
                lastMedian = median;
            }
        }

        if (medians.Count == 0)
        {
            return new GraphStatistics
            {
                HasData = false,
                MedianAverage = 0,
                MedianMaximum = 0,
                MedianMinimum = 0,
                MedianNow = 0,
                StandardDeviation = 0,
                SignalToNoise = 0,
                Jitter = 0,
                LossAverage = losses.Count == 0 ? 0 : losses.Average(),
                LossMaximum = losses.Count == 0 ? 0 : losses.Max(),
                LossMinimum = losses.Count == 0 ? 0 : losses.Min(),
                LossNow = double.IsNaN(lastLoss) ? 0 : lastLoss,
                RoundsWithData = 0,
            };
        }

        var mean = medians.Average();
        var variance = medians.Sum(m => (m - mean) * (m - mean)) / medians.Count;
        var standardDeviation = Math.Sqrt(variance);

        return new GraphStatistics
        {
            HasData = true,
            MedianAverage = mean,
            MedianMaximum = medians.Max(),
            MedianMinimum = medians.Min(),
            MedianNow = lastMedian,
            StandardDeviation = standardDeviation,

            // A perfectly steady link has no noise to divide by.
            SignalToNoise = standardDeviation > 0 ? mean / standardDeviation : 0,
            Jitter = jitters.Count == 0 ? 0 : jitters.Average(),
            LossAverage = losses.Count == 0 ? 0 : losses.Average(),
            LossMaximum = losses.Count == 0 ? 0 : losses.Max(),
            LossMinimum = losses.Count == 0 ? 0 : losses.Min(),
            LossNow = double.IsNaN(lastLoss) ? 0 : lastLoss,
            RoundsWithData = medians.Count,
        };
    }
}
