using Microsoft.Extensions.Logging.Abstractions;
using SmokePing.Net.Configuration;
using SmokePing.Net.Probes;
using SmokePing.Net.Services;
using SmokePing.Net.Storage;
using Xunit;

namespace SmokePing.Net.Tests;

/// <summary>
/// The recorded trace, which exists to answer one question: was there a moment when
/// this link misbehaved, and when was it? Everything here is a variation on that -
/// that a bad second is written down, that consolidating a good minute around it does
/// not average it away, and that reading it back finds it and names the time.
/// </summary>
public sealed class TraceTests
{
    private static readonly (int Multiplier, int SlotCount)[] Plan = [(1, 600), (60, 120)];

    /// <summary>TraceFile: a recorded probe reads back exactly</summary>
    [Fact]
    public void TraceFile_A_Recorded_Probe_Reads_Back_Exactly()
    {
        using var directory = new TempDirectory();
        using var file = TraceFile.OpenOrCreate(directory.File("t.sptr"), 1, Plan);

        file.Record(1_000_000, 12.5, 1.25f);

        var samples = file.Read(0, 1_000_000, 1_000_000);
        Verify.Equal(1, samples.Count, "one second was asked for");
        Verify.Close(12.5, samples[0].Mean, 0.001, "the round trip time is what was recorded");
        Verify.Close(12.5, samples[0].Maximum, 0.001, "and at this resolution it is also the maximum");
        Verify.Close(1.25, samples[0].Jitter, 0.001, "as is the jitter");
        Verify.Equal(1, samples[0].Sent, "one probe was sent");
        Verify.Equal(0, samples[0].Lost, "and it came back");
    }

    /// <summary>TraceFile: a lost probe is recorded as lost, not as missing</summary>
    [Fact]
    public void TraceFile_A_Lost_Probe_Is_Recorded_As_Lost_Not_As_Missing()
    {
        using var directory = new TempDirectory();
        using var file = TraceFile.OpenOrCreate(directory.File("t.sptr"), 1, Plan);

        file.Record(1_000_000, null, float.NaN);

        var sample = file.Read(0, 1_000_000, 1_000_000)[0];

        // The difference matters: a lost probe says the network dropped it, a gap says
        // nobody asked. Drawn the same they would be, and diagnosed the same they
        // would be wrong.
        Verify.True(sample.HasData, "the second was measured");
        Verify.False(sample.HasAnswer, "but nothing came back");
        Verify.Equal(1, sample.Lost, "so it counts as a loss");
    }

    /// <summary>TraceFile: a slot never written comes back as a gap</summary>
    [Fact]
    public void TraceFile_A_Slot_Never_Written_Comes_Back_As_A_Gap()
    {
        using var directory = new TempDirectory();
        using var file = TraceFile.OpenOrCreate(directory.File("t.sptr"), 1, Plan);

        file.Record(1_000_000, 10, float.NaN);

        var samples = file.Read(0, 999_995, 1_000_000);
        Verify.Equal(6, samples.Count, "six seconds were asked for");
        Verify.False(samples[0].HasData, "the seconds before the recorder started are gaps");
        Verify.True(samples[^1].HasData, "and the one it recorded is not");
    }

    /// <summary>TraceFile: one bad second survives a minute of good ones</summary>
    [Fact]
    public void TraceFile_One_Bad_Second_Survives_A_Minute_Of_Good_Ones()
    {
        using var directory = new TempDirectory();
        using var file = TraceFile.OpenOrCreate(directory.File("t.sptr"), 1, Plan);

        // A whole minute at 10ms with a single 400ms stall in the middle of it.
        var start = 1_800_000L;
        for (var i = 0; i < 60; i++)
        {
            file.Record(start + i, i == 30 ? 400 : 10, 0.5f);
        }

        var minute = file.Read(1, start, start)[0];

        Verify.Equal(60, minute.Sent, "the summary covers the whole minute");
        Verify.Close(10, minute.Minimum, 0.01, "the good seconds set the minimum");
        Verify.Close(400, minute.Maximum, 0.01, "and the bad one is still the maximum");

        // This is the whole reason the maximum is stored. The mean of this minute is
        // 16.5ms, which is indistinguishable from a slightly busy link; keeping only
        // the mean would have erased the stall entirely.
        Verify.Close(16.5, minute.Mean, 0.1, "while the mean has almost forgotten it");
    }

