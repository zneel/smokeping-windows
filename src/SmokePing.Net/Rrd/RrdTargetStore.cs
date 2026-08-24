using SmokePing.Net.Configuration;
using SmokePing.Net.Storage;

namespace SmokePing.Net.Rrd;

/// <summary>
/// Stores measurements as RRDtool files laid out exactly as SmokePing lays them out,
/// so an existing installation's <c>.rrd</c> files are read and written in place and
/// <c>rrdtool</c> still understands everything this writes.
///
/// The schema is upstream's: uptime, loss and median, then one data source per probe.
/// The probes are written sorted with the lost ones as unknown at both ends, which is
/// what makes the smoke band narrow as loss rises.
/// </summary>
public sealed class RrdTargetStore : IDisposable
{
    private readonly Dictionary<string, RrdFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly string _dataDirectory;
    private readonly IReadOnlyList<RrdArchive> _archives;

    public RrdTargetStore(string dataDirectory, IReadOnlyList<(int Steps, int Rows)>? archivePlan = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        _dataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(_dataDirectory);

        var plan = archivePlan is { Count: > 0 }
            ? archivePlan
            : [(1, 28800), (12, 9600), (144, 2400)];

        _archives = [.. plan.Select(a => new RrdArchive("AVERAGE", a.Rows, a.Steps))];
    }

    public string DataDirectory => _dataDirectory;

    /// <summary>Path of a target's database, using upstream's own naming.</summary>
    public string ResolvePath(string targetId)
    {
        var segments = targetId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new ArgumentException("Target id must have at least one segment.", nameof(targetId));
        }

        return Path.Combine([_dataDirectory, .. segments[..^1], segments[^1] + ".rrd"]);
    }

    /// <summary>
    /// The data sources upstream creates: uptime, loss, median and one per probe. The
    /// heartbeat is twice the step, so a missed round is unknown rather than stretched
    /// across the gap.
    /// </summary>
    public static IReadOnlyList<RrdDataSource> SchemaFor(int pings, int stepSeconds)
    {
        var heartbeat = 2L * stepSeconds;
        var sources = new List<RrdDataSource>(pings + 3)
        {
            RrdDataSource.Gauge("uptime", heartbeat, 0),
            RrdDataSource.Gauge("loss", heartbeat, 0, pings),
            RrdDataSource.Gauge("median", heartbeat, 0),
        };

        for (var i = 1; i <= pings; i++)
        {
            sources.Add(RrdDataSource.Gauge($"ping{i}", heartbeat, 0));
        }

        return sources;
    }

    /// <summary>Opens a target's database, creating it when it does not exist yet.</summary>
    public RrdFile GetOrOpen(MeasuredTarget target, long now)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_gate)
        {
            if (_files.TryGetValue(target.Id, out var cached))
            {
                return cached;
            }

            var path = ResolvePath(target.Id);
            RrdFile file;

            if (File.Exists(path))
            {
                file = RrdFile.Read(path);

                if (file.StepSeconds != target.StepSeconds)
                {
                    throw new RrdFormatException(
                        $"'{path}' has a {file.StepSeconds} second step but '{target.Id}' is configured for " +
                        $"{target.StepSeconds}. rrdtool cannot change a step in place; use rrdtool tune or " +
                        "move the file aside.");
                }
            }
            else
            {
                file = RrdFile.Create(
                    target.StepSeconds,
                    SchemaFor(target.Pings, target.StepSeconds),
                    _archives,

                    // One step back, so the first round is the first update.
                    now - target.StepSeconds);
            }

            _files[target.Id] = file;
            return file;
        }
    }

    /// <summary>
    /// Records one round. <paramref name="roundTripTimes"/> is in send order; lost
    /// probes are null.
    /// </summary>
    public void Write(MeasuredTarget target, long timestamp, IReadOnlyList<double?> roundTripTimes)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(roundTripTimes);

        var file = GetOrOpen(target, timestamp);

        lock (_gate)
        {
            file.Update(timestamp, BuildValues(target.Pings, roundTripTimes));
            file.Write(ResolvePath(target.Id));
        }
    }

    /// <summary>
    /// Lays a round out the way upstream does: uptime, the loss count, the median, and
    /// then the received round-trip times sorted and centred among the lost ones.
    /// </summary>
    public static double[] BuildValues(int pings, IReadOnlyList<double?> roundTripTimes)
    {
        var received = roundTripTimes
            .Where(rtt => rtt is { } value && !double.IsNaN(value))
            .Select(rtt => rtt!.Value)
            .Order()
            .ToList();

        var lost = pings - received.Count;
        var values = new double[pings + 3];

        // Uptime is upstream's per-target link uptime, which is not tracked here.
        values[0] = double.NaN;
        values[1] = lost;
        values[2] = received.Count == 0 ? double.NaN : received[received.Count / 2];

        for (var i = 0; i < pings; i++)
        {
            values[i + 3] = double.NaN;
        }

        // int(loss/2) unknowns first, then the received values, then the rest.
        var leading = lost / 2;
        for (var i = 0; i < received.Count; i++)
        {
            values[3 + leading + i] = received[i];
        }

        return values;
    }

    /// <summary>
    /// Reads a period back as the samples the rest of the application works with.
    /// </summary>
    public IReadOnlyList<Sample> Read(MeasuredTarget target, long from, long to)
    {
        ArgumentNullException.ThrowIfNull(target);

        var file = GetOrOpen(target, to);
        var archive = SelectArchive(file, to - from);
        var rows = file.ReadArchive(archive);
        var samples = new List<Sample>();

        foreach (var row in rows)
        {
            if (row.Timestamp < from || row.Timestamp > to)
            {
                continue;
            }

            samples.Add(ToSample(row, target.Pings));
        }

        return samples;
    }

    /// <summary>Picks the finest archive that covers the requested span.</summary>
    public static int SelectArchive(RrdFile file, long spanSeconds)
    {
        for (var i = 0; i < file.Archives.Count; i++)
        {
            if (file.RowStep(i) * file.Archives[i].RowCount >= spanSeconds)
            {
                return i;
            }
        }

        return file.Archives.Count - 1;
    }

    /// <summary>Turns an RRD row back into a sample, recovering the quantile vector.</summary>
    public static Sample ToSample(RrdRow row, int pings)
    {
        var lost = double.IsNaN(row.Values[1]) ? 0 : (int)Math.Round(row.Values[1]);
        var probes = row.Values.Skip(3).Take(pings).ToArray();
        var quantiles = Sample.CreateNaNQuantiles();

        if (probes.Length > 0)
        {
            for (var i = 0; i < Sample.QuantileCount; i++)
            {
                var fraction = (double)i / (Sample.QuantileCount - 1);
                var slot = Math.Min((int)(fraction * probes.Length), probes.Length - 1);
                quantiles[i] = (float)probes[slot];
            }
        }

        var anyData = !double.IsNaN(row.Values[1]) || probes.Any(p => !double.IsNaN(p));

        return new Sample
        {
            Timestamp = row.Timestamp,
            Sent = anyData ? pings : 0,
            Lost = Math.Clamp(lost, 0, pings),
            Quantiles = quantiles,
            MedianValue = (float)row.Values[2],
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _files.Clear();
        }
    }
}
