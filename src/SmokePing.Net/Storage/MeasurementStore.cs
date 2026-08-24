using SmokePing.Net.Configuration;
using SmokePing.Net.Rrd;

namespace SmokePing.Net.Storage;

/// <summary>
/// The one place that knows which storage format is in use.
///
/// Both formats are real options, so the rest of the application should not have to
/// branch on which one is configured - and it should certainly not have to ask the
/// container for a service that may not have been registered, which is how an
/// optional dependency turns into a request that fails at run time.
/// </summary>
public sealed class MeasurementStore : IDisposable
{
    private readonly DataStore? _dataStore;
    private readonly RrdTargetStore? _rrdStore;

    public MeasurementStore(LoadedConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var plan = config.Raw.Database.Archives
            .Where(archive => archive.Steps > 0 && archive.Rows > 0)
            .Select(archive => (archive.Steps, archive.Rows))
            .ToList();

        if (config.Raw.Database.Format.Equals("rrd", StringComparison.OrdinalIgnoreCase))
        {
            _rrdStore = new RrdTargetStore(config.DataDirectory, plan);
            Format = "rrd";
        }
        else if (config.Raw.Database.Format.Equals("spd", StringComparison.OrdinalIgnoreCase))
        {
            _dataStore = new DataStore(config.DataDirectory, plan);
            Format = "spd";
        }
        else
        {
            throw new ConfigurationException(
                $"Unknown database format '{config.Raw.Database.Format}'. Use \"spd\" or \"rrd\".");
        }

        DataDirectory = config.DataDirectory;
    }

    public string Format { get; }

    public string DataDirectory { get; }

    /// <summary>
    /// Opens a target's database up front, so a permissions or disk problem is
    /// reported at start-up rather than at that target's first round hours later.
    /// </summary>
    public void Open(MeasuredTarget target, long now)
    {
        if (_rrdStore is not null)
        {
            _rrdStore.GetOrOpen(target, now);
        }
        else
        {
            _dataStore!.GetOrOpen(target);
        }
    }

    /// <summary>Records one round. Round-trip times are in send order; lost probes are null.</summary>
    public void Write(MeasuredTarget target, long timestamp, IReadOnlyList<double?> roundTripTimes)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(roundTripTimes);

        if (_rrdStore is not null)
        {
            _rrdStore.Write(target, timestamp, roundTripTimes);
            return;
        }

        var (sent, lost, median, quantiles) = Quantiles.Compute(roundTripTimes);
        _dataStore!.GetOrOpen(target).Write(
            timestamp,
            sent,
            lost,
            quantiles,
            Jitter.Compute(roundTripTimes),
            median);
    }

    /// <summary>Reads a period, along with the resolution the samples came back at.</summary>
    public (IReadOnlyList<Sample> Samples, int StepSeconds) Read(MeasuredTarget target, long from, long to)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (_rrdStore is not null)
        {
            var file = _rrdStore.GetOrOpen(target, to);
            var archive = RrdTargetStore.SelectArchive(file, to - from);
            return (_rrdStore.Read(target, from, to), (int)file.RowStep(archive));
        }

        var database = _dataStore!.GetOrOpen(target);
        var index = database.SelectArchive(to - from);
        return (database.Read(index, from, to), database.Archives[index].StepSeconds);
    }

    public void Dispose()
    {
        _dataStore?.Dispose();
        _rrdStore?.Dispose();
    }
}