    /// <summary>TraceFile: a backwards clock does not overwrite newer readings</summary>
    [Fact]
    public void TraceFile_A_Backwards_Clock_Does_Not_Overwrite_Newer_Readings()
    {
        using var directory = new TempDirectory();
        using var file = TraceFile.OpenOrCreate(directory.File("t.sptr"), 1, Plan);

        file.Record(1_000_100, 10, float.NaN);
        file.Record(1_000_050, 999, float.NaN);

        Verify.False(
            file.Read(0, 1_000_050, 1_000_050)[0].HasData,
            "a reading from before the newest one is refused, not written");
        Verify.Close(
            10,
            file.Read(0, 1_000_100, 1_000_100)[0].Mean,
            0.01,
            "and the newest reading is untouched");
    }

    /// <summary>TraceFile: history survives being closed and reopened</summary>
    [Fact]
    public void TraceFile_History_Survives_Being_Closed_And_Reopened()
    {
        using var directory = new TempDirectory();
        var path = directory.File("t.sptr");

        using (var file = TraceFile.OpenOrCreate(path, 1, Plan))
        {
            file.Record(1_000_000, 42, 3f);
            file.Flush(toDisk: true);
        }

        using var reopened = TraceFile.OpenOrCreate(path, 1, Plan);
        Verify.Close(42, reopened.Read(0, 1_000_000, 1_000_000)[0].Mean, 0.01, "the reading is still there");

        // And the guard against a backwards clock came back with it, rather than
        // resetting and allowing one rewrite of live data after every restart.
        reopened.Record(999_999, 1, float.NaN);
        Verify.False(reopened.Read(0, 999_999, 999_999)[0].HasData, "an older reading is still refused");
    }

    /// <summary>TraceFile: a file written for a different plan is replaced, not misread</summary>
    [Fact]
    public void TraceFile_A_File_Written_For_A_Different_Plan_Is_Replaced_Not_Misread()
    {
        using var directory = new TempDirectory();
        var path = directory.File("t.sptr");

        using (var file = TraceFile.OpenOrCreate(path, 1, Plan))
        {
            file.Record(1_000_000, 42, float.NaN);
            file.Flush(toDisk: true);
        }

        using var rebuilt = TraceFile.OpenOrCreate(path, 5, Plan);
        Verify.Equal(5, rebuilt.StepSeconds, "the file was rebuilt at the new resolution");
        Verify.False(rebuilt.Read(0, 1_000_000, 1_000_000)[0].HasData, "and holds nothing from before");
    }

    /// <summary>TraceFile: the coarsest tier is used for a span the fine one cannot reach</summary>
    [Fact]
    public void TraceFile_The_Coarsest_Tier_Is_Used_For_A_Span_The_Fine_One_Cannot_Reach()
    {
        using var directory = new TempDirectory();
        using var file = TraceFile.OpenOrCreate(directory.File("t.sptr"), 1, Plan);

        Verify.Equal(0, file.SelectTier(300), "five minutes fits in the fine tier");
        Verify.Equal(0, file.SelectTier(600), "and so does its whole span");
        Verify.Equal(1, file.SelectTier(601), "one second more than it holds falls to the summary");
        Verify.Equal(1, file.SelectTier(999_999), "as does anything longer than either");
    }

