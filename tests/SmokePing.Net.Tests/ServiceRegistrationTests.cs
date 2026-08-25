using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmokePing.Net.Alerting;
using SmokePing.Net.Configuration;
using SmokePing.Net.Probes;
using SmokePing.Net.Services;
using SmokePing.Net.Storage;
using Xunit;

namespace SmokePing.Net.Tests;

public sealed class ServiceRegistrationTests
{
    /// <summary>Registration: shared services really are shared</summary>
    [Fact]
    public void Registration_Shared_Services_Really_Are_Shared()
    {
            using var directory = new TempDirectory();
            using var provider = BuildProvider(directory);

            // The polling loop writes to these and the web API reads from them. A
            // second instance would leave the API reporting an empty history, which
            // is exactly what AddHttpClient<T> quietly caused before.
            Verify.True(
                ReferenceEquals(provider.GetRequiredService<AlertNotifier>(), provider.GetRequiredService<AlertNotifier>()),
                "the alert notifier is a singleton");
            Verify.True(
                ReferenceEquals(provider.GetRequiredService<MeasurementStore>(), provider.GetRequiredService<MeasurementStore>()),
                "the measurement store is a singleton");
            Verify.True(
                ReferenceEquals(provider.GetRequiredService<AlertEngine>(), provider.GetRequiredService<AlertEngine>()),
                "the alert engine is a singleton");
            Verify.True(
                ReferenceEquals(provider.GetRequiredService<ProbeRegistry>(), provider.GetRequiredService<ProbeRegistry>()),
                "the probe registry is a singleton");
    }

    /// <summary>Registration: the whole host starts, in both storage formats</summary>
    [Fact]
    public void Registration_The_Whole_Host_Starts()
    {
            // Resolving services one by one does not prove the host can be built:
            // a hosted service with a dependency the container cannot supply fails
            // only when everything is wired together.
            foreach (var format in new[] { "spd", "rrd" })
            {
                using var directory = new TempDirectory();
                using var provider = BuildProvider(directory, format);

                var hosted = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ToList();

                Verify.Equal(2, hosted.Count, $"the measuring services are registered ({format})");
                Verify.True(
                    hosted.Any(h => h is PollingService),
                    $"the polling service is one of them ({format})");
                Verify.True(
                    hosted.Any(h => h is TraceRecorder),
                    $"and the trace recorder is the other ({format})");
                Verify.Equal(
                    format,
                    provider.GetRequiredService<MeasurementStore>().Format,
                    "and the store is the configured format");
            }
    }

    /// <summary>Registration: every configured probe resolves</summary>
    [Fact]
    public void Registration_Every_Configured_Probe_Resolves()
    {
            using var directory = new TempDirectory();
            using var provider = BuildProvider(directory);
            var registry = provider.GetRequiredService<ProbeRegistry>();

            foreach (var name in new[] { "icmp", "tcp", "dns", "http" })
            {
                Verify.Equal(name, registry.Get(name).Name, $"{name} is registered");
            }
    }

    /// <summary>AlertNotifier: notifications are kept for the web interface</summary>
    [Fact]
    public async Task AlertNotifier_Notifications_Are_Kept_For_The_Web_Interface()
    {
            using var factory = new SimpleHttpClientFactory();
            var notifier = new AlertNotifier(new NullLogger<AlertNotifier>(), factory);

            var rule = new AlertRuleConfig { Name = "down", Type = "loss", Pattern = "==100%" };
await             notifier.NotifyAsync(Event("raised"), rule, CancellationToken.None);
await             notifier.NotifyAsync(Event("cleared"), rule, CancellationToken.None);

            var recent = notifier.Recent;
            Verify.Equal(2, recent.Count, "both notifications were kept");
            Verify.Equal("cleared", recent[0].State, "the newest notification comes first");
    }

    /// <summary>AlertNotifier: history is capped</summary>
    [Fact]
    public async Task AlertNotifier_History_Is_Capped()
    {
            using var factory = new SimpleHttpClientFactory();
            var notifier = new AlertNotifier(new NullLogger<AlertNotifier>(), factory);
            var rule = new AlertRuleConfig { Name = "down", Type = "loss", Pattern = "==100%" };

            for (var i = 0; i < AlertNotifier.RecentCapacity + 20; i++)
            {
await                 notifier.NotifyAsync(Event("raised"), rule, CancellationToken.None);
            }

            Verify.Equal(AlertNotifier.RecentCapacity, notifier.Recent.Count, "the buffer does not grow without bound");
    }


    

    private static ServiceProvider BuildProvider(TempDirectory directory, string format = "spd")
    {
        var config = ConfigLoader.Build(
            new SmokePingConfig
            {
                General = { DataDirectory = directory.Path },
                Database = { Format = format },
                Targets = [new TargetNode { Id = "host", Host = "192.0.2.1" }],
            },
            Path.Combine(directory.Path, "smokeping.json"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSmokePingServices(config);
        return services.BuildServiceProvider();
    }

    private static AlertEvent Event(string state) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch,
        AlertName = "down",
        TargetId = "host",
        TargetTitle = "host",
        Host = "192.0.2.1",
        State = state,
        Comment = "test",
        Pattern = "==100%",
        LossHistory = ["100%"],
        RttHistory = ["U"],
    };

    /// <summary>A logger that drops everything, so tests stay quiet.</summary>
    private sealed class NullLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
