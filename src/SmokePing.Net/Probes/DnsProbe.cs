using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SmokePing.Net.Configuration;

namespace SmokePing.Net.Probes;

/// <summary>
/// Times a DNS lookup against a specific server, like upstream's AnotherDNS probe.
/// The query is built and parsed here rather than going through the system resolver,
/// so the measurement is of the named server and is never served from a local cache.
/// </summary>
public sealed class DnsProbe : ProbeBase
{
    public const int DefaultPort = 53;
    public const string DefaultQuery = "localhost";

    public override string Name => "dns";

    public override string Describe(MeasuredTarget target) =>
        $"{target.Pings} DNS lookups of {target.Query ?? DefaultQuery} against " +
        $"{target.Host}:{target.Port ?? DefaultPort} every {target.StepSeconds}s";

    protected override async Task<double?> MeasureOnceAsync(MeasuredTarget target, CancellationToken cancellationToken)
    {
        var endpoint = await ResolveServerAsync(target, cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            return null;
        }

        // A fresh transaction id per probe stops a reply to an earlier, timed out
        // query from being mistaken for the answer to this one.
        var transactionId = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var query = BuildQuery(transactionId, target.Query ?? DefaultQuery);

        using var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(target.TimeoutMs);

        var buffer = new byte[512];
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await socket.SendToAsync(query, SocketFlags.None, endpoint, timeout.Token).ConfigureAwait(false);

            while (true)
            {
                var received = await socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, endpoint, timeout.Token)
                    .ConfigureAwait(false);

                if (received.ReceivedBytes >= 2 &&
                    BinaryPrimitives.ReadUInt16BigEndian(buffer) == transactionId)
                {
                    stopwatch.Stop();
                    return stopwatch.Elapsed.TotalMilliseconds;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<IPEndPoint?> ResolveServerAsync(MeasuredTarget target, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(target.Host, out var address))
        {
            return new IPEndPoint(address, target.Port ?? DefaultPort);
        }

        var addresses = await Dns.GetHostAddressesAsync(target.Host, cancellationToken).ConfigureAwait(false);
        return addresses.Length == 0 ? null : new IPEndPoint(addresses[0], target.Port ?? DefaultPort);
    }

    /// <summary>Builds a standard recursive A query (RFC 1035 section 4.1).</summary>
    public static byte[] BuildQuery(ushort transactionId, string name)
    {
        var labels = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var length = 12 + labels.Sum(label => 1 + Encoding.ASCII.GetByteCount(label)) + 1 + 4;
        var packet = new byte[length];

        BinaryPrimitives.WriteUInt16BigEndian(packet, transactionId);
        packet[2] = 0x01; // standard query, recursion desired
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 1); // one question

        var offset = 12;
        foreach (var label in labels)
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length > 63)
            {
                throw new ArgumentException($"DNS label '{label}' is longer than 63 bytes.", nameof(name));
            }

            packet[offset++] = (byte)bytes.Length;
            bytes.CopyTo(packet, offset);
            offset += bytes.Length;
        }

        packet[offset++] = 0; // root label
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset), 1); // QTYPE A
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset + 2), 1); // QCLASS IN
        return packet;
    }
}