    /// <summary>TraceAnalysis: a spike on a fast link is found, and on a slow one it is not</summary>
    [Fact]
    public void TraceAnalysis_A_Spike_On_A_Fast_Link_Is_Found_And_On_A_Slow_One_It_Is_Not()
    {
        // 60ms is a disaster on a 5ms LAN and an ordinary Tuesday on a 55ms link.
        // A fixed threshold has to be wrong about one of them.
        var fast = Series(300, _ => 5).ToList();
        fast[100] = With(fast[100], 60);

        var slow = Series(300, _ => 55).ToList();
        slow[100] = With(slow[100], 60);

        var config = new SpikeConfig();

        var onFast = TraceAnalysis.FindEvents(fast, 1, TraceAnalysis.ComputeThresholds(fast, config));
        var onSlow = TraceAnalysis.FindEvents(slow, 1, TraceAnalysis.ComputeThresholds(slow, config));

        Verify.Equal(1, onFast.Count, "60ms stands out against 5ms");
        Verify.Equal(0, onSlow.Count, "and does not against 55ms");
    }

    /// <summary>TraceAnalysis: an event says when it happened and how bad it was</summary>
    [Fact]
    public void TraceAnalysis_An_Event_Says_When_It_Happened_And_How_Bad_It_Was()
    {
        var samples = Series(300, _ => 10).ToList();
        for (var i = 120; i < 124; i++)
        {
            samples[i] = With(samples[i], 150);
        }

        var thresholds = TraceAnalysis.ComputeThresholds(samples, new SpikeConfig());
        var events = TraceAnalysis.FindEvents(samples, 1, thresholds);

        Verify.Equal(1, events.Count, "the four bad seconds are one disturbance, not four");
        Verify.Equal(samples[120].Timestamp, events[0].Start, "it starts at the first bad second");
        Verify.Equal(4, events[0].DurationSeconds, "and lasts as long as it lasted");
        Verify.Close(150, events[0].PeakRoundTrip, 0.01, "the peak is the worst reading in it");
        Verify.True(events[0].Kinds.Contains("latency"), "and latency is what triggered it");
    }

    /// <summary>TraceAnalysis: a flickering disturbance is one event, not a dozen</summary>
    [Fact]
    public void TraceAnalysis_A_Flickering_Disturbance_Is_One_Event_Not_A_Dozen()
    {
        var samples = Series(300, _ => 10).ToList();

        // Bad, briefly better, bad again - which is what congestion actually looks
        // like. Reported as separate events it would read as a dozen problems.
        foreach (var i in (int[])[100, 101, 103, 105, 106])
        {
            samples[i] = With(samples[i], 200);
        }

        var events = TraceAnalysis.FindEvents(
            samples, 1, TraceAnalysis.ComputeThresholds(samples, new SpikeConfig()));

        Verify.Equal(1, events.Count, "one disturbance");
        Verify.Equal(samples[100].Timestamp, events[0].Start, "starting at the first bad second");
        Verify.Equal(7, events[0].DurationSeconds, "and running to the last one");
    }

    /// <summary>TraceAnalysis: a gap does not join two disturbances into one</summary>
    [Fact]
    public void TraceAnalysis_A_Gap_Does_Not_Join_Two_Disturbances_Into_One()
    {
        var samples = Series(300, _ => 10).ToList();
        samples[100] = With(samples[100], 200);
        samples[101] = TraceSample.Empty(samples[101].Timestamp);
        samples[102] = With(samples[102], 200);

        var events = TraceAnalysis.FindEvents(
            samples, 1, TraceAnalysis.ComputeThresholds(samples, new SpikeConfig()));

        // The daemon was not running for that second, so nothing is known about it.
        // Bridging across it would claim the trouble continued through a moment
        // nobody measured.
        Verify.Equal(2, events.Count, "a gap ends a disturbance rather than being bridged");
    }

