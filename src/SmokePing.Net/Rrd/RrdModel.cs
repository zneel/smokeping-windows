namespace SmokePing.Net.Rrd;

/// <summary>A data source definition.</summary>
/// <param name="Name">Up to 19 characters of <c>[-a-zA-Z0-9_]</c>.</param>
/// <param name="Type">GAUGE, COUNTER, DERIVE, ABSOLUTE.</param>
/// <param name="HeartbeatSeconds">How long a value stays valid before the next is UNKNOWN.</param>
/// <param name="Minimum">Lower bound, NaN for unbounded.</param>
/// <param name="Maximum">Upper bound, NaN for unbounded.</param>
public sealed record RrdDataSource(
    string Name,
    string Type,
    long HeartbeatSeconds,
    double Minimum,
    double Maximum)
{
    public static RrdDataSource Gauge(string name, long heartbeat, double minimum = double.NaN, double maximum = double.NaN) =>
        new(name, "GAUGE", heartbeat, minimum, maximum);
}

/// <summary>A round robin archive definition.</summary>
/// <param name="ConsolidationFunction">AVERAGE, MIN, MAX or LAST.</param>
/// <param name="RowCount">Rows kept before the archive wraps.</param>
/// <param name="StepsPerRow">Primary data points consolidated into one row.</param>
/// <param name="XFilesFactor">
/// Fraction of a row's primary data points that may be unknown before the row itself is
/// unknown. RRDtool's default, and SmokePing's, is 0.5.
/// </param>
public sealed record RrdArchive(
    string ConsolidationFunction,
    int RowCount,
    int StepsPerRow,
    double XFilesFactor = 0.5);

/// <summary>One consolidated row: a timestamp and one value per data source.</summary>
public sealed record RrdRow(long Timestamp, double[] Values);
