using System.Net;
using System.Net.Sockets;
using SmokePing.Net.Configuration;
using SmokePing.Net.Probes;
using SmokePing.Net.Services;
using SmokePing.Net.Storage;

namespace SmokePing.Net.Tests;

public static class ProbeTests
{
    public static void Register(TestRunner runner)
    {
        runner.Add("ProbeRegistry: probes resolve by their configuration name", () =>
        {
            using var httpClientFactory = new SimpleHttpClientFactory();
            var registry = new ProbeRegistry(
                [new IcmpProbe(), new TcpProbe(), new DnsProbe(), new HttpProbe(httpClientFactory)]);

            Assert.Equal("icmp", registry.Get("icmp").Name, "icmp resolves");
            Assert.Equal("tcp", registry.Get("TCP").Name, "names are case insensitive");
            Assert.True(registry.Contains("dns"), "dns is registered");
            Assert.False(registry.Contains("carrier-pigeon"), "unknown probes are reported as missing");
            Assert.Throws<KeyNotFoundException>(() => registry.Get("nope"), "and cannot be resolved");
        });

        runner.Add("ProbeBase: every probe in a round is reported, failures included", () =>
        {
            var probe = new ScriptedProbe([12.0, null, 8.0]);
            var results = probe.MeasureAsync(Target(pings: 3), CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(3, results.Length, "one entry per probe sent");
            Assert.Close(12.0, results[0]!.Value, 0.001, "the first measurement is kept");
            Assert.True(results[1] is null, "a lost probe stays null");
        });

        runner.Add("ProbeBase: an exception becomes a lost probe, not a failed round", () =>
        {
            var probe = new ScriptedProbe([10.0, null, 10.0], throwOnIndex: 1);
            var results = probe.MeasureAsync(Target(pings: 3), CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(3, results.Length, "the round completes");
            Assert.True(results[1] is null, "the failing probe counts as loss");
            Assert.Close(10.0, results[2]!.Value, 0.001, "later probes still run");
        });

        runner.Add("ProbeBase: cancellation stops the round", () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var probe = new ScriptedProbe([10.0, 10.0]);

            Assert.Throws<OperationCanceledException>(
                () => probe.MeasureAsync(Target(pings: 2), cancellation.Token).GetAwaiter().GetResult(),
                "a cancelled round does not silently return partial data");
        });

        runner.Add("ProbeBase: a round of results turns into a storable sample", () =>
        {
            var probe = new ScriptedProbe([10.0, 20.0, null, 40.0]);
            var results = probe.MeasureAsync(Target(pings: 4), CancellationToken.None).GetAwaiter().GetResult();
            var (sent, lost, quantiles) = Quantiles.Compute(results);

            Assert.Equal(4, sent, "four probes were sent");
            Assert.Equal(1, lost, "one was lost");
            Assert.Close(20.0, quantiles[Sample.MedianIndex], 0.001, "the median ignores the lost probe");
        });

        runner.Add("TcpProbe: a listening port is measured, a closed one is loss", () =>
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var probe = new TcpProbe();
            var target = Target(pings: 1, host: "127.0.0.1", port: port);

            var measured = probe.MeasureAsync(target, CancellationToken.None).GetAwaiter().GetResult();
            Assert.True(measured[0] is not null, "connecting to a listening port succeeds");

            listener.Stop();

            var refused = probe.MeasureAsync(target, CancellationToken.None).GetAwaiter().GetResult();
            Assert.True(refused[0] is null, "connecting to a closed port counts as a lost probe");
        });

        runner.Add("Probes describe themselves for the graph subtitle", () =>
        {
            using var httpClientFactory = new SimpleHttpClientFactory();

            Assert.Contains(new IcmpProbe().Describe(Target()), "ICMP", "the icmp probe names itself");
            Assert.Contains(new TcpProbe().Describe(Target(port: 443)), "443", "the tcp probe names the port");
            Assert.Contains(
                new DnsProbe().Describe(Target(query: "example.com")),
                "example.com",
                "the dns probe names the query");
            Assert.Contains(
                new HttpProbe(httpClientFactory).Describe(Target(url: "https://example.com/")),
                "https://example.com/",
                "the http probe names the url");
        });
    }

    private static MeasuredTarget Target(
        int pings = 1,
        string host = "192.0.2.1",
        int? port = null,
        string? query = null,
        string? url = null) => new()
    {
        Id = "test",
        Title = "test",
        Host = host,
        ProbeType = "icmp",
        StepSeconds = 300,
        Pings = pings,
        PingIntervalMs = 0,
        TimeoutMs = 1000,
        Port = port,
        Query = query,
        Url = url,
        PacketSize = 56,
        AlertRules = [],
        ParentId = string.Empty,
    };

    /// <summary>A probe that replays a fixed set of results, so pacing and error handling can be tested.</summary>
    private sealed class ScriptedProbe : ProbeBase
    {
        private readonly double?[] _results;
        private readonly int _throwOnIndex;
        private int _index;

        public ScriptedProbe(double?[] results, int throwOnIndex = -1)
        {
            _results = results;
            _throwOnIndex = throwOnIndex;
        }

        public override string Name => "scripted";

        public override string Describe(MeasuredTarget target) => "scripted probe";

        protected override Task<double?> MeasureOnceAsync(MeasuredTarget target, CancellationToken cancellationToken)
        {
            var current = _index++;
            if (current == _throwOnIndex)
            {
                throw new InvalidOperationException("probe blew up");
            }

            return Task.FromResult(_results[current]);
        }
    }
}