    /// <summary>TraceAnalysis: loss is an event on its own, however fast the answers were</summary>
    [Fact]
    public void TraceAnalysis_Loss_Is_An_Event_On_Its_Own_However_Fast_The_Answers_Were()
    {
        var samples = Series(300, _ => 10).ToList();
        samples[50] = new TraceSample(
            samples[50].Timestamp, 1, 1, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN);

        var events = TraceAnalysis.FindEvents(
            samples, 1, TraceAnalysis.ComputeThresholds(samples, new SpikeConfig()));

        Verify.Equal(1, events.Count, "a dropped probe is worth reporting on its own");
        Verify.True(events[0].Kinds.Contains("loss"), "and it is reported as loss");
        Verify.Equal(1, events[0].Lost, "with the count of what went missing");
    }

    /// <summary>TraceAnalysis: a steady link reports nothing at all</summary>
    [Fact]
    public void TraceAnalysis_A_Steady_Link_Reports_Nothing_At_All()
    {
        // Real measurements are never identical, so this wobbles by a millisecond.
        // A detector that fires on that is a detector nobody will read twice.
        var samples = Series(600, i => 20 + ((i % 5) * 0.4)).ToList();

        var events = TraceAnalysis.FindEvents(
            samples, 1, TraceAnalysis.ComputeThresholds(samples, new SpikeConfig()));

        Verify.Equal(0, events.Count, "ordinary variation is not a peak");
    }

    /// <summary>TraceAnalysis: the worst events survive when there are too many to list</summary>
    [Fact]
    public void TraceAnalysis_The_Worst_Events_Survive_When_There_Are_Too_Many_To_List()
    {
        var samples = Series(1200, _ => 10).ToList();

        // Twenty disturbances, one of them far worse than the rest.
        for (var n = 0; n < 20; n++)
        {
            samples[100 + (n * 50)] = With(samples[100 + (n * 50)], n == 7 ? 900 : 100);
        }

        var events = TraceAnalysis.FindEvents(
            samples, 1, TraceAnalysis.ComputeThresholds(samples, new SpikeConfig()), maximumEvents: 5);

        Verify.Equal(5, events.Count, "the list is capped");
        Verify.True(
            events.Any(e => e.PeakRoundTrip > 800),
            "and what it drops is the mild ones, not the 900ms stall");
    }

    /// <summary>TraceAnalysis: thinning a series for drawing keeps its peaks</summary>
    [Fact]
    public void TraceAnalysis_Thinning_A_Series_For_Drawing_Keeps_Its_Peaks()
    {
        var samples = Series(3600, _ => 10).ToList();
        samples[1800] = With(samples[1800], 500);

        var drawn = TraceAnalysis.Downsample(samples, 200);

        Verify.True(drawn.Count <= 200, "the series fits the chart");
        Verify.True(
            drawn.Any(s => s.Maximum > 490),
            "and the one bad second in the hour is still in it");

        // The column holding it also reports its own mean, so the chart can show both
        // what the link was normally doing and what it did once.
        var column = drawn.First(s => s.Maximum > 490);
        Verify.True(column.Mean < 100, "the column's mean still describes the ordinary seconds");
    }

    /// <summary>TraceAnalysis: thinning a series short enough to draw leaves it alone</summary>
    [Fact]
    public void TraceAnalysis_Thinning_A_Series_Short_Enough_To_Draw_Leaves_It_Alone()
    {
        var samples = Series(100, _ => 10).ToList();
        Verify.Equal(100, TraceAnalysis.Downsample(samples, 900).Count, "nothing to thin");
    }

    /// <summary>TraceAnalysis: a window with no readings at all does not throw</summary>
    [Fact]
    public void TraceAnalysis_A_Window_With_No_Readings_At_All_Does_Not_Throw()
    {
        var samples = Enumerable.Range(0, 60).Select(i => TraceSample.Empty(1_000_000 + i)).ToList();

        var thresholds = TraceAnalysis.ComputeThresholds(samples, new SpikeConfig());
        var events = TraceAnalysis.FindEvents(samples, 1, thresholds);
        var summary = TraceAnalysis.Summarise(samples, events.Count);

        Verify.Equal(0, events.Count, "nothing was measured, so nothing happened");
        Verify.Equal(0, summary.Sent, "and nothing was sent");
        Verify.IsNaN(summary.MedianRoundTrip, "the median of no readings is not a number");
    }

