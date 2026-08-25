using System.Text.Json.Serialization;

namespace SmokePing.Net.Configuration;

/// <summary>Root of smokeping.json.</summary>
public sealed class SmokePingConfig
{
    public GeneralConfig General { get; set; } = new();

    /// <summary>Settings inherited by every target unless overridden further down the tree.</summary>
    public TargetDefaults Defaults { get; set; } = new();

    /// <summary>Storage settings, taken from the Database section of a native configuration.</summary>
    public DatabaseConfig Database { get; set; } = new();

    /// <summary>Settings for the continuously recorded, second-by-second trace.</summary>
    public TraceConfig Trace { get; set; } = new();

    /// <summary>The target hierarchy. Nodes without a host act as menu folders.</summary>
    public List<TargetNode> Targets { get; set; } = [];

    public List<AlertRuleConfig> Alerts { get; set; } = [];
}

/// <summary>One resolution tier, as the Database section's archive table describes it.</summary>
public sealed class ArchiveConfig
{
    /// <summary>How many polling steps one row of this archive covers.</summary>
    public int Steps { get; set; } = 1;

    /// <summary>How many rows the archive keeps before it wraps.</summary>
    public int Rows { get; set; }
}

public sealed class DatabaseConfig
{
    /// <summary>
    /// Storage format: "spd" for the built-in round-robin files, or "rrd" for
    /// RRDtool files laid out as SmokePing lays them out, which an existing
    /// installation's data can be read from and which rrdtool can still read.
    /// </summary>
    public string Format { get; set; } = "spd";

    /// <summary>
    /// Resolution tiers. Empty means the built-in plan, which matches upstream's
    /// default archive table.
    /// </summary>
    public List<ArchiveConfig> Archives { get; set; } = [];
}

/// <summary>
/// Settings for the recorded trace: one probe a second, kept on disk.
///
/// This runs whether or not anybody is looking at it, which is the point. A spike
/// that lasts two seconds happens while you are busy, and the only way to still have
/// it afterwards is to have been recording before it started.
/// </summary>
public sealed class TraceConfig
{
    /// <summary>
    /// Whether targets are traced by default. Individual targets can opt in or out
    /// with their own <c>trace</c> setting.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between recorded probes.</summary>
    public int IntervalSeconds { get; set; } = 1;

    /// <summary>Hours kept at full resolution.</summary>
    public int FineHours { get; set; } = 24;

    /// <summary>Seconds covered by one slot of the summary tier.</summary>
    public int CoarseStepSeconds { get; set; } = 60;

    /// <summary>
    /// Days kept in the summary tier. It stores the maximum as well as the mean, so a
    /// spike stays visible long after the second that held it has been overwritten.
    /// </summary>
    public int CoarseDays { get; set; } = 30;

    /// <summary>How a moment counts as a peak worth listing.</summary>
    public SpikeConfig Spike { get; set; } = new();
}

/// <summary>
/// What counts as a peak.
///
/// Thresholds are derived from the window being looked at rather than fixed, because
/// "slow" means something different on a 4 ms LAN and a 90 ms transatlantic hop. The
/// floors stop a very steady link from reporting a peak every time it moves a
/// millisecond.
/// </summary>
public sealed class SpikeConfig
{
    /// <summary>How far above the window's own typical spread a reading must sit.</summary>
    public double Deviations { get; set; } = 4;

    /// <summary>A latency peak must also exceed the baseline by at least this many milliseconds.</summary>
    public double LatencyFloorMs { get; set; } = 10;

    /// <summary>A jitter peak must also exceed its baseline by at least this many milliseconds.</summary>
    public double JitterFloorMs { get; set; } = 5;
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

    /// <summary>Record type the DNS probe asks for: A, AAAA, MX, NS, TXT and so on.</summary>
    public string? RecordType { get; set; }

    /// <summary>URL requested by the HTTP probe. Overrides Host when set.</summary>
    public string? Url { get; set; }

    /// <summary>ICMP payload size in bytes.</summary>
    public int? PacketSize { get; set; }

    /// <summary>Names of the alert rules applied to this target.</summary>
    public List<string>? AlertRules { get; set; }

    /// <summary>
    /// Whether to record a second-by-second trace of this target. Inherited like every
    /// other setting; unset means whatever the Trace section says.
    /// </summary>
    public bool? Trace { get; set; }
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
    /// every round the alert matches. Defaults to false, as upstream's edgetrigger
    /// does - a rule that matches five rounds running notifies five times unless it
    /// says otherwise.
    /// </summary>
    public bool EdgeTrigger { get; set; }

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

    /// <summary>DNS record type, or null for the probe's default.</summary>
    public string? RecordType { get; init; }

    public string? Url { get; init; }

    public required int PacketSize { get; init; }

    public required IReadOnlyList<string> AlertRules { get; init; }

    /// <summary>True when this target is recorded second by second.</summary>
    public required bool Traced { get; init; }

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
