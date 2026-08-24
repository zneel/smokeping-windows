using SmokePing.Net.Alerting;
using SmokePing.Net.Configuration;
using SmokePing.Net.Storage;

namespace SmokePing.Net.Tests;

public static class AlertTests
{
    private static IReadOnlyList<Reading> Loss(params double?[] values) =>
        values.Select(v => new Reading(v)).ToList();

    public static void Register(TestRunner runner)
    {
        runner.Add("AlertPattern: consecutive losses match at the newest reading", () =>
        {
            var pattern = AlertPattern.Compile(">20%,>20%,>20%", "loss");

            Assert.True(pattern.Matches(Loss(0, 30, 40, 50)), "three high readings at the end match");
            Assert.False(pattern.Matches(Loss(30, 40, 50, 0)), "the pattern is anchored at the newest reading");
            Assert.False(pattern.Matches(Loss(0, 30, 40)), "only two high readings do not match");
        });

        runner.Add("AlertPattern: not enough history cannot match", () =>
        {
            var pattern = AlertPattern.Compile(">20%,>20%,>20%", "loss");

            Assert.Equal(3, pattern.MinimumLength, "three fixed tokens");
            Assert.False(pattern.Matches(Loss(50, 50)), "two readings cannot satisfy three tokens");
        });

        runner.Add("AlertPattern: gaps allow arbitrary readings in between", () =>
        {
            var pattern = AlertPattern.Compile(">0%,*3*,>0%", "loss");

            Assert.True(pattern.Matches(Loss(5, 5)), "a gap may consume nothing");
            Assert.True(pattern.Matches(Loss(5, 0, 0, 5)), "a gap absorbs two clean rounds");
            Assert.False(pattern.Matches(Loss(5, 0, 0, 0, 0, 5)), "four clean rounds exceed a gap of three");
        });

        runner.Add("AlertPattern: the upstream someloss pattern behaves as documented", () =>
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

            Assert.True(pattern.Matches(history), "three lossy rounds spread over a window match");
        });

        runner.Add("AlertPattern: rtt patterns detect a step change", () =>
        {
            var pattern = AlertPattern.Compile("<10,<10,<10,<10,<100,>100,>100,>100", "rtt");
            var history = Loss(5, 5, 5, 5, 50, 150, 160, 170);

            Assert.True(pattern.Matches(history), "latency stepping up from below 10ms to above 100ms matches");
        });

        runner.Add("AlertPattern: a range token requires both comparisons", () =>
        {
            var pattern = AlertPattern.Compile(">100<200", "rtt");

            Assert.True(pattern.Matches(Loss(150)), "150 lies inside the range");
            Assert.False(pattern.Matches(Loss(250)), "250 is above the range");
            Assert.False(pattern.Matches(Loss(50)), "50 is below the range");
        });

        runner.Add("AlertPattern: U matches missing data and S matches the start marker", () =>
        {
            var undefined = AlertPattern.Compile("==U", "loss");
            var defined = AlertPattern.Compile("!=U", "loss");
            var start = AlertPattern.Compile("==S", "loss");

            Assert.True(undefined.Matches(Loss(0, null)), "the newest reading has no data");
            Assert.False(undefined.Matches(Loss(null, 0)), "the newest reading does have data");
            Assert.True(defined.Matches(Loss(null, 0)), "!=U is the mirror image");
            Assert.True(start.Matches([Reading.Start]), "the start marker matches S");
            Assert.False(start.Matches(Loss(0)), "a real reading is not the start marker");
        });

        runner.Add("AlertPattern: a numeric token never matches missing data", () =>
        {
            var pattern = AlertPattern.Compile(">0%", "loss");

            Assert.False(pattern.Matches(Loss((double?)null)), "no data is not the same as loss");
            Assert.False(pattern.Matches([Reading.Start]), "the start marker is not a measurement");
        });

        runner.Add("AlertPattern: ==* accepts any single reading", () =>
        {
            var pattern = AlertPattern.Compile(">0%,==*,>0%", "loss");

            Assert.True(pattern.Matches(Loss(5, 0, 5)), "the wildcard covers the clean round in the middle");
            Assert.False(pattern.Matches(Loss(5, 5)), "the wildcard still consumes exactly one reading");
        });

