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
    /// <summary>Readings retained per target; the longest pattern that can be evaluated.</summary>
    public const int HistoryLength = 64;

    private readonly ConcurrentDictionary<string, TargetState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, CompiledAlertRule> _rules;

    public AlertEngine(IReadOnlyDictionary<string, CompiledAlertRule> rules)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

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
                .OrderBy(rule => rule.Config.Priority ?? int.MaxValue)
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

                var key = $"{target.Id}/{rule.Name}";
                var active = (ConcurrentDictionary<string, AlertEvent>)Active;
                if (matched)
                {
                    active[key] = alertEvent;
                }
                else
                {
                    active.TryRemove(key, out _);
                }
            }
        }

        return events;
    }

    private static void Trim(List<Reading> readings)
    {
        while (readings.Count > HistoryLength)
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
