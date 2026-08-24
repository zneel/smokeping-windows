using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SmokePing.Net.Alerting;

namespace SmokePing.Net.Configuration;

/// <summary>A validated configuration: the raw file plus everything derived from it.</summary>
public sealed class LoadedConfiguration
{
    public required SmokePingConfig Raw { get; init; }

    /// <summary>Every node in the tree that has a host, with inherited settings applied.</summary>
    public required IReadOnlyList<MeasuredTarget> Targets { get; init; }

    /// <summary>The menu tree shown by the web interface.</summary>
    public required IReadOnlyList<MenuNode> Menu { get; init; }

    public required IReadOnlyDictionary<string, CompiledAlertRule> Alerts { get; init; }

    /// <summary>Absolute path of the data directory.</summary>
    public required string DataDirectory { get; init; }

    public required string ConfigFilePath { get; init; }

    /// <summary>Targets that were dropped because this implementation cannot measure them.</summary>
    public IReadOnlyList<string> SkippedTargets { get; init; } = [];

    /// <summary>Configurations that will work but probably do not do what was meant.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    private Dictionary<string, MeasuredTarget>? _byId;

    public bool TryGetTarget(string id, out MeasuredTarget target)
    {
        _byId ??= Targets.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        return _byId.TryGetValue(id, out target!);
    }
}

/// <summary>Thrown when smokeping.json is missing, malformed or inconsistent.</summary>
public sealed class ConfigurationException : Exception
{
    public ConfigurationException(string message) : base(message)
    {
    }

    public ConfigurationException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Reads smokeping.json, applies the inheritance rules of the target tree and
/// validates everything up front, so a typo is reported at start-up rather than
/// silently producing an unmonitored host.
/// </summary>
public static class ConfigLoader
{
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,

        // A misspelled or mistranslated setting must be an error. Silently falling
        // back to the default is how a target ends up measured with settings nobody
        // chose, and nothing in the output would ever say so.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Settings applied when neither the target nor the defaults section specifies one.</summary>
    private static readonly TargetDefaults BuiltInDefaults = new()
    {
        Probe = "icmp",
        Step = 300,
        Pings = 20,
        PingIntervalMs = 500,
        PacketSize = 56,
        AlertRules = [],
    };

    /// <summary>
    /// Per-probe timeouts, applied when nothing in the configuration sets one. A single
    /// figure across all probes is wrong in both directions: upstream gives DNS five
    /// seconds and an HTTP fetch ten, and a shared 1.5s would report loss on sites that
    /// upstream measures perfectly well.
    /// </summary>
    private static int DefaultTimeoutMs(string probe) => probe.ToLowerInvariant() switch
    {
        "dns" => 5000,
        "http" => 10000,
        "tcp" => 5000,
        _ => 1500,
    };

    public static LoadedConfiguration Load(string configFilePath, bool skipUnsupported = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);

        var fullPath = Path.GetFullPath(configFilePath);
        if (!File.Exists(fullPath))
        {
            throw new ConfigurationException($"Configuration file '{fullPath}' does not exist.");
        }

        var text = File.ReadAllText(fullPath);

        // A SmokePing installation's own configuration is read directly, so migrating
        // does not start with translating a file by hand.
        if (SmokePingConfigParser.LooksLikeNativeFormat(text))
        {
            var sections = SmokePingConfigParser.Parse(fullPath);
            return Build(SmokePingConfigTranslator.Translate(sections, fullPath), fullPath, skipUnsupported);
        }

        SmokePingConfig? raw;
        try
        {
            raw = JsonSerializer.Deserialize<SmokePingConfig>(text, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ConfigurationException($"Configuration file '{fullPath}' is not valid JSON: {ex.Message}", ex);
        }

        if (raw is null)
        {
            throw new ConfigurationException($"Configuration file '{fullPath}' is empty.");
        }

        return Build(raw, fullPath, skipUnsupported);
    }

