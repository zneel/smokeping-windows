using System.Text.Json.Serialization;

namespace SmokePing.Net.Configuration;

/// <summary>Root of smokeping.json.</summary>
public sealed class SmokePingConfig
{
    public GeneralConfig General { get; set; } = new();

    /// <summary>Settings inherited by every target unless overridden further down the tree.</summary>
    public TargetDefaults Defaults { get; set; } = new();

    /// <summary>The target hierarchy. Nodes without a host act as menu folders.</summary>
    public List<TargetNode> Targets { get; set; } = [];

    public List<AlertRuleConfig> Alerts { get; set; } = [];
}

public sealed class GeneralConfig
{
    public string SiteName { get; set; } = "SmokePing.NET";

    public string Owner { get; set; } = string.Empty;

    public string ContactEmail { get; set; } = string.Empty;

    /// <summary>Directory holding the round-robin databases. Relative paths resolve against the config file.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>URL the built-in web server binds to.</summary>
    public string ListenUrl { get; set; } = "http://localhost:8081";
}

/// <summary>Measurement settings; every field is inheritable down the target tree.</summary>
public class TargetDefaults
{
    /// <summary>Probe type: icmp, tcp, dns or http.</summary>
    public string? Probe { get; set; }

    /// <summary>Seconds between measurement rounds.</summary>
    public int? Step { get; set; }

    /// <summary>Probes sent per round.</summary>
    public int? Pings { get; set; }

    /// <summary>Milliseconds between individual probes inside a round.</summary>
    public int? PingIntervalMs { get; set; }

    /// <summary>Per-probe timeout in milliseconds.</summary>
    public int? TimeoutMs { get; set; }

    /// <summary>Port for the TCP probe, or the DNS server port.</summary>
    public int? Port { get; set; }

    /// <summary>Name looked up by the DNS probe.</summary>
    public string? Query { get; set; }

    /// <summary>URL requested by the HTTP probe. Overrides Host when set.</summary>
    public string? Url { get; set; }

    /// <summary>ICMP payload size in bytes.</summary>
    public int? PacketSize { get; set; }

    /// <summary>Names of the alert rules applied to this target.</summary>
    public List<string>? AlertRules { get; set; }
}

/// <summary>A node in the target hierarchy.</summary>
public sealed class TargetNode : TargetDefaults
{
    /// <summary>Path segment used in URLs and on disk. Letters, digits, dash and underscore.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name shown in the menu.</summary>
    public string? Title { get; set; }

    /// <summary>Free-form description shown on the detail page.</summary>
    public string? Description { get; set; }

    /// <summary>Host name or address to probe. Omit to make this node a menu folder only.</summary>
    public string? Host { get; set; }

    public List<TargetNode> Children { get; set; } = [];
}

public sealed class AlertRuleConfig
{
    public string Name { get; set; } = string.Empty;

    /// <summary>What the pattern is matched against: "loss" (percent) or "rtt" (milliseconds).</summary>
    public string Type { get; set; } = "loss";

    /// <summary>
    /// Comma separated detector pattern, matched against the most recent readings and
    /// anchored at the newest one. See <see cref="Alerting.AlertPattern"/> for the grammar.
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>Human-readable explanation included in notifications.</summary>
    public string Comment { get; set; } = string.Empty;

    /// <summary>
    /// When true, notify only on transitions (raised / cleared). When false, notify on
    /// every round the alert matches.
    /// </summary>
    public bool EdgeTrigger { get; set; } = true;

    /// <summary>
    /// Lower numbers are evaluated first; once a rule with a priority has notified,
    /// other prioritised rules stay quiet for that round.
    /// </summary>
    public int? Priority { get; set; }

    /// <summary>
    /// Command run on notification. Receives: alert name, target path, loss history,
    /// rtt history, host and (for edge-triggered alerts) 1 when raised / 0 when cleared.
    /// </summary>
    public string? Command { get; set; }

    /// <summary>URL that receives a JSON POST on notification.</summary>
    public string? WebhookUrl { get; set; }
}

/// <summary>A fully resolved target with all inherited settings applied.</summary>
public sealed record MeasuredTarget
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public required string Host { get; init; }

    public required string ProbeType { get; init; }

    public required int StepSeconds { get; init; }

    public required int Pings { get; init; }

    public required int PingIntervalMs { get; init; }

    public required int TimeoutMs { get; init; }

    public int? Port { get; init; }

    public string? Query { get; init; }

    public string? Url { get; init; }

    public required int PacketSize { get; init; }

    public required IReadOnlyList<string> AlertRules { get; init; }

    /// <summary>Menu path of the parent node, empty for top-level targets.</summary>
    public required string ParentId { get; init; }

    /// <summary>True when <see cref="Host"/> is a token resolved at measurement time.</summary>
    public bool HasDynamicHost => Probes.HostResolver.IsToken(Host);
}

/// <summary>A menu entry exposed to the web front end.</summary>
public sealed class MenuNode
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    /// <summary>True when this node is measured and therefore has graphs.</summary>
    public required bool IsTarget { get; init; }

    public string? Host { get; init; }

    public string? ProbeType { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<MenuNode> Children { get; init; } = [];
}
