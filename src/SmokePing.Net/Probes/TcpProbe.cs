using System.Diagnostics;
using System.Net.Sockets;
using SmokePing.Net.Configuration;

namespace SmokePing.Net.Probes;

/// <summary>
/// Measures how long a TCP handshake to a port takes, like upstream's TCPPing probe.
/// Useful where ICMP is filtered, and it measures what users actually wait for.
/// </summary>
public sealed class TcpProbe : ProbeBase
{
    public const int DefaultPort = 80;

    public override string Name => "tcp";

    public override string Describe(MeasuredTarget target) =>
        $"TCP connect to port {target.Port ?? DefaultPort} ({target.Pings} probes every {target.StepSeconds}s)";

    protected override async Task<double?> MeasureOnceAsync(MeasuredTarget target, CancellationToken cancellationToken)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(target.TimeoutMs);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await socket
                .ConnectAsync(target.Host, target.Port ?? DefaultPort, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        stopwatch.Stop();

        // Close the connection politely rather than leaving it for the peer to time out.
        if (socket.Connected)
        {
            socket.Shutdown(SocketShutdown.Both);
        }

        return stopwatch.Elapsed.TotalMilliseconds;
    }
}
