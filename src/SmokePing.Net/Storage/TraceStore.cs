using System.Collections.Concurrent;
using SmokePing.Net.Configuration;

namespace SmokePing.Net.Storage;

/// <summary>
/// Owns one <see cref="TraceFile"/> per traced target, and turns the Trace section of
/// the configuration into the tier plan those files are built on.
/// </summary>
public sealed class TraceStore : IDisposable
{
    private readonly ConcurrentDictionary<string, TraceFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dataDirectory;
    private readonly (int Multiplier, int SlotCount)[] _plan;

    public TraceStore(LoadedConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var trace = config.Raw.Trace;
        _dataDirectory = config.DataDirectory;
        StepSeconds = trace.IntervalSeconds;
        Enabled = trace.Enabled || config.Targets.Any(t => t.Traced);

        var fineSlots = (int)(trace.FineHours * 3600L / trace.IntervalSeconds);
        var coarseMultiplier = trace.CoarseStepSeconds / trace.IntervalSeconds;
        var coarseSlots = (int)(trace.CoarseDays * 86400L / trace.CoarseStepSeconds);

        _plan = coarseMultiplier > 1
            ? [(1, fineSlots), (coarseMultiplier, coarseSlots)]
            : [(1, fineSlots)];

        BytesPerTarget = TraceFile.FileSize(_plan);
    }

    /// <summary>Seconds between recorded probes.</summary>
    public int StepSeconds { get; }

    /// <summary>True when anything at all is traced.</summary>
    public bool Enabled { get; }

    /// <summary>Disk one traced target costs, fixed for the life of the file.</summary>
    public long BytesPerTarget { get; }

    /// <summary>The tiers each trace file is built with.</summary>
    public IReadOnlyList<(int Multiplier, int SlotCount)> Plan => _plan;

    /// <summary>Opens a target's trace, creating the file on first use.</summary>
    public TraceFile GetOrOpen(MeasuredTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return _files.GetOrAdd(
            target.Id,
            static (id, state) => TraceFile.OpenOrCreate(
                TargetPaths.Resolve(state.Directory, id, ".sptr"),
                state.Step,
                state.Plan),
            (Directory: _dataDirectory, Step: StepSeconds, Plan: _plan));
    }

    /// <summary>Reads a period of a target's trace, at the finest resolution that covers it.</summary>
    public (IReadOnlyList<TraceSample> Samples, int StepSeconds) Read(MeasuredTarget target, long from, long to) =>
        GetOrOpen(target).Read(from, to);

    /// <summary>Pushes every open trace out to disk.</summary>
    public void Flush(bool toDisk)
    {
        foreach (var file in _files.Values)
        {
            file.Flush(toDisk);
        }
    }

    public void Dispose()
    {
        foreach (var file in _files.Values)
        {
            file.Flush(toDisk: true);
            file.Dispose();
        }

        _files.Clear();
    }
}
