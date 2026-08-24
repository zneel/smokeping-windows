using SmokePing.Net.Storage;

namespace SmokePing.Net.Graphing;

/// <summary>
/// Summary figures for a period: the numbers shown under a graph and the values the
/// "top N" charts are sorted by.
/// </summary>
public sealed class GraphStatistics
{
    public required bool HasData { get; init; }

    /// <summary>Median of the per-round medians, in milliseconds.</summary>
    public required double Median { get; init; }

    public required double Minimum { get; init; }

    public required double Maximum { get; init; }

    /// <summary>Standard deviation of the per-round medians, in milliseconds.</summary>
    public required double StandardDeviation { get; init; }

    public required double LossPercent { get; init; }

    /// <summary>Rounds that produced at least one response.</summary>
    public required int RoundsWithData { get; init; }

    public static GraphStatistics Compute(IReadOnlyList<Sample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var medians = new List<double>();
        var minimum = double.MaxValue;
        var maximum = double.MinValue;
        var sent = 0;
        var lost = 0;

        foreach (var sample in samples)
        {
            sent += sample.Sent;
            lost += sample.Lost;

            if (sample.Median is not { } median)
            {
                continue;
            }

            medians.Add(median);

            var low = sample.Quantiles[0];
            var high = sample.Quantiles[Sample.QuantileCount - 1];
            if (!float.IsNaN(low))
            {
                minimum = Math.Min(minimum, low);
            }

            if (!float.IsNaN(high))
            {
                maximum = Math.Max(maximum, high);
            }
        }

        if (medians.Count == 0)
        {
            return new GraphStatistics
            {
                HasData = false,
                Median = 0,
                Minimum = 0,
                Maximum = 0,
                StandardDeviation = 0,
                LossPercent = sent == 0 ? 0 : (double)lost / sent * 100,
                RoundsWithData = 0,
            };
        }

        medians.Sort();
        var mean = medians.Average();
        var variance = medians.Sum(m => (m - mean) * (m - mean)) / medians.Count;

        return new GraphStatistics
        {
            HasData = true,
            Median = medians[medians.Count / 2],
            Minimum = minimum,
            Maximum = maximum,
            StandardDeviation = Math.Sqrt(variance),
            LossPercent = sent == 0 ? 0 : (double)lost / sent * 100,
            RoundsWithData = medians.Count,
        };
    }
}
