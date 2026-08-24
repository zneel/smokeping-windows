using System.Collections.Concurrent;
using System.Text;
using SmokePing.Net.Configuration;

namespace SmokePing.Net.Storage;

/// <summary>
/// Owns one <see cref="RoundRobinFile"/> per measured target and maps target
/// identifiers onto safe paths below the data directory.
/// </summary>
public sealed class DataStore : IDisposable
{
    /// <summary>
    /// Resolution tiers created for every target, as (multiple of the polling step, slots).
    /// These are upstream's default RRA rows, so retention matches an existing SmokePing
    /// installation: with the default 300s step, 100 days at full resolution, 400 days
    /// at one hour and 1200 days at twelve hours - about 2.5 MB per target, forever.
    ///
    /// Upstream also creates separate MIN and MAX archives for the coarser tiers, but
    /// none of its own graphs ever read them - every DEF in the original fetches with
    /// AVERAGE - so they are not reproduced here.
    /// </summary>
    public static readonly (int Multiplier, int SlotCount)[] ArchivePlan =
    [
        (1, 28800),
        (12, 9600),
        (144, 2400),
    ];

    private readonly (int Multiplier, int SlotCount)[] _archivePlan;

    private readonly ConcurrentDictionary<string, RoundRobinFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dataDirectory;

    /// <summary>
    /// Creates a store. <paramref name="archivePlan"/> overrides the default retention,
    /// which is how a native configuration's own archive table is honoured.
    /// </summary>
    public DataStore(string dataDirectory, IReadOnlyList<(int Multiplier, int SlotCount)>? archivePlan = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(_dataDirectory);

        _archivePlan = archivePlan is { Count: > 0 } ? [.. archivePlan] : ArchivePlan;

        if (_archivePlan[0].Multiplier != 1)
        {
            throw new ArgumentException("The first archive must run at the polling step.", nameof(archivePlan));
        }
    }

    /// <summary>The resolution tiers this store creates.</summary>
    public IReadOnlyList<(int Multiplier, int SlotCount)> Archives => _archivePlan;

    public string DataDirectory => _dataDirectory;

    /// <summary>
    /// Returns the database for a target, creating or opening the backing file on
    /// first use. Files are cached for the lifetime of the store.
    /// </summary>
    public RoundRobinFile GetOrOpen(MeasuredTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return _files.GetOrAdd(
            target.Id,
            static (_, state) => RoundRobinFile.OpenOrCreate(
                state.Store.ResolvePath(state.Target.Id),
                state.Target.StepSeconds,
                state.Target.Pings,
                state.Store._archivePlan),
            (Store: this, Target: target));
    }

    /// <summary>Maps a slash-separated target id to a file below the data directory.</summary>
    public string ResolvePath(string targetId)
    {
        var segments = targetId.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitiseSegment)
            .ToArray();

        if (segments.Length == 0)
        {
            throw new ArgumentException("Target id must contain at least one path segment.", nameof(targetId));
        }

        return Path.Combine([_dataDirectory, .. segments[..^1], segments[^1] + ".spd"]);
    }

    /// <summary>
    /// Strips anything that is not safe in a file name. Target ids are already
    /// validated at configuration load time; this is defence in depth against
    /// path traversal from a hand-edited configuration file.
    /// </summary>
    private static string SanitiseSegment(string segment)
    {
        var builder = new StringBuilder(segment.Length);
        foreach (var c in segment)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }

        var result = builder.ToString().Trim('.');
        return result.Length == 0 ? "_" : result;
    }

    public void Dispose()
    {
        foreach (var file in _files.Values)
        {
            file.Dispose();
        }

        _files.Clear();
    }
}
