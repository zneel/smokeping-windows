using System.Text;

namespace SmokePing.Net.Storage;

/// <summary>
/// Maps a slash-separated target id onto a file below a data directory.
///
/// Shared by every store so a target's databases sit next to each other under the
/// same name, differing only in extension.
/// </summary>
public static class TargetPaths
{
    /// <summary>Builds the path for a target's database with the given extension.</summary>
    public static string Resolve(string dataDirectory, string targetId, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        var segments = targetId.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Sanitise)
            .ToArray();

        if (segments.Length == 0)
        {
            throw new ArgumentException("Target id must contain at least one path segment.", nameof(targetId));
        }

        return Path.Combine([dataDirectory, .. segments[..^1], segments[^1] + extension]);
    }

    /// <summary>
    /// Strips anything that is not safe in a file name. Target ids are already
    /// validated at configuration load time; this is defence in depth against
    /// path traversal from a hand-edited configuration file.
    /// </summary>
    private static string Sanitise(string segment)
    {
        var builder = new StringBuilder(segment.Length);
        foreach (var c in segment)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }

        var result = builder.ToString().Trim('.');
        return result.Length == 0 ? "_" : result;
    }
}
