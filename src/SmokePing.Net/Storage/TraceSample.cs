namespace SmokePing.Net.Storage;

/// <summary>
/// One point of the second-by-second trace.
///
/// At the recording resolution a point is a single probe, so <see cref="Minimum"/>,
/// <see cref="Mean"/> and <see cref="Maximum"/> are the same number. Coarser tiers
/// summarise many probes, and there the three differ - which is the whole point of
/// keeping them: a one-second spike inside a minute survives as the maximum, where an
/// average would have swallowed it.
/// </summary>
/// <param name="Timestamp">Unix seconds at the start of the point.</param>
/// <param name="Sent">Probes the point covers.</param>
/// <param name="Lost">How many of them went unanswered.</param>
/// <param name="Minimum">Fastest answer in milliseconds, NaN when none came back.</param>
/// <param name="Mean">Mean answer in milliseconds, NaN when none came back.</param>
/// <param name="Maximum">Slowest answer in milliseconds, NaN when none came back.</param>
/// <param name="Jitter">Mean variation between consecutive answers, NaN when unknown.</param>
/// <param name="JitterMaximum">Largest such variation, NaN when unknown.</param>
public readonly record struct TraceSample(
    long Timestamp,
    int Sent,
    int Lost,
    float Minimum,
    float Mean,
    float Maximum,
    float Jitter,
    float JitterMaximum)
{
    /// <summary>True when the point covers at least one probe.</summary>
    public bool HasData => Sent > 0;

    /// <summary>True when at least one probe was answered.</summary>
    public bool HasAnswer => Sent > Lost && !float.IsNaN(Mean);

    /// <summary>A point covering no probes at all: a gap, not a loss.</summary>
    public static TraceSample Empty(long timestamp) => new(
        timestamp, 0, 0, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN);
}
