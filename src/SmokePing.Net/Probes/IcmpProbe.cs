using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using SmokePing.Net.Configuration;

namespace SmokePing.Net.Probes;

/// <summary>
/// ICMP echo probe, the equivalent of upstream's FPing probe. On Windows this uses
/// the IP Helper API through <see cref="Ping"/> and needs no special privileges,
/// which is the main reason a native Windows port is worth having.
/// </summary>
public sealed class IcmpProbe : ProbeBase
{
    public override string Name => "icmp";

    public override string Describe(MeasuredTarget target) =>
        $"ICMP Echo Ping ({target.PacketSize} bytes payload, {target.Pings} pings every {target.StepSeconds}s)";

    protected override async Task<double?> MeasureOnceAsync(MeasuredTarget target, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        var payload = CreatePayload(target.PacketSize);
        var options = new PingOptions { DontFragment = false };

        // Ping.Roundtrip time is only millisecond-accurate, which is too coarse on a
        // LAN, so time the call ourselves and use the reported value as a sanity floor.
        var stopwatch = Stopwatch.StartNew();
        var reply = await ping
            .SendPingAsync(target.Host, target.TimeoutMs, payload, options)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();

        if (reply.Status != IPStatus.Success)
        {
            return null;
        }

        var measured = stopwatch.Elapsed.TotalMilliseconds;
        return measured >= reply.RoundtripTime ? measured : reply.RoundtripTime;
    }

    /// <summary>Builds a payload of the requested size, like fping's -b option.</summary>
    private static byte[] CreatePayload(int size)
    {
        var payload = new byte[Math.Clamp(size, 0, 65000)];
        var filler = Encoding.ASCII.GetBytes("smokeping.net");
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = filler[i % filler.Length];
        }

        return payload;
    }
}
