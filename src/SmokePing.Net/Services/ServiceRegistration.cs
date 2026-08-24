using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    /// <param name="poll">
    /// Registers the measurement loop. Off for a web interface that only serves what
    /// is already stored.
    /// </param>
    public static IServiceCollection AddSmokePingServices(
        this IServiceCollection services,
        LoadedConfiguration config,
        bool poll = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.AddSingleton(config);
        services.AddSingleton(TimeProvider.System);

        // One store, whichever format is configured, so nothing downstream has to
        // ask for a service that might not have been registered - an optional
        // dependency the container cannot supply is a failure at request time.
        services.AddSingleton(new MeasurementStore(config));

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

        // Registered here rather than at the call site so that building the container
        // proves the polling loop's dependencies can actually be supplied.
        if (poll)
        {
            services.AddHostedService<PollingService>();
        }

        return services;
    }

    private static void ConfigureProbeClient(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SmokePing.NET/1.0");
    }
}