    /// <summary>TraceStore: the tier plan follows the configuration</summary>
    [Fact]
    public void TraceStore_The_Tier_Plan_Follows_The_Configuration()
    {
        using var directory = new TempDirectory();
        var config = ConfigLoader.Build(
            new SmokePingConfig
            {
                General = { DataDirectory = directory.Path },
                Trace = { IntervalSeconds = 1, FineHours = 24, CoarseStepSeconds = 60, CoarseDays = 30 },
                Targets = [new TargetNode { Id = "a", Host = "192.0.2.1" }],
            },
            directory.File("smokeping.json"));

        using var store = new TraceStore(config);

        Verify.Equal(2, store.Plan.Count, "a fine tier and a summary");
        Verify.Equal(86400, store.Plan[0].SlotCount, "a day of seconds");
        Verify.Equal(60, store.Plan[1].Multiplier, "summarised a minute at a time");
        Verify.Equal(43200, store.Plan[1].SlotCount, "for thirty days");

        // Quoted in the documentation and in the start-up log, so it is pinned here.
        Verify.Close(4.5, store.BytesPerTarget / 1024.0 / 1024.0, 0.2, "about four and a half megabytes per target");
    }

    /// <summary>Configuration: a target inherits whether it is traced</summary>
    [Fact]
    public void Configuration_A_Target_Inherits_Whether_It_Is_Traced()
    {
        using var directory = new TempDirectory();
        var config = ConfigLoader.Build(
            new SmokePingConfig
            {
                General = { DataDirectory = directory.Path },
                Trace = { Enabled = true },
                Targets =
                [
                    new TargetNode { Id = "a", Host = "192.0.2.1" },
                    new TargetNode { Id = "b", Host = "192.0.2.2", Trace = false },
                    new TargetNode
                    {
                        Id = "group",
                        Trace = false,
                        Children = [new TargetNode { Id = "c", Host = "192.0.2.3" }],
                    },
                ],
            },
            directory.File("smokeping.json"));

        Verify.True(config.Targets.Single(t => t.Id == "a").Traced, "the section decides by default");
        Verify.False(config.Targets.Single(t => t.Id == "b").Traced, "a target can opt out");
        Verify.False(
            config.Targets.Single(t => t.Id == "group/c").Traced,
            "and opting a folder out opts its children out, like every other setting");
    }

    /// <summary>Configuration: a summary step that is not a multiple of the interval is refused</summary>
    [Fact]
    public void Configuration_A_Summary_Step_That_Is_Not_A_Multiple_Of_The_Interval_Is_Refused()
    {
        using var directory = new TempDirectory();

        var error = Verify.Throws<ConfigurationException>(
            () => ConfigLoader.Build(
                new SmokePingConfig
                {
                    General = { DataDirectory = directory.Path },
                    Trace = { IntervalSeconds = 7, CoarseStepSeconds = 60 },
                    Targets = [new TargetNode { Id = "a", Host = "192.0.2.1" }],
                },
                directory.File("smokeping.json")),
            "the tiers would not line up");

        Verify.Contains(error.Message, "multiple of", "and the message says why");
    }

