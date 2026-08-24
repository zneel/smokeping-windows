using SmokePing.Net.Alerting;
using SmokePing.Net.Configuration;
using SmokePing.Net.Storage;
using Xunit;

namespace SmokePing.Net.Tests;

public sealed class AlertTests
{
    /// <summary>AlertPattern: consecutive losses match at the newest reading</summary>
    [Fact]
    public void AlertPattern_Consecutive_Losses_Match_At_The_Newest_Reading()
    {
            var pattern = AlertPattern.Compile(">20%,>20%,>20%", "loss");

            Verify.True(pattern.Matches(Loss(0, 30, 40, 50)), "three high readings at the end match");
            Verify.False(pattern.Matches(Loss(30, 40, 50, 0)), "the pattern is anchored at the newest reading");
            Verify.False(pattern.Matches(Loss(0, 30, 40)), "only two high readings do not match");
    }

    /// <summary>AlertPattern: not enough history cannot match</summary>
    [Fact]
    public void AlertPattern_Not_Enough_History_Cannot_Match()
    {
            var pattern = AlertPattern.Compile(">20%,>20%,>20%", "loss");

            Verify.Equal(3, pattern.MinimumLength, "three fixed tokens");
            Verify.False(pattern.Matches(Loss(50, 50)), "two readings cannot satisfy three tokens");
    }

    /// <summary>AlertPattern: gaps allow readings in between, within the original's bounds</summary>
    [Fact]
    public void AlertPattern_Gaps_Allow_Readings_In_Between_Within_The_Original_S_Bounds()
    {
            var pattern = AlertPattern.Compile(">0%,*3*,>0%", "loss");

            // The original's gap bound is min(history - fixed tokens, N), tested with a
            // strict <, so the usable gap is narrower than the N suggests and grows
            // with the history available. These are the answers the Perl gives.
            Verify.True(pattern.Matches(Loss(0, 5, 5)), "a gap may consume nothing once there is history to spare");
            Verify.True(pattern.Matches(Loss(0, 5, 0, 5)), "and one reading with a little more");
            Verify.False(pattern.Matches(Loss(5, 0, 0, 0, 0, 5)), "four clean rounds exceed a gap of three");
    }

    /// <summary>AlertPattern: a gap pattern cannot match a history of only its fixed tokens</summary>
    [Fact]
    public void AlertPattern_A_Gap_Pattern_Cannot_Match_A_History_Of_Only_Its_Fixed_Tokens()
    {
            var pattern = AlertPattern.Compile(">0%,*3*,>0%", "loss");

            // Surprising, and faithful: with history == minimum length the original's
            // gap loop never runs a single iteration, so the pattern cannot match.
            Verify.False(pattern.Matches(Loss(5, 5)), "two readings are not enough for a two-token gap pattern");
    }

    /// <summary>AlertPattern: a gap tolerates fewer rounds than its number suggests</summary>
    [Fact]
    public void AlertPattern_A_Gap_Tolerates_Fewer_Rounds_Than_Its_Number_Suggests()
    {
            // The documented example. Over the fourteen readings the pattern itself
            // spans, *12* tolerates six intervening rounds, not twelve.
            var pattern = AlertPattern.Compile(">0%,*12*,>0%", "loss");

            List<Reading> WithGap(int clean)
            {
                var history = new List<Reading>();
                for (var i = 0; i < 14 - 2 - clean; i++)
                {
                    history.Add(new Reading(0));
                }

                history.Add(new Reading(5));
                for (var i = 0; i < clean; i++)
                {
                    history.Add(new Reading(0));
                }

                history.Add(new Reading(5));
                return history;
            }

            Verify.True(pattern.Matches(WithGap(6)), "six intervening rounds match");
            Verify.False(pattern.Matches(WithGap(7)), "seven do not, despite the pattern saying 12");
    }

    /// <summary>AlertPattern: the upstream someloss pattern behaves as documented</summary>
    [Fact]
    public void AlertPattern_The_Upstream_Someloss_Pattern_Behaves_As_Documented()
    {
            var pattern = AlertPattern.Compile(">0%,*12*,>0%,*12*,>0%", "loss");
            var history = new List<Reading> { new(5) };

            for (var i = 0; i < 10; i++)
            {
                history.Add(new Reading(0));
            }

            history.Add(new Reading(5));
            history.Add(new Reading(0));
            history.Add(new Reading(5));

            Verify.True(pattern.Matches(history), "three lossy rounds spread over a window match");
    }

    /// <summary>AlertPattern: rtt patterns detect a step change</summary>
    [Fact]
    public void AlertPattern_Rtt_Patterns_Detect_A_Step_Change()
    {
            var pattern = AlertPattern.Compile("<10,<10,<10,<10,<100,>100,>100,>100", "rtt");
            var history = Loss(5, 5, 5, 5, 50, 150, 160, 170);

            Verify.True(pattern.Matches(history), "latency stepping up from below 10ms to above 100ms matches");
    }

