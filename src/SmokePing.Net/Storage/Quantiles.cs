namespace SmokePing.Net.Storage;

/// <summary>
/// Turns the raw round-trip times of a probe round into the fixed-size record that
/// gets persisted: a median and a quantile vector describing the spread.
///
/// The original stores one data source per probe (ping1..pingN), holding the received
/// round-trip times sorted ascending and <em>centred</em> in the vector, with the lost
/// probes written as UNKNOWN at both ends. That padding is not incidental: it is what
/// makes the smoke band narrow as loss rises, because the outer bands have an unknown
/// endpoint and are not drawn at all.
///
/// Storing eleven fixed quantiles instead keeps every record the same size, and
/// sampling them from the same padded vector preserves that behaviour.
/// </summary>
public static class Quantiles
{
    /// <summary>
    /// Computes sent/lost counts, the median and the quantile vector for one round.
    /// Null entries in <paramref name="roundTripTimes"/> represent lost probes.
    /// </summary>
    public static (int Sent, int Lost, float Median, float[] Quantiles) Compute(
        IReadOnlyList<double?> roundTripTimes)
    {
        ArgumentNullException.ThrowIfNull(roundTripTimes);

        var received = new List<double>(roundTripTimes.Count);
        foreach (var rtt in roundTripTimes)
        {
            if (rtt is { } value && !double.IsNaN(value))
            {
                received.Add(value);
            }
        }

        received.Sort();

        var sent = roundTripTimes.Count;
        var lost = sent - received.Count;
        return (sent, lost, Median(received), FromSamples(received, sent));
    }

    /// <summary>
    /// The median of a round, matching the original exactly: the middle value of the
    /// received probes by position, without interpolation. For twenty probes that is
    /// the eleventh smallest, not the mean of the tenth and eleventh.
    /// </summary>
    public static float Median(List<double> received)
    {
        ArgumentNullException.ThrowIfNull(received);

        if (received.Count == 0)
        {
            return float.NaN;
        }

        received.Sort();
        return (float)received[received.Count / 2];
    }

    /// <summary>
    /// Builds the quantile vector from the probes that came back, spread across a
    /// round of <paramref name="sent"/> slots the way the original lays them out:
    /// half the lost probes at the low end, the rest at the high end, received values
    /// in the middle. Quantiles that fall on a lost slot are NaN, so the corresponding
    /// smoke band is not drawn.
    /// </summary>
    public static float[] FromSamples(List<double> received, int sent)
    {
        ArgumentNullException.ThrowIfNull(received);

        var quantiles = Sample.CreateNaNQuantiles();
        if (received.Count == 0 || sent <= 0)
        {
            return quantiles;
        }

        received.Sort();

        // The padded round: index 0..sent-1, with the received values occupying
        // [leading, leading + received.Count).
        var lost = Math.Max(sent - received.Count, 0);
        var leading = lost / 2;

        for (var i = 0; i < Sample.QuantileCount; i++)
        {
            // Nearest-rank rather than interpolated, because the original's bands are
            // drawn from individual probes rather than from a fitted distribution.
            var fraction = (double)i / (Sample.QuantileCount - 1);
            var slot = Math.Min((int)(fraction * sent), sent - 1);
            var offset = slot - leading;

            if (offset >= 0 && offset < received.Count)
            {
                quantiles[i] = (float)received[offset];
            }
        }

        return quantiles;
    }

    /// <summary>
    /// Builds a quantile vector from a set of measurements with no losses. Convenience
    /// for callers that only have the received values.
    /// </summary>
    public static float[] FromSamples(List<double> received)
    {
        ArgumentNullException.ThrowIfNull(received);
        return FromSamples(received, received.Count);
    }

    /// <summary>
    /// Averages several quantile vectors, ignoring unknown entries per position.
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

    /// <summary>Averages the medians of several rounds, ignoring rounds that had none.</summary>
    public static float AverageMedian(IEnumerable<float> medians)
    {
        var total = 0.0;
        var count = 0;

        foreach (var median in medians)
        {
            if (!float.IsNaN(median))
            {
                total += median;
                count++;
            }
        }

        return count == 0 ? float.NaN : (float)(total / count);
    }
}
