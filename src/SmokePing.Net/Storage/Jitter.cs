namespace SmokePing.Net.Storage;

/// <summary>
/// Computes jitter for a measurement round.
///
/// Jitter here is mean absolute inter-packet delay variation: the average of the
/// absolute differences between the round-trip times of probes that came back, taken
/// in the order they were sent. This is the RFC 3550 delay variation without the
/// exponential smoothing, which suits a round of probes taken as one batch.
///
/// It has to be computed while the individual round-trip times are still in hand.
/// The stored quantiles describe the distribution of a round but not the order the
/// probes came back in, and jitter is a property of that order: a round that
/// alternates 10ms and 60ms and a round that rises smoothly from 10ms to 60ms have
/// the same quantiles and completely different jitter.
/// </summary>
public static class Jitter
{
    /// <summary>
    /// Returns the mean absolute difference between consecutive successful probes,
    /// or <see cref="float.NaN"/> when fewer than two probes came back.
    /// </summary>
    /// <param name="roundTripTimes">
    /// Round-trip times in send order; null entries are probes that were lost.
    /// </param>
    public static float Compute(IReadOnlyList<double?> roundTripTimes)
    {
        ArgumentNullException.ThrowIfNull(roundTripTimes);

        var total = 0.0;
        var pairs = 0;
        double? previous = null;

        foreach (var rtt in roundTripTimes)
        {
            if (rtt is not { } value || double.IsNaN(value))
            {
                // A lost probe breaks the pair, but the probes either side of it are
                // still consecutive arrivals, so the next one pairs with the last
                // that actually came back.
                continue;
            }

            if (previous is { } last)
            {
                total += Math.Abs(value - last);
                pairs++;
            }

            previous = value;
        }

        return pairs == 0 ? float.NaN : (float)(total / pairs);
    }

    /// <summary>
    /// Averages the jitter of several rounds, ignoring rounds that produced none.
    /// This is the consolidation function used to build the coarser archives.
    /// </summary>
    public static float Average(IEnumerable<float> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var total = 0.0;
        var count = 0;

        foreach (var value in values)
        {
            if (!float.IsNaN(value))
            {
                total += value;
                count++;
            }
        }

        return count == 0 ? float.NaN : (float)(total / count);
    }
}
