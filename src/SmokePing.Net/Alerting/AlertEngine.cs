using System.Collections.Concurrent;
using System.Globalization;
using SmokePing.Net.Configuration;
using SmokePing.Net.Storage;

namespace SmokePing.Net.Alerting;

/// <summary>A raise / clear / still-active notification produced by the alert engine.</summary>
public sealed class AlertEvent
{
    public required DateTimeOffset Timestamp { get; init; }

    public required string AlertName { get; init; }

    public required string TargetId { get; init; }

    public required string TargetTitle { get; init; }

    public required string Host { get; init; }

    /// <summary>"raised", "cleared" or "active".</summary>
    public required string State { get; init; }

    public required string Comment { get; init; }

    public required string Pattern { get; init; }

    /// <summary>Recent loss history in percent, oldest first.</summary>
    public required IReadOnlyList<string> LossHistory { get; init; }

    /// <summary>Recent median rtt history in milliseconds, oldest first.</summary>
    public required IReadOnlyList<string> RttHistory { get; init; }

    public bool IsRaised => State != "cleared";
}

/// <summary>A rule compiled from configuration together with its notification settings.</summary>
public sealed class CompiledAlertRule
{
    public required AlertRuleConfig Config { get; init; }

    public required AlertPattern Pattern { get; init; }

    public string Name => Config.Name;
}

/// <summary>
/// Keeps the recent loss/rtt history for every target and decides, after each
/// measurement round, which alerts should fire. Mirrors upstream behaviour: rules are
/// evaluated in priority order, edge-triggered rules notify only on transitions, and
/// the history starts with the synthetic "S" marker so <c>==S</c> patterns work.
/// </summary>
public sealed class AlertEngine
{
    /// <summary>Readings retained per target when no rule needs more.</summary>
    public const int DefaultHistoryLength = 64;

    /// <summary>
    /// Upper bound on retained readings, and therefore on how far back a pattern may
    /// look. A pattern longer than this is rejected at load time rather than silently
    /// never matching.
    /// </summary>
    public const int MaximumHistoryLength = 1024;

    private readonly int _historyLength;

    private readonly ConcurrentDictionary<string, TargetState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, CompiledAlertRule> _rules;

    public AlertEngine(IReadOnlyDictionary<string, CompiledAlertRule> rules)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));

        // Upstream sizes its history from the longest pattern in play (fetchlength);
        // keeping a fixed 64 would make a longer pattern quietly impossible to match.
        var longest = rules.Values.Select(rule => rule.Pattern.MaximumLength).DefaultIfEmpty(0).Max();
        _historyLength = Math.Clamp(longest, DefaultHistoryLength, MaximumHistoryLength);
    }

    /// <summary>Readings retained per target, sized to the longest configured pattern.</summary>
    public int HistoryLength => _historyLength;

    /// <summary>Alerts currently raised, keyed by "targetId/alertName".</summary>
    public IReadOnlyDictionary<string, AlertEvent> Active { get; } =
        new ConcurrentDictionary<string, AlertEvent>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records one measurement round for a target and returns the notifications it triggers.
    /// </summary>
    public IReadOnlyList<AlertEvent> Evaluate(MeasuredTarget target, Sample sample, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sample);

        var state = _state.GetOrAdd(target.Id, static _ => new TargetState());
        var events = new List<AlertEvent>();

        lock (state)
        {
            var lossPercent = sample.Sent == 0 ? (double?)null : sample.LossFraction * 100.0;
            state.Loss.Add(new Reading(lossPercent));
            state.Rtt.Add(new Reading(sample.Median));
            Trim(state.Loss);
            Trim(state.Rtt);

            var applicable = target.AlertRules
                .Select(name => _rules.TryGetValue(name, out var rule) ? rule : null)
                .Where(rule => rule is not null)
                .Select(rule => rule!)
                .OrderBy(rule => rule.Config.Priority ?? 0)
                .ToList();

            var prioritisedNotificationSent = false;

            foreach (var rule in applicable)
            {
                var history = rule.Pattern.IsLoss ? state.Loss : state.Rtt;
                var matched = rule.Pattern.Matches(history);
                var previouslyMatched = state.PreviousMatch.GetValueOrDefault(rule.Name);

                string? what = null;
                if (rule.Config.EdgeTrigger)
                {
                    if (matched != previouslyMatched)
                    {
                        what = matched ? "raised" : "cleared";
                    }
                }
                else if (matched)
                {
                    what = "active";
                }

                state.PreviousMatch[rule.Name] = matched;

                // Tracked before the notification decision: a level-triggered rule
                // reports nothing on a clean round, but it has still stopped being
                // active, and leaving it in the set would have the web interface
                // showing an alert that cleared hours ago.
                var key = $"{target.Id}/{rule.Name}";
                var active = (ConcurrentDictionary<string, AlertEvent>)Active;
                if (!matched)
                {
                    active.TryRemove(key, out _);
                }

                if (what is null)
                {
                    continue;
                }

                // A rule with a priority yields to any higher-priority rule that
                // already notified in this round; unprioritised rules always notify.
                if (rule.Config.Priority is not null)
                {
                    if (prioritisedNotificationSent)
                    {
                        continue;
                    }

                    prioritisedNotificationSent = true;
                }

                var alertEvent = new AlertEvent
                {
                    Timestamp = now,
                    AlertName = rule.Name,
                    TargetId = target.Id,
                    TargetTitle = target.Title,
                    Host = target.Host,
                    State = what,
                    Comment = rule.Config.Comment,
                    Pattern = rule.Pattern.Source,
                    LossHistory = Format(state.Loss, "{0:F0}%"),
                    RttHistory = Format(state.Rtt, "{0:F1}ms"),
                };

                events.Add(alertEvent);

                if (matched)
                {
                    active[key] = alertEvent;
                }
            }
        }

        return events;
    }

    private void Trim(List<Reading> readings)
    {
        while (readings.Count > _historyLength)
        {
            readings.RemoveAt(0);
        }
    }

    private static List<string> Format(List<Reading> readings, string format) =>
        readings.Select(r => r.IsStart
            ? "S"
            : r.Value is { } value
                ? string.Format(CultureInfo.InvariantCulture, format, value)
                : "U").ToList();

    private sealed class TargetState
    {
        /// <summary>Histories start with the "S" marker, matching upstream's initial stack.</summary>
        public List<Reading> Loss { get; } = [Reading.Start];

        public List<Reading> Rtt { get; } = [Reading.Start];

        public Dictionary<string, bool> PreviousMatch { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
