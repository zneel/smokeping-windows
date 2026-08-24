namespace SmokePing.Net.Storage;

/// <summary>
/// One measurement round for a single target: how many probes were sent, how many
/// were lost, and the round-trip time distribution expressed as evenly spaced
/// quantiles (0%, 10%, ... 100%). Quantiles are <see cref="float.NaN"/> when every
/// probe in the round was lost.
/// </summary>
public sealed class Sample
{
    /// <summary>Number of quantiles stored per sample (0%, 10%, ..., 100%).</summary>
    public const int QuantileCount = 11;

    public required long Timestamp { get; init; }

    public required int Sent { get; init; }

    public required int Lost { get; init; }

    /// <summary>
    /// Round-trip times in milliseconds across the round, at 0%, 10%, ... 100%.
    /// Entries that fall on a lost probe are <see cref="float.NaN"/>, which is what
    /// makes the smoke band narrow as loss rises.
    /// </summary>
    public required float[] Quantiles { get; init; }

    /// <summary>
    /// Median round-trip time in milliseconds, or <see cref="float.NaN"/> when nothing
    /// came back. Stored in its own right rather than read out of the quantile vector,
    /// because the original keeps it as a separate data source computed only from the
    /// probes that were received.
    /// </summary>
    public float MedianValue { get; init; } = float.NaN;

    /// <summary>
    /// Mean absolute variation between consecutive probes, in milliseconds, or
    /// <see cref="float.NaN"/> when fewer than two probes came back. See
    /// <see cref="Storage.Jitter"/> for why this is stored rather than derived.
    /// </summary>
    public float Jitter { get; init; } = float.NaN;

    /// <summary>Median round-trip time in milliseconds, or null when the round was a total loss.</summary>
    public double? Median => float.IsNaN(MedianValue) ? null : MedianValue;

    /// <summary>Fraction of probes lost, 0.0 - 1.0.</summary>
    public double LossFraction => Sent == 0 ? 0 : (double)Lost / Sent;

    /// <summary>Jitter in milliseconds, or null when the round did not produce one.</summary>
    public double? JitterMilliseconds => float.IsNaN(Jitter) ? null : Jitter;

    public static Sample Empty(long timestamp) => new()
    {
        Timestamp = timestamp,
        Sent = 0,
        Lost = 0,
        Quantiles = CreateNaNQuantiles(),
        MedianValue = float.NaN,
        Jitter = float.NaN,
    };

    public static float[] CreateNaNQuantiles()
    {
        var q = new float[QuantileCount];
        Array.Fill(q, float.NaN);
        return q;
    }
}
