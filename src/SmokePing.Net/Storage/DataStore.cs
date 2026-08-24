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
    /// With the default 300s step this is 14 days at full resolution, 60 days at 30
    /// minutes, one year at two hours and five years at one day - about 700 KB per target.
    /// </summary>
    private static readonly (int Multiplier, int SlotCount)[] ArchivePlan =
    [
        (1, 4032),
        (6, 2880),
        (24, 4380),
        (288, 1825),
    ];

    private readonly ConcurrentDictionary<string, RoundRobinFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dataDirectory;

    public DataStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(_dataDirectory);
    }

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
                ArchivePlan),
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
