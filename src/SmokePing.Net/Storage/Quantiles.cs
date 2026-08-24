namespace SmokePing.Net.Storage;

/// <summary>
/// Turns the raw round-trip times of a probe round into the fixed quantile
/// vector that gets persisted. Storing quantiles rather than individual pings
/// keeps every record the same size (which is what makes the round-robin file
/// possible) while preserving the distribution the "smoke" band is drawn from.
/// </summary>
public static class Quantiles
{
    /// <summary>
    /// Computes sent/lost counts and the quantile vector for one round.
    /// Null entries in <paramref name="roundTripTimes"/> represent lost probes.
    /// </summary>
    public static (int Sent, int Lost, float[] Quantiles) Compute(IReadOnlyList<double?> roundTripTimes)
    {
        ArgumentNullException.ThrowIfNull(roundTripTimes);

        var successes = new List<double>(roundTripTimes.Count);
        foreach (var rtt in roundTripTimes)
        {
            if (rtt is { } value && !double.IsNaN(value))
            {
                successes.Add(value);
            }
        }

        var sent = roundTripTimes.Count;
        var lost = sent - successes.Count;
        return (sent, lost, FromSamples(successes));
    }

    /// <summary>
    /// Computes the quantile vector from a set of successful measurements.
    /// Returns all-NaN when the set is empty.
    /// </summary>
    public static float[] FromSamples(List<double> successes)
    {
        var quantiles = Sample.CreateNaNQuantiles();
        if (successes.Count == 0)
        {
            return quantiles;
        }

        successes.Sort();
        for (var i = 0; i < Sample.QuantileCount; i++)
        {
            var fraction = (double)i / (Sample.QuantileCount - 1);
            quantiles[i] = (float)Interpolate(successes, fraction);
        }

        return quantiles;
    }

    /// <summary>Linear-interpolated quantile of a pre-sorted list.</summary>
    private static double Interpolate(List<double> sorted, double fraction)
    {
        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        var position = fraction * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var weight = position - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * weight);
    }

    /// <summary>
    /// Averages several quantile vectors, ignoring all-NaN (total loss) rows.
    /// This is the consolidation function used to build the coarser archives.
    /// </summary>
    public static float[] Average(IEnumerable<float[]> vectors)
    {
        var sums = new double[Sample.QuantileCount];
        var counts = new int[Sample.QuantileCount];

        foreach (var vector in vectors)
        {
            for (var i = 0; i < Sample.QuantileCount; i++)
            {
                if (!float.IsNaN(vector[i]))
                {
                    sums[i] += vector[i];
                    counts[i]++;
                }
            }
        }

        var result = Sample.CreateNaNQuantiles();
        for (var i = 0; i < Sample.QuantileCount; i++)
        {
            if (counts[i] > 0)
            {
                result[i] = (float)(sums[i] / counts[i]);
            }
        }

        return result;
    }
}