    /// <summary>Validates an already-parsed configuration. Exposed for tests.</summary>
    public static LoadedConfiguration Build(SmokePingConfig raw, string configFilePath) =>
        Build(raw, configFilePath, skipUnsupported: false);

    /// <summary>
    /// Validates a configuration.
    ///
    /// When <paramref name="skipUnsupported"/> is set, targets this implementation
    /// cannot measure are dropped with a note rather than refusing the whole file.
    /// That matters when adopting an existing installation's configuration, where one
    /// unsupported target among fifty should not stop the other forty-nine.
    /// </summary>
    public static LoadedConfiguration Build(SmokePingConfig raw, string configFilePath, bool skipUnsupported)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var alerts = CompileAlerts(raw);
        var targets = new List<MeasuredTarget>();
        var menu = new List<MenuNode>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var rootDefaults = Merge(BuiltInDefaults, raw.Defaults);
        var skipped = new List<string>();
        var warnings = new List<string>();

        foreach (var node in raw.Targets)
        {
            var walked = Walk(node, string.Empty, rootDefaults, targets, alerts, seenIds, skipUnsupported, skipped, warnings);
            if (walked is not null)
            {
                menu.Add(walked);
            }
        }

        if (targets.Count == 0)
        {
            throw new ConfigurationException("No targets with a host were found; there would be nothing to measure.");
        }

        var configDirectory = Path.GetDirectoryName(configFilePath) ?? Directory.GetCurrentDirectory();
        var dataDirectory = Path.IsPathRooted(raw.General.DataDirectory)
            ? raw.General.DataDirectory
            : Path.Combine(configDirectory, raw.General.DataDirectory);

        return new LoadedConfiguration
        {
            Raw = raw,
            Targets = targets,
            Menu = menu,
            Alerts = alerts,
            DataDirectory = Path.GetFullPath(dataDirectory),
            ConfigFilePath = configFilePath,
            SkippedTargets = skipped,
            Warnings = warnings,
        };
    }

    private static IReadOnlyDictionary<string, CompiledAlertRule> CompileAlerts(SmokePingConfig raw)
    {
        var alerts = new Dictionary<string, CompiledAlertRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var alert in raw.Alerts)
        {
            if (string.IsNullOrWhiteSpace(alert.Name))
            {
                throw new ConfigurationException("Every alert needs a name.");
            }

            if (!alerts.TryAdd(alert.Name, null!))
            {
                throw new ConfigurationException($"Alert '{alert.Name}' is defined more than once.");
            }

            try
            {
                alerts[alert.Name] = new CompiledAlertRule
                {
                    Config = alert,
                    Pattern = AlertPattern.Compile(alert.Pattern, alert.Type),
                };
            }
            catch (FormatException ex)
            {
                throw new ConfigurationException($"Alert '{alert.Name}' is invalid: {ex.Message}", ex);
            }

            if (alerts[alert.Name].Pattern.MaximumLength > AlertEngine.MaximumHistoryLength)
            {
                throw new ConfigurationException(
                    $"Alert '{alert.Name}' spans {alerts[alert.Name].Pattern.MaximumLength} rounds, " +
                    $"more than the {AlertEngine.MaximumHistoryLength} kept per target; it could never match.");
            }
        }