        runner.Add("AlertPattern: invalid patterns are rejected with a reason", () =>
        {
            Assert.Throws<FormatException>(() => AlertPattern.Compile(">20", "loss"), "loss needs a percent sign");
            Assert.Throws<FormatException>(() => AlertPattern.Compile(">20%", "rtt"), "rtt is in milliseconds");
            Assert.Throws<FormatException>(() => AlertPattern.Compile("nonsense", "loss"), "gibberish is not a pattern");
            Assert.Throws<FormatException>(() => AlertPattern.Compile(">0%,*3*", "loss"), "a pattern cannot end with a gap");
            Assert.Throws<FormatException>(() => AlertPattern.Compile("", "loss"), "an empty pattern is meaningless");
            Assert.Throws<FormatException>(() => AlertPattern.Compile(">0%", "bogus"), "the type must be loss or rtt");
        });

        runner.Add("AlertEngine: edge triggered rules fire on raise and on clear", () =>
        {
            var rule = Rule("down", "loss", "==100%,==100%");
            var engine = new AlertEngine(new Dictionary<string, CompiledAlertRule> { ["down"] = rule });
            var target = Target("down");

            Assert.Equal(0, engine.Evaluate(target, SampleWith(10, 10), Now(1)).Count, "one bad round is not enough");

            var raised = engine.Evaluate(target, SampleWith(10, 10), Now(2));
            Assert.Equal(1, raised.Count, "the second bad round raises the alert");
            Assert.Equal("raised", raised[0].State, "the transition is a raise");
            Assert.Equal(1, engine.Active.Count, "the alert is now active");

            Assert.Equal(0, engine.Evaluate(target, SampleWith(10, 10), Now(3)).Count, "staying broken is not a new event");

            var cleared = engine.Evaluate(target, SampleWith(10, 0), Now(4));
            Assert.Equal(1, cleared.Count, "recovery produces an event");
            Assert.Equal("cleared", cleared[0].State, "the transition is a clear");
            Assert.Equal(0, engine.Active.Count, "the alert is no longer active");
        });

        runner.Add("AlertEngine: level triggered rules fire on every matching round", () =>
        {
            var rule = Rule("loss", "loss", ">0%", edgeTrigger: false);
            var engine = new AlertEngine(new Dictionary<string, CompiledAlertRule> { ["loss"] = rule });
            var target = Target("loss");

            Assert.Equal("active", engine.Evaluate(target, SampleWith(10, 1), Now(1))[0].State, "first match reports active");
            Assert.Equal(1, engine.Evaluate(target, SampleWith(10, 1), Now(2)).Count, "and so does the next one");
            Assert.Equal(0, engine.Evaluate(target, SampleWith(10, 0), Now(3)).Count, "a clean round says nothing");
        });

        runner.Add("AlertEngine: the highest priority rule speaks for the round", () =>
        {
            var rules = new Dictionary<string, CompiledAlertRule>
            {
                ["down"] = Rule("down", "loss", "==100%", priority: 1),
                ["someloss"] = Rule("someloss", "loss", ">0%", priority: 2),
            };

            var engine = new AlertEngine(rules);
            var events = engine.Evaluate(Target("down", "someloss"), SampleWith(10, 10), Now(1));

            Assert.Equal(1, events.Count, "only one prioritised notification per round");
            Assert.Equal("down", events[0].AlertName, "the lower priority number wins");
        });

        runner.Add("AlertEngine: unprioritised rules all get to notify", () =>
        {
            var rules = new Dictionary<string, CompiledAlertRule>
            {
                ["a"] = Rule("a", "loss", ">0%"),
                ["b"] = Rule("b", "loss", ">50%"),
            };

            var engine = new AlertEngine(rules);
            var events = engine.Evaluate(Target("a", "b"), SampleWith(10, 10), Now(1));

            Assert.Equal(2, events.Count, "both rules notify when neither has a priority");
        });

        runner.Add("AlertEngine: history starts with the S marker", () =>
        {
            var rule = Rule("first", "loss", "==S,==0%");
            var engine = new AlertEngine(new Dictionary<string, CompiledAlertRule> { ["first"] = rule });

            var events = engine.Evaluate(Target("first"), SampleWith(10, 0), Now(1));

            Assert.Equal(1, events.Count, "the very first round follows the start marker");
        });

        runner.Add("AlertEngine: alerts are tracked per target", () =>
        {
            var rule = Rule("down", "loss", "==100%");
            var engine = new AlertEngine(new Dictionary<string, CompiledAlertRule> { ["down"] = rule });

            engine.Evaluate(Target(["down"], "a"), SampleWith(10, 10), Now(1));
            engine.Evaluate(Target(["down"], "b"), SampleWith(10, 0), Now(1));

            Assert.Equal(1, engine.Active.Count, "only the broken target has an active alert");
            Assert.True(engine.Active.ContainsKey("a/down"), "and it is the right one");
        });
    }

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
