using System.Net;
using System.Net.Sockets;
using SmokePing.Net.Configuration;
using SmokePing.Net.Probes;
using SmokePing.Net.Services;
using SmokePing.Net.Storage;
using Xunit;

namespace SmokePing.Net.Tests;

public sealed class ProbeTests
{
    /// <summary>HostResolver: a plain host is returned untouched</summary>
    [Fact]
    public void HostResolver_A_Plain_Host_Is_Returned_Untouched()
    {
            var resolver = NewResolver();

            Verify.Equal("1.1.1.1", resolver.Resolve("1.1.1.1"), "an address is not a token");
            Verify.Equal("example.com", resolver.Resolve("example.com"), "nor is a host name");
            Verify.False(HostResolver.IsToken("example.com"), "and it is not reported as one");
    }

    /// <summary>HostResolver: the gateway token resolves to a real address</summary>
    [Fact]
    public void HostResolver_The_Gateway_Token_Resolves_To_A_Real_Address()
    {
            var resolved = NewResolver().Resolve(HostResolver.GatewayToken);

            // A machine with no default route is possible, so only assert the shape
            // when something was found.
            if (resolved is null)
            {
                return;
            }

            Verify.True(IPAddress.TryParse(resolved, out _), $"'{resolved}' is a usable address");
            Verify.False(resolved.StartsWith("169.254.", StringComparison.Ordinal), "a DHCP failure is not a gateway");
            Verify.False(resolved is "0.0.0.0" or "::", "the all-zeroes placeholder is not a gateway");
    }

    /// <summary>HostResolver: tokens are recognised whatever the casing</summary>
    [Fact]
    public void HostResolver_Tokens_Are_Recognised_Whatever_The_Casing()
    {
            Verify.True(HostResolver.IsToken("%gateway%"), "the gateway token");
            Verify.True(HostResolver.IsToken("%GATEWAY%"), "in upper case too");
            Verify.True(HostResolver.IsToken("%dns%"), "the dns token");
            Verify.False(HostResolver.IsToken("%router%"), "an unknown token is not one of ours");
    }

    /// <summary>HostResolver: a resolved address is cached, not looked up per probe</summary>
    [Fact]
    public void HostResolver_A_Resolved_Address_Is_Cached_Not_Looked_Up_Per_Probe()
    {
            var resolver = NewResolver();
            var first = resolver.Resolve(HostResolver.GatewayToken);
            var second = resolver.Resolve(HostResolver.GatewayToken);

            Verify.Equal(first, second, "the same address comes back within the cache window");
    }

    /// <summary>ProbeRegistry: probes resolve by their configuration name</summary>
    [Fact]
    public void ProbeRegistry_Probes_Resolve_By_Their_Configuration_Name()
    {
            using var httpClientFactory = new SimpleHttpClientFactory();
            var registry = new ProbeRegistry(
                [new IcmpProbe(), new TcpProbe(), new DnsProbe(), new HttpProbe(httpClientFactory)]);

            Verify.Equal("icmp", registry.Get("icmp").Name, "icmp resolves");
            Verify.Equal("tcp", registry.Get("TCP").Name, "names are case insensitive");
            Verify.True(registry.Contains("dns"), "dns is registered");
            Verify.False(registry.Contains("carrier-pigeon"), "unknown probes are reported as missing");
            Verify.Throws<KeyNotFoundException>(() => registry.Get("nope"), "and cannot be resolved");
    }

    /// <summary>ProbeBase: every probe in a round is reported, failures included</summary>
    [Fact]
    public async Task ProbeBase_Every_Probe_In_A_Round_Is_Reported_Failures_Included()
    {
            var probe = new ScriptedProbe([12.0, null, 8.0]);
            var results =await  probe.MeasureAsync(Target(pings: 3), CancellationToken.None);

            Verify.Equal(3, results.Length, "one entry per probe sent");
            Verify.Close(12.0, results[0]!.Value, 0.001, "the first measurement is kept");
            Verify.True(results[1] is null, "a lost probe stays null");
    }

    /// <summary>ProbeBase: an exception becomes a lost probe, not a failed round</summary>
    [Fact]
    public async Task ProbeBase_An_Exception_Becomes_A_Lost_Probe_Not_A_Failed_Round()
    {
            var probe = new ScriptedProbe([10.0, null, 10.0], throwOnIndex: 1);
            var results =await  probe.MeasureAsync(Target(pings: 3), CancellationToken.None);

            Verify.Equal(3, results.Length, "the round completes");
            Verify.True(results[1] is null, "the failing probe counts as loss");
            Verify.Close(10.0, results[2]!.Value, 0.001, "later probes still run");
    }

    /// <summary>ProbeBase: cancellation stops the round</summary>
    [Fact]
    public async Task ProbeBase_Cancellation_Stops_The_Round()
    {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var probe = new ScriptedProbe([10.0, 10.0]);

            await Verify.ThrowsAsync<OperationCanceledException>(
                () => probe.MeasureAsync(Target(pings: 2), cancellation.Token),
                "a cancelled round does not silently return partial data");
    }

    /// <summary>ProbeBase: a round of results turns into a storable sample</summary>
    [Fact]
    public async Task ProbeBase_A_Round_Of_Results_Turns_Into_A_Storable_Sample()
    {
            var probe = new ScriptedProbe([10.0, 20.0, null, 40.0]);
            var results =await  probe.MeasureAsync(Target(pings: 4), CancellationToken.None);
            var (sent, lost, median, _) = Quantiles.Compute(results);

            Verify.Equal(4, sent, "four probes were sent");
            Verify.Equal(1, lost, "one was lost");
            Verify.Close(20.0, median, 0.001, "the median is the middle of what came back");
    }

    /// <summary>TcpProbe: a listening port is measured, a closed one is loss</summary>
    [Fact]
    public async Task TcpProbe_A_Listening_Port_Is_Measured_A_Closed_One_Is_Loss()
    {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var probe = new TcpProbe();
            var target = Target(pings: 1, host: "127.0.0.1", port: port);

            var measured =await  probe.MeasureAsync(target, CancellationToken.None);
            Verify.True(measured[0] is not null, "connecting to a listening port succeeds");

            listener.Stop();

            var refused =await  probe.MeasureAsync(target, CancellationToken.None);
            Verify.True(refused[0] is null, "connecting to a closed port counts as a lost probe");
    }

    /// <summary>Probes describe themselves for the graph subtitle</summary>
    [Fact]
    public void Probes_Describe_Themselves_For_The_Graph_Subtitle()
    {
            using var httpClientFactory = new SimpleHttpClientFactory();

            Verify.Contains(new IcmpProbe().Describe(Target()), "ICMP", "the icmp probe names itself");
            Verify.Contains(new TcpProbe().Describe(Target(port: 443)), "443", "the tcp probe names the port");
            Verify.Contains(
                new DnsProbe().Describe(Target(query: "example.com")),
                "example.com",
                "the dns probe names the query");
            Verify.Contains(
                new HttpProbe(httpClientFactory).Describe(Target(url: "https://example.com/")),
                "https://example.com/",
                "the http probe names the url");
    }


    

    private static HostResolver NewResolver() => new(
        Microsoft.Extensions.Logging.Abstractions.NullLogger<HostResolver>.Instance,
        TimeProvider.System);

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
        Traced = false,
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