        return alerts;
    }

    /// <summary>Walks one subtree, resolving settings and collecting measured targets.</summary>
    private static MenuNode? Walk(
        TargetNode node,
        string parentId,
        TargetDefaults inherited,
        List<MeasuredTarget> targets,
        IReadOnlyDictionary<string, CompiledAlertRule> alerts,
        HashSet<string> seenIds,
        bool skipUnsupported,
        List<string> skipped,
        List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(node.Id))
        {
            throw new ConfigurationException(
                $"A target below '{(parentId.Length == 0 ? "(root)" : parentId)}' has no id.");
        }

        if (!IdPattern.IsMatch(node.Id))
        {
            throw new ConfigurationException(
                $"Target id '{node.Id}' is invalid; use letters, digits, dashes and underscores only.");
        }

        var id = parentId.Length == 0 ? node.Id : $"{parentId}/{node.Id}";
        if (!seenIds.Add(id))
        {
            throw new ConfigurationException($"Target '{id}' is defined more than once.");
        }

        if (!string.IsNullOrWhiteSpace(node.Host))
        {
            try
            {
                ValidateHost(id, node.Host);
            }
            catch (ConfigurationException ex) when (skipUnsupported)
            {
                skipped.Add(ex.Message);
                return null;
            }
        }

        var settings = Merge(inherited, node);
        var title = string.IsNullOrWhiteSpace(node.Title) ? node.Id : node.Title;

        var menuNode = new MenuNode
        {
            Id = id,
            Title = title,
            Description = node.Description,
            IsTarget = !string.IsNullOrWhiteSpace(node.Host),
            Host = node.Host,
            ProbeType = string.IsNullOrWhiteSpace(node.Host) ? null : settings.Probe,
        };

        if (!string.IsNullOrWhiteSpace(node.Host))
        {
            try
            {
                targets.Add(BuildTarget(id, title, parentId, node, settings, alerts, warnings));
            }
            catch (ConfigurationException ex) when (skipUnsupported)
            {
                skipped.Add(ex.Message);
                return null;
            }
        }

        foreach (var child in node.Children)
        {
            var walked = Walk(child, id, settings, targets, alerts, seenIds, skipUnsupported, skipped, warnings);
            if (walked is not null)
            {
                menuNode.Children.Add(walked);
            }
        }

        if (!menuNode.IsTarget && menuNode.Children.Count == 0)
        {
            // A folder whose children were all skipped is not an error in itself.
            if (skipUnsupported)
            {
                return null;
            }

            throw new ConfigurationException($"Target '{id}' has neither a host nor any children.");
        }

        return menuNode;
    }

    /// <summary>
    /// Rejects host forms this port does not implement. Upstream accepts DYNAMIC, a
    /// list of /target/paths for a multi-host graph, and a ~slave suffix; measuring
    /// any of them here would fail every round and look exactly like the target being
    /// down, so they are refused at load time with an explanation instead.
    /// </summary>
    private static void ValidateHost(string id, string host)
    {
        if (host.StartsWith('%') || host.EndsWith('%'))
        {
            if (!Probes.HostResolver.IsToken(host))
            {
                throw new ConfigurationException(
                    $"Target '{id}': '{host}' is not a host token this version understands. " +
                    $"Supported tokens: {string.Join(", ", Probes.HostResolver.Tokens)}.");
            }

            return;
        }

        if (host.StartsWith("DYNAMIC", StringComparison.Ordinal))
        {
            throw new ConfigurationException(
                $"Target '{id}': DYNAMIC hosts are not supported. They need the CGI that lets a " +
                "remote machine register its own address, which this port does not implement.");
        }

        if (host.StartsWith('/'))
        {
            throw new ConfigurationException(
                $"Target '{id}': multi-host targets (a list of /target/paths) are not supported. " +
                "Give this target a host of its own.");
        }

        if (host.Contains('~', StringComparison.Ordinal))
        {
            throw new ConfigurationException(
                $"Target '{id}': the ~slave suffix in '{host}' is not supported; " +
                "this port has no master/slave mode.");
        }

        if (host.Contains(' ', StringComparison.Ordinal))
        {
            throw new ConfigurationException($"Target '{id}': '{host}' is not a single host name or address.");
        }
    }

    private static MeasuredTarget BuildTarget(
        string id,
        string title,
        string parentId,
        TargetNode node,
        TargetDefaults settings,
        IReadOnlyDictionary<string, CompiledAlertRule> alerts,
        List<string> warnings)
    {
        var probe = settings.Probe!;
        var step = settings.Step!.Value;
        var pings = settings.Pings!.Value;
        var timeoutMs = settings.TimeoutMs ?? DefaultTimeoutMs(probe);

        if (step < 10)
        {
            throw new ConfigurationException($"Target '{id}': step must be at least 10 seconds.");
        }

        // Upstream requires three, and with fewer there is no spread to draw: the
        // smoke needs at least three probes before it has any shape.
        if (pings < 3)
        {
            throw new ConfigurationException($"Target '{id}': pings must be at least 3.");
        }

        if (timeoutMs is < 1 or > 300_000)
        {
            throw new ConfigurationException($"Target '{id}': timeoutMs must be between 1 and 300000.");
        }

        if (settings.PingIntervalMs is < 0 or > 300_000)
        {
            throw new ConfigurationException($"Target '{id}': pingIntervalMs must be between 0 and 300000.");
        }

        if (settings.Port is < 1 or > 65535)
        {
            throw new ConfigurationException($"Target '{id}': port must be between 1 and 65535.");
        }

        if (settings.PacketSize is < 12 or > 64000)
        {
            throw new ConfigurationException($"Target '{id}': packetSize must be between 12 and 64000.");
        }

        if (settings.RecordType is { } recordType && !Probes.DnsProbe.IsKnownRecordType(recordType))
        {
            throw new ConfigurationException($"Target '{id}': '{recordType}' is not a DNS record type.");
        }

        // A round that overruns its step is not fatal - the next round simply starts at
        // the following boundary and some are skipped - and upstream allows it, so this
        // is a warning rather than a refusal.
        var roundDuration = (long)(pings - 1) * settings.PingIntervalMs!.Value + timeoutMs;
        if (roundDuration > step * 1000L)
        {
            warnings.Add(
                $"Target '{id}': {pings} pings at {settings.PingIntervalMs}ms plus a {timeoutMs}ms timeout " +
                $"can take {roundDuration / 1000.0:F0}s, longer than the {step}s step, so rounds will be skipped.");
        }

        var ruleNames = settings.AlertRules ?? [];
        foreach (var ruleName in ruleNames)
        {
            if (!alerts.ContainsKey(ruleName))
            {
                throw new ConfigurationException($"Target '{id}' refers to undefined alert '{ruleName}'.");
            }
        }

        if (Probes.HostResolver.IsToken(node.Host!) &&
            probe.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(settings.Url))
        {
            throw new ConfigurationException(
                $"Target '{id}': the http probe cannot build a URL from '{node.Host}'. Set url explicitly.");
        }

        if (probe.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(settings.Url) &&
            !Uri.TryCreate(settings.Url, UriKind.Absolute, out _))
        {
            throw new ConfigurationException($"Target '{id}': '{settings.Url}' is not a valid absolute URL.");
        }

        return new MeasuredTarget
        {
            Id = id,
            Title = title,
            Description = node.Description,
            Host = node.Host!,
            ProbeType = probe,
            StepSeconds = step,
            Pings = pings,
            PingIntervalMs = settings.PingIntervalMs!.Value,
            TimeoutMs = timeoutMs,
            Port = settings.Port,
            Query = settings.Query,
            RecordType = settings.RecordType,
            Url = settings.Url,
            PacketSize = settings.PacketSize!.Value,
            AlertRules = ruleNames,
            ParentId = parentId,
        };
    }

    /// <summary>Overlays the settings a node specifies on top of the ones it inherits.</summary>
    private static TargetDefaults Merge(TargetDefaults inherited, TargetDefaults? overrides) => new()
    {
        Probe = overrides?.Probe ?? inherited.Probe,
        Step = overrides?.Step ?? inherited.Step,
        Pings = overrides?.Pings ?? inherited.Pings,
        PingIntervalMs = overrides?.PingIntervalMs ?? inherited.PingIntervalMs,
        TimeoutMs = overrides?.TimeoutMs ?? inherited.TimeoutMs,
        Port = overrides?.Port ?? inherited.Port,
        Query = overrides?.Query ?? inherited.Query,
        RecordType = overrides?.RecordType ?? inherited.RecordType,
        Url = overrides?.Url ?? inherited.Url,
        PacketSize = overrides?.PacketSize ?? inherited.PacketSize,
        AlertRules = overrides?.AlertRules ?? inherited.AlertRules,
    };
}