    /// <summary>TraceRecorder: it records with nobody watching</summary>
    [Fact]
    public async Task TraceRecorder_It_Records_With_Nobody_Watching()
    {
        using var directory = new TempDirectory();
        var config = TraceConfiguration(directory, ("watched", true));
        using var store = new TraceStore(config);
        var probe = new CountingProbe();

        // Nothing subscribes, nothing asks, no browser is open. That is the point:
        // the recorder exists so that the moment nobody was watching is still there
        // to be found afterwards.
        var recorder = NewRecorder(config, store, probe);
        using var stopping = new CancellationTokenSource();

        await recorder.StartAsync(stopping.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(3200));
        await stopping.CancelAsync();
        await recorder.StopAsync(CancellationToken.None);

        var target = config.Targets.Single(t => t.Id == "watched");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var (samples, _) = store.Read(target, now - 30, now);
        var recorded = samples.Where(s => s.HasData).ToList();

        Verify.True(recorded.Count >= 2, $"seconds were recorded unprompted (got {recorded.Count})");
        Verify.True(probe.Calls >= 2, "because it kept probing");
        Verify.True(
            recorded.All(s => s.Sent == 1),
            "one probe per second, not a whole round");

        // The counting probe answers 1ms, 2ms, 3ms, so consecutive answers differ by
        // one and the recorder should have worked that out as it went.
        Verify.True(
            recorded.Skip(1).Any(s => !float.IsNaN(s.Jitter)),
            "and jitter was computed between consecutive answers");
    }

    /// <summary>TraceRecorder: a target that opted out is not recorded</summary>
    [Fact]
    public async Task TraceRecorder_A_Target_That_Opted_Out_Is_Not_Recorded()
    {
        using var directory = new TempDirectory();
        var config = TraceConfiguration(directory, ("watched", true), ("ignored", false));
        using var store = new TraceStore(config);

        var recorder = NewRecorder(config, store, new CountingProbe());
        using var stopping = new CancellationTokenSource();

        await recorder.StartAsync(stopping.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        await stopping.CancelAsync();
        await recorder.StopAsync(CancellationToken.None);

        // A probe a second is a real cost, so opting out has to actually stop it -
        // not merely hide the panel while the traffic carries on.
        Verify.True(File.Exists(directory.File("watched.sptr")), "the traced target has a file");
        Verify.False(File.Exists(directory.File("ignored.sptr")), "and the one that opted out does not");
    }

    private static TraceRecorder NewRecorder(LoadedConfiguration config, TraceStore store, IProbe probe) => new(
        config,
        new ProbeRegistry([probe]),
        store,
        new HostResolver(NullLogger<HostResolver>.Instance, TimeProvider.System),
        NullLogger<TraceRecorder>.Instance,
        TimeProvider.System);

    private static LoadedConfiguration TraceConfiguration(
        TempDirectory directory,
        params (string Id, bool Traced)[] targets) =>
        ConfigLoader.Build(
            new SmokePingConfig
            {
                General = { DataDirectory = directory.Path },
                Defaults = { Probe = "counting" },
                Trace = { Enabled = true, IntervalSeconds = 1, FineHours = 1, CoarseStepSeconds = 60, CoarseDays = 1 },
                Targets = [.. targets.Select(t => new TargetNode
                {
                    Id = t.Id,
                    Host = "192.0.2.1",
                    Trace = t.Traced,
                })],
            },
            directory.File("smokeping.json"));

    /// <summary>A probe that answers instantly and counts how often it was asked.</summary>
    private sealed class CountingProbe : ProbeBase
    {
        private int _calls;

        public override string Name => "counting";

        public int Calls => Volatile.Read(ref _calls);

        public override string Describe(MeasuredTarget target) => "counting probe";

        protected override Task<double?> MeasureOnceAsync(MeasuredTarget target, CancellationToken cancellationToken) =>
            Task.FromResult<double?>(Interlocked.Increment(ref _calls));
    }

    /// <summary>A steady series of answered probes, one a second.</summary>
    private static IEnumerable<TraceSample> Series(int count, Func<int, double> rtt)
    {
        var previous = double.NaN;
        for (var i = 0; i < count; i++)
        {
            var value = (float)rtt(i);
            var jitter = double.IsNaN(previous) ? float.NaN : (float)Math.Abs(value - previous);
            previous = value;
            yield return new TraceSample(1_700_000_000 + i, 1, 0, value, value, value, jitter, jitter);
        }
    }

    /// <summary>Replaces one reading's round trip time, leaving it a single answered probe.</summary>
    private static TraceSample With(TraceSample sample, float value) =>
        sample with { Minimum = value, Mean = value, Maximum = value };
}
