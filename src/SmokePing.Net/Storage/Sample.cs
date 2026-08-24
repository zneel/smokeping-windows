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

    /// <summary>Index into <see cref="Quantiles"/> holding the median.</summary>
    public const int MedianIndex = 5;

    public required long Timestamp { get; init; }

    public required int Sent { get; init; }

    public required int Lost { get; init; }

    /// <summary>Round-trip times in milliseconds at 0%, 10%, ... 100%.</summary>
    public required float[] Quantiles { get; init; }

    /// <summary>Median round-trip time in milliseconds, or null when the round was a total loss.</summary>
    public double? Median => float.IsNaN(Quantiles[MedianIndex]) ? null : Quantiles[MedianIndex];

    /// <summary>Fraction of probes lost, 0.0 - 1.0.</summary>
    public double LossFraction => Sent == 0 ? 0 : (double)Lost / Sent;

    public static Sample Empty(long timestamp) => new()
    {
        Timestamp = timestamp,
        Sent = 0,
        Lost = 0,
        Quantiles = CreateNaNQuantiles(),
    };

    public static float[] CreateNaNQuantiles()
    {
        var q = new float[QuantileCount];
        Array.Fill(q, float.NaN);
        return q;
    }
}
