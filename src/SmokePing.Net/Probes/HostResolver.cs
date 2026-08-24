using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace SmokePing.Net.Probes;

/// <summary>
/// Turns the dynamic host tokens a configuration may use into real addresses.
///
/// Hard-coding a gateway address in a configuration file is wrong the moment the
/// machine moves to a different network, and it is wrong from the start on any
/// network that does not use the address the sample happens to name. Writing
/// <c>%gateway%</c> instead makes the configuration portable.
///
/// Resolution reads the operating system's routing and DNS configuration through
/// <see cref="NetworkInterface"/>, which needs no elevation on any supported platform.
/// Results are cached briefly rather than for the process lifetime, so a laptop that
/// moves networks starts measuring the new gateway without a restart.
/// </summary>
public sealed class HostResolver
{
    /// <summary>The default gateway of the active interface.</summary>
    public const string GatewayToken = "%gateway%";

    /// <summary>The first DNS server the active interface is configured with.</summary>
    public const string DnsToken = "%dns%";

    /// <summary>
    /// How long a resolved address is reused. Short enough to follow a network
    /// change within a round or two, long enough not to re-read the routing table
    /// for every probe.
    /// </summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    private readonly ILogger<HostResolver> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string Address, DateTimeOffset Expires)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public HostResolver(ILogger<HostResolver> logger, TimeProvider timeProvider)
    {
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>True when the host is a token that needs resolving.</summary>
    public static bool IsToken(string host) =>
        host.Equals(GatewayToken, StringComparison.OrdinalIgnoreCase) ||
        host.Equals(DnsToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every token this resolver understands, for error messages.</summary>
    public static IReadOnlyList<string> Tokens => [GatewayToken, DnsToken];

    /// <summary>
    /// Resolves a host. A plain host name or address is returned unchanged; a token is
    /// looked up. Returns null when a token cannot be resolved right now, which the
    /// caller should treat as a round that could not be measured.
    /// </summary>
    public string? Resolve(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (!IsToken(host))
        {
            return host;
        }

        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            if (_cache.TryGetValue(host, out var cached) && cached.Expires > now)
            {
                return cached.Address;
            }

            var resolved = host.Equals(GatewayToken, StringComparison.OrdinalIgnoreCase)
                ? FindGateway()
                : FindDnsServer();

            if (resolved is null)
            {
                _logger.LogWarning("Could not resolve {Token}; no suitable network interface was found.", host);
                _cache.Remove(host);
                return null;
            }

            if (!_cache.TryGetValue(host, out var previous) || previous.Address != resolved)
            {
                _logger.LogInformation("{Token} resolved to {Address}.", host, resolved);
            }

            _cache[host] = (resolved, now + CacheDuration);
            return resolved;
        }
    }

    /// <summary>
    /// Finds the default gateway. Interfaces are considered in the order the operating
    /// system reports them, skipping loopback, tunnels and anything that is down, and
    /// ignoring the all-zeroes placeholder some drivers report for "no gateway".
    /// </summary>
    private static string? FindGateway() =>
        UsableInterfaces()
            .SelectMany(nic => nic.GetIPProperties().GatewayAddresses)
            .Select(gateway => gateway.Address)
            .Where(IsUsableAddress)
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .FirstOrDefault()
            ?.ToString();

    private static string? FindDnsServer() =>
        UsableInterfaces()
            .SelectMany(nic => nic.GetIPProperties().DnsAddresses)
            .Where(IsUsableAddress)
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .FirstOrDefault()
            ?.ToString();

    private static IEnumerable<NetworkInterface> UsableInterfaces() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
            .Where(nic => nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel));

    private static bool IsUsableAddress(IPAddress address)
    {
        if (IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address))
        {
            return false;
        }

        // A link-local IPv6 gateway is real and routable for this purpose, but a
        // link-local IPv4 address means DHCP failed.
        return !(address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes() is [169, 254, ..]);
    }
}
