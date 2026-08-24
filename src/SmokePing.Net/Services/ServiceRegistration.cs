using Microsoft.Extensions.DependencyInjection;
using SmokePing.Net.Alerting;
using SmokePing.Net.Configuration;
using SmokePing.Net.Probes;
using SmokePing.Net.Rrd;
using SmokePing.Net.Storage;

namespace SmokePing.Net.Services;

/// <summary>Wires up everything the daemon needs.</summary>
public static class ServiceRegistration
{
    /// <summary>
    /// Registers the storage, probes and alerting services.
    ///
    /// The notifier and the data store must be genuine singletons: the polling loop
    /// writes to them and the web API reads from them, and a second instance would
    /// silently serve an empty view of the world.
    /// </summary>
    public static IServiceCollection AddSmokePingServices(
        this IServiceCollection services,
        LoadedConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.AddSingleton(config);
        services.AddSingleton(TimeProvider.System);
        // A native configuration brings its own archive table; otherwise the default
        // plan applies, which is the same table upstream ships.
        var plan = config.Raw.Database.Archives
            .Where(archive => archive.Steps > 0 && archive.Rows > 0)
            .Select(archive => (archive.Steps, archive.Rows))
            .ToList();

        services.AddSingleton(new DataStore(config.DataDirectory, plan));

        // RRDtool storage is opt-in: it is what lets an existing installation's files
        // be used in place, at the cost of rewriting the whole file on every round.
        if (config.Raw.Database.Format.Equals("rrd", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton(new RrdTargetStore(config.DataDirectory, plan));
        }
        services.AddSingleton(new AlertEngine(config.Alerts));
        services.AddSingleton<AlertNotifier>();
        services.AddSingleton<HostResolver>();

        // Named clients rather than AddHttpClient<T>, which would re-register the
        // service type itself as transient and undo the singletons above. Separate
        // clients also stop a slow webhook from starving the HTTP probe.
        services.AddHttpClient(AlertNotifier.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddHttpClient(HttpProbe.HttpClientName, ConfigureProbeClient);

        services.AddSingleton<IProbe, IcmpProbe>();
        services.AddSingleton<IProbe, TcpProbe>();
        services.AddSingleton<IProbe, DnsProbe>();
        services.AddSingleton<IProbe, HttpProbe>();
        services.AddSingleton(sp => new ProbeRegistry(sp.GetRequiredService<IEnumerable<IProbe>>()));

        return services;
    }

    private static void ConfigureProbeClient(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SmokePing.NET/1.0");
    }
}
