using Microsoft.Extensions.Logging.Abstractions;
using SmokePing.Net.Configuration;
using SmokePing.Net.Probes;
using SmokePing.Net.Services;
using Xunit;

namespace SmokePing.Net.Tests;

/// <summary>
/// Tests for the live, once-a-second view. What matters here is not the measurement -
/// that is the same probe the scheduler uses - but that a session is shared between
/// watchers and stops when they leave, because "every second" is three hundred times
/// the load of a normal round.
/// </summary>
public sealed class LiveProbeTests
{
    [Fact]
    public async Task Live_Streams_Samples_As_They_Are_Measured()
    {
        var probe = new CountingProbe();
        var service = NewService(probe);

        var samples = await TakeAsync(service, Target(), count: 3);

        Verify.Equal(3, samples.Count, "three samples arrived");
        Verify.Close(1, samples[0].RoundTripMilliseconds!.Value, 0.001, "carrying what the probe measured");
        Verify.Close(2, samples[1].RoundTripMilliseconds!.Value, 0.001, "in order");
    }

    [Fact]
    public async Task Live_A_Lost_Probe_Is_Reported_Rather_Than_Skipped()
    {
        var probe = new CountingProbe { LoseEvery = 2 };
        var service = NewService(probe);

        var samples = await TakeAsync(service, Target(), count: 4);

        Verify.True(
            samples.Any(s => s.RoundTripMilliseconds is null),
            "a lost probe appears in the stream as a gap, not as a missing sample");
    }

    [Fact]
    public async Task Live_Two_Watchers_Share_One_Session()
    {
        var probe = new CountingProbe();
        var service = NewService(probe);
        var target = Target();

        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Watching the same target twice must not double the probing.
        var first = TakeAsync(service, target, count: 3, stopping.Token);
        var second = TakeAsync(service, target, count: 3, stopping.Token);

        var results = await Task.WhenAll(first, second);

        Verify.Equal(3, results[0].Count, "the first watcher got its samples");
        Verify.Equal(3, results[1].Count, "and so did the second");
        Verify.True(
            probe.Calls <= 8,
            $"one probe per tick, not one per watcher (probe ran {probe.Calls} times)");
    }

    [Fact]
    public async Task Live_The_Session_Stops_When_The_Last_Watcher_Leaves()
    {
        var probe = new CountingProbe();
        var service = NewService(probe);

        await TakeAsync(service, Target(), count: 2);

        Verify.Equal(0, service.ActiveSessions, "the session is gone once nobody is watching");

        var afterLeaving = probe.Calls;
        await Task.Delay(120);

        Verify.Equal(afterLeaving, probe.Calls, "and the probing has actually stopped");
    }

    [Fact]
    public async Task Live_A_Late_Watcher_Sees_What_It_Missed()
    {
        var probe = new CountingProbe();
        var service = NewService(probe);
        var target = Target();

        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var holder = TakeAsync(service, target, count: 6, stopping.Token);
        await Task.Delay(150);

        var late = await TakeAsync(service, target, count: 2, stopping.Token);
        await holder;

        Verify.True(late.Count >= 2, "a watcher joining midway is given the recent history");
    }

    [Fact]
    public async Task Live_Only_So_Many_Sessions_May_Run_At_Once()
    {
        var service = NewService(new CountingProbe());
        var started = new List<CancellationTokenSource>();

        try
        {
            // Each open tab is a session; without a cap they would flood a network.
            for (var i = 0; i < LiveProbeService.MaximumSessions; i++)
            {
                var stopping = new CancellationTokenSource();
                started.Add(stopping);
                _ = TakeAsync(service, Target($"target{i}"), count: 100, stopping.Token);
            }

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (service.ActiveSessions < LiveProbeService.MaximumSessions && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            await Verify.ThrowsAsync<InvalidOperationException>(
                () => TakeAsync(service, Target("one-too-many"), count: 1),
                "the session after the limit is refused with a reason");
        }
        finally
        {
            foreach (var stopping in started)
            {
                await stopping.CancelAsync();
                stopping.Dispose();
            }
        }
    }

    private static LiveProbeService NewService(IProbe probe) => new(
        new ProbeRegistry([probe]),
        new HostResolver(NullLogger<HostResolver>.Instance, TimeProvider.System),
        NullLogger<LiveProbeService>.Instance,
        TimeProvider.System);

    /// <summary>Reads a fixed number of samples, then stops watching.</summary>
    private static async Task<List<LiveSample>> TakeAsync(
        LiveProbeService service,
        MeasuredTarget target,
        int count,
        CancellationToken cancellationToken = default)
    {
        var samples = new List<LiveSample>();

        await foreach (var sample in service.WatchAsync(target, intervalMs: 20, cancellationToken))
        {
            samples.Add(sample);
            if (samples.Count >= count)
            {
                break;
            }
        }

        return samples;
    }

    private static MeasuredTarget Target(string id = "group/host") => new()
    {
        Id = id,
        Title = "host",
        Host = "192.0.2.1",
        ProbeType = "counting",
        StepSeconds = 300,
        Pings = 20,
        PingIntervalMs = 500,
        TimeoutMs = 1000,
        PacketSize = 56,
        AlertRules = [],
        ParentId = "group",
    };

    /// <summary>A probe that answers instantly and counts how often it was asked.</summary>
    private sealed class CountingProbe : ProbeBase
    {
        private int _calls;

        public override string Name => "counting";

        /// <summary>When set, every Nth probe is reported lost.</summary>
        public int LoseEvery { get; init; }

        public int Calls => Volatile.Read(ref _calls);

        public override string Describe(MeasuredTarget target) => "counting probe";

        protected override Task<double?> MeasureOnceAsync(MeasuredTarget target, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            var lost = LoseEvery > 0 && call % LoseEvery == 0;
            return Task.FromResult<double?>(lost ? null : call);
        }
    }
}
