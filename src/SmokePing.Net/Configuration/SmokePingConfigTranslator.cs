using System.Globalization;

namespace SmokePing.Net.Configuration;

/// <summary>
/// Turns a parsed SmokePing configuration into this implementation's model, so an
/// existing installation's file can be used unchanged.
///
/// Where a setting has no equivalent here it is ignored rather than rejected - a real
/// configuration is full of paths to the CGI, mail templates and image caches that
/// have no meaning without them. Settings that would change what gets measured are
/// never ignored silently: an unsupported probe or host form is an error.
/// </summary>
public static class SmokePingConfigTranslator
{
    /// <summary>
    /// Probe classes mapped onto the probes implemented here. Several upstream probes
    /// are different ways of doing the same measurement.
    /// </summary>
    private static readonly Dictionary<string, string> ProbeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FPing"] = "icmp",
        ["FPing6"] = "icmp",
        ["FPingContinuous"] = "icmp",
        ["IOSPing"] = "icmp",
        ["NFSping"] = "icmp",
        ["DismanPing"] = "icmp",
        ["TCPPing"] = "tcp",
        ["DNS"] = "dns",
        ["AnotherDNS"] = "dns",
        ["Curl"] = "http",
        ["AnotherCurl"] = "http",
        ["EchoPingHttp"] = "http",
        ["EchoPingHttps"] = "http",
    };

    public static SmokePingConfig Translate(IReadOnlyList<ConfigSection> sections, string path)
    {
        ArgumentNullException.ThrowIfNull(sections);

        var config = new SmokePingConfig();
        var probeDefaults = new Dictionary<string, ConfigSection>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in sections)
        {
            switch (section.Name.ToLowerInvariant())
            {
                case "general":
                    TranslateGeneral(section, config);
                    break;
                case "database":
                    TranslateDatabase(section, config);
                    break;
                case "alerts":
                    TranslateAlerts(section, config);
                    break;
                case "probes":
                    foreach (var probe in section.Children)
                    {
                        probeDefaults[probe.Name] = probe;
                    }

                    break;
                case "targets":
                    TranslateTargets(section, config, probeDefaults, path);
                    break;
                default:
                    // Presentation, Slaves and anything else describe things this
                    // implementation does not have.
                    break;
            }
        }

        if (config.Targets.Count == 0)
        {
            throw new ConfigurationException($"'{path}': no *** Targets *** section, so there is nothing to measure.");
        }

        return config;
    }

    private static void TranslateGeneral(ConfigSection section, SmokePingConfig config)
    {
        config.General.Owner = section.Get("owner") ?? string.Empty;
        config.General.ContactEmail = section.Get("contact") ?? string.Empty;

        if (section.Get("datadir") is { Length: > 0 } dataDirectory)
        {
            config.General.DataDirectory = dataDirectory;
        }
    }

    private static void TranslateDatabase(ConfigSection section, SmokePingConfig config)
    {
        if (TryParseInt(section.Get("step")) is { } step)
        {
            config.Defaults.Step = step;
        }

        if (TryParseInt(section.Get("pings")) is { } pings)
        {
            config.Defaults.Pings = pings;
        }

        // The RRA table describes retention. It is read rather than ignored so a
        // migrated installation keeps the history depth it was configured for.
        foreach (var row in section.Rows)
        {
            if (row.Length < 4 || !row[0].Equals("AVERAGE", StringComparison.OrdinalIgnoreCase))
            {
                // MIN and MAX archives are not reproduced: upstream creates them but
                // none of its own graphs ever read them.
                continue;
            }

            if (TryParseInt(row[2]) is { } steps && TryParseInt(row[3]) is { } rows)
            {
                config.Database.Archives.Add(new ArchiveConfig { Steps = steps, Rows = rows });
            }
        }
    }

    private static void TranslateAlerts(ConfigSection section, SmokePingConfig config)
    {
        var inheritedEdgeTrigger = ParseYesNo(section.Get("edgetrigger")) ?? false;

        foreach (var alert in section.Children)
        {
            var type = alert.Get("type");
            if (type is null)
            {
                continue;
            }

            if (type.Equals("matcher", StringComparison.OrdinalIgnoreCase))
            {
                throw new ConfigurationException(
                    $"Alert '{alert.Name}' uses a matcher plugin, which is not implemented. " +
                    "Matchers decide differently depending on whether the alert is already raised, " +
                    "which a detector pattern cannot express.");
            }

            config.Alerts.Add(new AlertRuleConfig
            {
                Name = alert.Name,
                Type = type,
                Pattern = alert.Get("pattern") ?? string.Empty,
                Comment = alert.Get("comment") ?? string.Empty,
                EdgeTrigger = ParseYesNo(alert.Get("edgetrigger")) ?? inheritedEdgeTrigger,
                Priority = TryParseInt(alert.Get("priority")),
            });
        }
    }

    private static void TranslateTargets(
        ConfigSection section,
        SmokePingConfig config,
        IReadOnlyDictionary<string, ConfigSection> probeDefaults,
        string path)
    {
        ApplySettings(section, config.Defaults, probeDefaults, path, "*** Targets ***");

        foreach (var child in section.Children)
        {
            config.Targets.Add(TranslateNode(child, probeDefaults, path));
        }
    }

    private static TargetNode TranslateNode(
        ConfigSection section,
        IReadOnlyDictionary<string, ConfigSection> probeDefaults,
        string path)
    {
        var node = new TargetNode
        {
            Id = section.Name,

            // Upstream shows "menu" in the navigation and "title" on the page; there
            // is one name here, and the menu entry is the one people navigate by.
            Title = section.Get("menu") ?? section.Get("title") ?? section.Name,
            Description = section.Get("remark") ?? section.Get("title"),
            Host = section.Get("host"),
        };

        if (section.Get("alerts") is { Length: > 0 } alerts)
        {
            node.AlertRules = [.. alerts.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)];
        }

        ApplySettings(section, node, probeDefaults, path, section.Name);

        foreach (var child in section.Children)
        {
            node.Children.Add(TranslateNode(child, probeDefaults, path));
        }

        return node;
    }

    /// <summary>
    /// Copies the measurement settings a section carries, including the parameters of
    /// whichever probe it selects.
    /// </summary>
    private static void ApplySettings(
        ConfigSection section,
        TargetDefaults settings,
        IReadOnlyDictionary<string, ConfigSection> probeDefaults,
        string path,
        string where)
    {
        if (section.Get("probe") is { Length: > 0 } probeName)
        {
            if (!ProbeMap.TryGetValue(probeName, out var probe))
            {
                throw new ConfigurationException(
                    $"'{path}': probe '{probeName}' at '{where}' is not implemented. " +
                    $"Supported: {string.Join(", ", ProbeMap.Keys.Order())}.");
            }

            settings.Probe = probe;

            // Probe-level parameters apply to everything using that probe.
            if (probeDefaults.TryGetValue(probeName, out var defaults))
            {
                ApplyProbeParameters(defaults, settings);
            }
        }

        ApplyProbeParameters(section, settings);
    }

    /// <summary>
    /// Maps upstream's probe parameters. Upstream measures in fractional seconds where
    /// this implementation uses milliseconds, so the units are converted rather than
    /// copied - a timeout of 1.5 must not become 1.5 milliseconds.
    /// </summary>
    private static void ApplyProbeParameters(ConfigSection section, TargetDefaults settings)
    {
        if (TryParseInt(section.Get("packetsize")) is { } packetSize)
        {
            settings.PacketSize = packetSize;
        }

        if (TryParseSeconds(section.Get("timeout")) is { } timeout)
        {
            settings.TimeoutMs = timeout;
        }

        if (TryParseSeconds(section.Get("hostinterval") ?? section.Get("mininterval")) is { } interval)
        {
            settings.PingIntervalMs = interval;
        }

        if (TryParseInt(section.Get("port")) is { } port)
        {
            settings.Port = port;
        }

        if ((section.Get("lookup") ?? section.Get("query")) is { Length: > 0 } query)
        {
            settings.Query = query;
        }

        if (section.Get("recordtype") is { Length: > 0 } recordType)
        {
            settings.RecordType = recordType;
        }

        if (section.Get("urlformat") is { Length: > 0 } url)
        {
            settings.Url = url;
        }

        if (TryParseInt(section.Get("pings")) is { } pings)
        {
            settings.Pings = pings;
        }

        if (TryParseInt(section.Get("step")) is { } step)
        {
            settings.Step = step;
        }
    }

    private static int? TryParseInt(string? value) =>
        value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>Converts upstream's fractional seconds to milliseconds.</summary>
    private static int? TryParseSeconds(string? value) =>
        value is not null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? (int)Math.Round(parsed * 1000)
            : null;

    private static bool? ParseYesNo(string? value) => value?.ToLowerInvariant() switch
    {
        "yes" or "true" or "1" => true,
        "no" or "false" or "0" => false,
        _ => null,
    };
}