    /// <summary>AlertPattern: a range token requires both comparisons</summary>
    [Fact]
    public void AlertPattern_A_Range_Token_Requires_Both_Comparisons()
    {
            var pattern = AlertPattern.Compile(">100<200", "rtt");

            Verify.True(pattern.Matches(Loss(150)), "150 lies inside the range");
            Verify.False(pattern.Matches(Loss(250)), "250 is above the range");
            Verify.False(pattern.Matches(Loss(50)), "50 is below the range");
    }

    /// <summary>AlertPattern: U matches missing data and S matches the start marker</summary>
    [Fact]
    public void AlertPattern_U_Matches_Missing_Data_And_S_Matches_The_Start_Marker()
    {
            var undefined = AlertPattern.Compile("==U", "loss");
            var defined = AlertPattern.Compile("!=U", "loss");
            var start = AlertPattern.Compile("==S", "loss");

            Verify.True(undefined.Matches(Loss(0, null)), "the newest reading has no data");
            Verify.False(undefined.Matches(Loss(null, 0)), "the newest reading does have data");
            Verify.True(defined.Matches(Loss(null, 0)), "!=U is the mirror image");
            Verify.True(start.Matches([Reading.Start]), "the start marker matches S");
            Verify.False(start.Matches(Loss(0)), "a real reading is not the start marker");
    }

    /// <summary>AlertPattern: a numeric token never matches missing data</summary>
    [Fact]
    public void AlertPattern_A_Numeric_Token_Never_Matches_Missing_Data()
    {
            var pattern = AlertPattern.Compile(">0%", "loss");

            Verify.False(pattern.Matches(Loss((double?)null)), "no data is not the same as loss");
            Verify.False(pattern.Matches([Reading.Start]), "the start marker is not a measurement");
    }

    /// <summary>AlertPattern: ==* accepts any single reading</summary>
    [Fact]
    public void AlertPattern_Accepts_Any_Single_Reading()
    {
            var pattern = AlertPattern.Compile(">0%,==*,>0%", "loss");

            Verify.True(pattern.Matches(Loss(5, 0, 5)), "the wildcard covers the clean round in the middle");
            Verify.False(pattern.Matches(Loss(5, 5)), "the wildcard still consumes exactly one reading");
    }

    /// <summary>AlertPattern: invalid patterns are rejected with a reason</summary>
    [Fact]
    public void AlertPattern_Invalid_Patterns_Are_Rejected_With_A_Reason()
    {
            Verify.Throws<FormatException>(() => AlertPattern.Compile(">20", "loss"), "loss needs a percent sign");
            Verify.Throws<FormatException>(() => AlertPattern.Compile(">20%", "rtt"), "rtt is in milliseconds");
            Verify.Throws<FormatException>(() => AlertPattern.Compile("nonsense", "loss"), "gibberish is not a pattern");
            Verify.Throws<FormatException>(() => AlertPattern.Compile(">0%,*3*", "loss"), "a pattern cannot end with a gap");
            Verify.Throws<FormatException>(() => AlertPattern.Compile("", "loss"), "an empty pattern is meaningless");
            Verify.Throws<FormatException>(() => AlertPattern.Compile(">0%", "bogus"), "the type must be loss or rtt");
    }

    /// <summary>AlertEngine: edge triggered rules fire on raise and on clear</summary>
    [Fact]
    public void AlertEngine_Edge_Triggered_Rules_Fire_On_Raise_And_On_Clear()
    {
            var rule = Rule("down", "loss", "==100%,==100%");
            var engine = new AlertEngine(new Dictionary<string, CompiledAlertRule> { ["down"] = rule });
            var target = Target("down");

            Verify.Equal(0, engine.Evaluate(target, SampleWith(10, 10), Now(1)).Count, "one bad round is not enough");

            var raised = engine.Evaluate(target, SampleWith(10, 10), Now(2));
            Verify.Equal(1, raised.Count, "the second bad round raises the alert");
            Verify.Equal("raised", raised[0].State, "the transition is a raise");
            Verify.Equal(1, engine.Active.Count, "the alert is now active");

            Verify.Equal(0, engine.Evaluate(target, SampleWith(10, 10), Now(3)).Count, "staying broken is not a new event");

            var cleared = engine.Evaluate(target, SampleWith(10, 0), Now(4));
            Verify.Equal(1, cleared.Count, "recovery produces an event");
            Verify.Equal("cleared", cleared[0].State, "the transition is a clear");
            Verify.Equal(0, engine.Active.Count, "the alert is no longer active");
    }

