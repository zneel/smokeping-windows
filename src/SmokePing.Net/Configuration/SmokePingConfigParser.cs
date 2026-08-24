using System.Text;

namespace SmokePing.Net.Configuration;

/// <summary>
/// One section of a SmokePing configuration file: its variables, its table rows, and
/// the subsections introduced by <c>+</c>, <c>++</c> and so on.
/// </summary>
public sealed class ConfigSection
{
    public ConfigSection(string name, int line)
    {
        Name = name;
        Line = line;
    }

    /// <summary>Section name, without the leading pluses.</summary>
    public string Name { get; }

    /// <summary>Line the section was declared on, for error messages.</summary>
    public int Line { get; }

    public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Rows that are not key/value pairs. The Database section uses these for the
    /// archive table: "AVERAGE 0.5 1 28800".
    /// </summary>
    public List<string[]> Rows { get; } = [];

    /// <summary>Subsections, in the order they appear.</summary>
    public List<ConfigSection> Children { get; } = [];

    public string? Get(string key) => Variables.TryGetValue(key, out var value) ? value : null;
}

/// <summary>
/// Reads SmokePing's own configuration format - the one an existing installation
/// already has - rather than requiring it to be translated to JSON first.
///
/// The format is Config::Grammar: <c>*** Section ***</c> headers, <c>key = value</c>
/// pairs with backslash continuation, <c>#</c> comments, and a target hierarchy built
/// from lines beginning with one or more <c>+</c>. Bare rows that are not assignments
/// are collected too, because the Database section describes its archives that way.
/// </summary>
public static class SmokePingConfigParser
{
    /// <summary>
    /// True when the file looks like a SmokePing configuration rather than JSON.
    /// Decided on the first line that carries anything, so a leading comment block or
    /// blank lines do not confuse it.
    /// </summary>
    public static bool LooksLikeNativeFormat(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();

            // Both comment styles are skipped: '#' is the native one, and the JSON
            // reader here accepts '//', so neither should decide the format.
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            return !line.StartsWith('{');
        }

        return false;
    }

    /// <summary>
    /// Parses a configuration file into its sections. <paramref name="path"/> is used
    /// to resolve <c>@include</c> directives and to describe errors.
    /// </summary>
    /// <exception cref="ConfigurationException">The file cannot be parsed.</exception>
    public static IReadOnlyList<ConfigSection> Parse(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllLines(path), path, depth: 0);
    }

    /// <summary>Parses already-read lines. Exposed for tests.</summary>
    public static IReadOnlyList<ConfigSection> Parse(IReadOnlyList<string> lines, string path, int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (depth > 10)
        {
            throw new ConfigurationException($"'{path}': @include nested more than ten deep; is there a loop?");
        }

        var sections = new List<ConfigSection>();
        ConfigSection? section = null;

        // A section's subsections, indexed by their depth, so "++" attaches to the
        // most recent "+".
        var open = new List<ConfigSection>();

        foreach (var (text, number) in Join(lines, path))
        {
            var line = text.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("@include", StringComparison.Ordinal))
            {
                var included = IncludePath(line, path, number);
                foreach (var parsed in Parse(included))
                {
                    sections.Add(parsed);
                }

                continue;
            }

            if (line.StartsWith("***", StringComparison.Ordinal))
            {
                section = new ConfigSection(ParseSectionName(line, path, number), number);
                sections.Add(section);
                open.Clear();
                continue;
            }

            if (section is null)
            {
                throw new ConfigurationException($"'{path}' line {number}: content before the first *** section ***.");
            }

            if (line.StartsWith('+'))
            {
                var level = 0;
                while (level < line.Length && line[level] == '+')
                {
                    level++;
                }

                var name = line[level..].Trim();
                if (name.Length == 0)
                {
                    throw new ConfigurationException($"'{path}' line {number}: a subsection needs a name.");
                }

                var child = new ConfigSection(name, number);

                // level 1 hangs off the section, level n off the level n-1 above it.
                if (level - 1 > open.Count)
                {
                    throw new ConfigurationException(
                        $"'{path}' line {number}: '{new string('+', level)} {name}' is nested too deep for what precedes it.");
                }

                if (level == 1)
                {
                    section.Children.Add(child);
                }
                else
                {
                    open[level - 2].Children.Add(child);
                }

                open.RemoveRange(level - 1, open.Count - (level - 1));
                open.Add(child);
                continue;
            }

            var target = open.Count > 0 ? open[^1] : section;
            var equals = line.IndexOf('=', StringComparison.Ordinal);

            if (equals < 0)
            {
                // Not an assignment: a table row, as the Database section uses.
                target.Rows.Add(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();

            if (key.Length == 0)
            {
                throw new ConfigurationException($"'{path}' line {number}: assignment with no name.");
            }

            target.Variables[key] = value;
        }

        return sections;
    }

    /// <summary>
    /// Joins backslash-continued lines, keeping the number of the line the statement
    /// started on so errors point at something useful.
    /// </summary>
    private static IEnumerable<(string Text, int Number)> Join(IReadOnlyList<string> lines, string path)
    {
        var builder = new StringBuilder();
        var start = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            if (builder.Length == 0)
            {
                start = i + 1;
            }

            if (line.EndsWith('\\'))
            {
                builder.Append(line.AsSpan(0, line.Length - 1)).Append(' ');
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(line);
                yield return (builder.ToString(), start);
                builder.Clear();
                continue;
            }

            yield return (line, start);
        }

        if (builder.Length > 0)
        {
            throw new ConfigurationException($"'{path}' line {start}: the file ends with a continuation.");
        }
    }

    private static string ParseSectionName(string line, string path, int number)
    {
        var trimmed = line.Trim().Trim('*').Trim();
        if (trimmed.Length == 0)
        {
            throw new ConfigurationException($"'{path}' line {number}: a section needs a name.");
        }

        return trimmed;
    }

    private static string IncludePath(string line, string path, int number)
    {
        var name = line["@include".Length..].Trim();
        if (name.Length == 0)
        {
            throw new ConfigurationException($"'{path}' line {number}: @include needs a file name.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var resolved = Path.IsPathRooted(name) ? name : Path.Combine(directory, name);

        if (!File.Exists(resolved))
        {
            throw new ConfigurationException($"'{path}' line {number}: included file '{resolved}' does not exist.");
        }

        return resolved;
    }
}
