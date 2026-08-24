using SmokePing.Net.Configuration;

namespace SmokePing.Net.Probes;

/// <summary>
/// Measures one round of round-trip times for a target. Implementations return one
/// entry per probe sent, in send order, using null for a lost or failed probe.
/// </summary>
public interface IProbe
{
    /// <summary>Probe name as used in the configuration ("icmp", "tcp", "dns", "http").</summary>
    string Name { get; }

    /// <summary>Short description shown under the graphs, e.g. "ICMP Echo Ping (56 bytes)".</summary>
    string Describe(MeasuredTarget target);

    /// <summary>
    /// Sends <see cref="MeasuredTarget.Pings"/> probes, spaced by
    /// <see cref="MeasuredTarget.PingIntervalMs"/>, and returns their round-trip times
    /// in milliseconds.
    /// </summary>
    Task<double?[]> MeasureAsync(MeasuredTarget target, CancellationToken cancellationToken);
}