    /// <summary>AlertEngine: level triggered rules fire on every matching round</summary>
    [Fact]
    public void AlertEngine_Level_Triggered_Rules_Fire_On_Every_Matching_Round()
    {
            var rule = Rule("loss", "loss", ">0%", edgeTrigger: false);
            var engine = new AlertEngine(new Dictionary<string, CompiledAlertRule> { ["loss"] = rule });
            var target = Target("loss");

            Verify.Equal("active", engine.Evaluate(target, SampleWith(10, 1), Now(1))[0].State, "first match reports active");
            Verify.Equal(1, engine.Evaluate(target, SampleWith(10, 1), Now(2)).Count, "and so does the next one");
            Verify.Equal(0, engine.Evaluate(target, SampleWith(10, 0), Now(3)).Count, "a clean round says nothing");
    }

    /// <summary>AlertEngine: the highest priority rule speaks for the round</summary>
    [Fact]
    public void AlertEngine_The_Highest_Priority_Rule_Speaks_For_The_Round()
    {
            var rules = new Dictionary<string, CompiledAlertRule>
            {
                ["down"] = Rule("down", "loss", "==100%", priority: 1),
                ["someloss"] = Rule("someloss", "loss", ">0%", priority: 2),
            };

            var engine = new AlertEngine(rules);
            var events = engine.Evaluate(Target("down", "someloss"), SampleWith(10, 10), Now(1));

            Verify.Equal(1, events.Count, "only one prioritised notification per round");
            Verify.Equal("down", events[0].AlertName, "the lower priority number wins");
    }

    /// <summary>AlertEngine: unprioritised rules all get to notify</summary>
    [Fact]
    public void AlertEngine_Unprioritised_Rules_All_Get_To_Notify()
    {
            var rules = new Dictionary<string, CompiledAlertRule>
            {
                ["a"] = Rule("a", "loss", ">0%"),
                ["b"] = Rule("b", "loss", ">50%"),
            };

            var engine = new AlertEngine(rules);
            var events = engine.Evaluate(Target("a", "b"), SampleWith(10, 10), Now(1));

            Verify.Equal(2, events.Count, "both rules notify when neither has a priority");
    }

    /// <summary>AlertEngine: history starts with the S marker</summary>
    [Fact]
    public void AlertEngine_History_Starts_With_The_S_Marker()
    {
            var rule = Rule("first", "loss", "==S,==0%");
            var engine = new AlertEngine(new Dictionary<string, CompiledAlertRule> { ["first"] = rule });

            var events = engine.Evaluate(Target("first"), SampleWith(10, 0), Now(1));

            Verify.Equal(1, events.Count, "the very first round follows the start marker");
    }

    /// <summary>AlertEngine: alerts are tracked per target</summary>
    [Fact]
    public void AlertEngine_Alerts_Are_Tracked_Per_Target()
    {
            var rule = Rule("down", "loss", "==100%");
            var engine = new AlertEngine(new Dictionary<string, CompiledAlertRule> { ["down"] = rule });

            engine.Evaluate(Target(["down"], "a"), SampleWith(10, 10), Now(1));
            engine.Evaluate(Target(["down"], "b"), SampleWith(10, 0), Now(1));

            Verify.Equal(1, engine.Active.Count, "only the broken target has an active alert");
            Verify.True(engine.Active.ContainsKey("a/down"), "and it is the right one");
    }


    private static IReadOnlyList<Reading> Loss(params double?[] values) =>
        values.Select(v => new Reading(v)).ToList();

    

    private static DateTimeOffset Now(int round) => DateTimeOffset.UnixEpoch.AddMinutes(round * 5);

    private static CompiledAlertRule Rule(
        string name,
        string type,
        string pattern,
        bool edgeTrigger = true,
        int? priority = null) => new()
        {
            Config = new AlertRuleConfig
            {
                Name = name,
                Type = type,
                Pattern = pattern,
                Comment = $"{name} test rule",
                EdgeTrigger = edgeTrigger,
                Priority = priority,
            },
            Pattern = AlertPattern.Compile(pattern, type),
        };

    private static MeasuredTarget Target(params string[] alertRules) => Target(alertRules, "a");

    private static MeasuredTarget Target(string[] alertRules, string id) => new()
    {
        Id = id,
        Title = id,
        Host = "192.0.2.1",
        ProbeType = "icmp",
        StepSeconds = 300,
        Pings = 10,
        PingIntervalMs = 100,
        TimeoutMs = 1000,
        PacketSize = 56,
        AlertRules = alertRules,
        ParentId = string.Empty,
    };

    private static Sample SampleWith(int sent, int lost)
    {
        var successes = Enumerable.Range(0, sent - lost).Select(i => 10.0 + i).ToList();
        return new Sample
        {
            Timestamp = 0,
            Sent = sent,
            Lost = lost,
            Quantiles = Quantiles.FromSamples(successes),
        };
    }
}
