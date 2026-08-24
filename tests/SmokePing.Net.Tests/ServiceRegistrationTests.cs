using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmokePing.Net.Alerting;
using SmokePing.Net.Configuration;
using SmokePing.Net.Probes;
using SmokePing.Net.Services;
using SmokePing.Net.Storage;

namespace SmokePing.Net.Tests;

public static class ServiceRegistrationTests
{
    public static void Register(TestRunner runner)
    {
        runner.Add("Registration: shared services really are shared", () =>
        {
            using var directory = new TempDirectory();
            using var provider = BuildProvider(directory);

            // The polling loop writes to these and the web API reads from them. A
            // second instance would leave the API reporting an empty history, which
            // is exactly what AddHttpClient<T> quietly caused before.
            Assert.True(
                ReferenceEquals(provider.GetRequiredService<AlertNotifier>(), provider.GetRequiredService<AlertNotifier>()),
                "the alert notifier is a singleton");
            Assert.True(
                ReferenceEquals(provider.GetRequiredService<DataStore>(), provider.GetRequiredService<DataStore>()),
                "the data store is a singleton");
            Assert.True(
                ReferenceEquals(provider.GetRequiredService<AlertEngine>(), provider.GetRequiredService<AlertEngine>()),
                "the alert engine is a singleton");
            Assert.True(
                ReferenceEquals(provider.GetRequiredService<ProbeRegistry>(), provider.GetRequiredService<ProbeRegistry>()),
                "the probe registry is a singleton");
        });

        runner.Add("Registration: every configured probe resolves", () =>
        {
            using var directory = new TempDirectory();
            using var provider = BuildProvider(directory);
            var registry = provider.GetRequiredService<ProbeRegistry>();

            foreach (var name in new[] { "icmp", "tcp", "dns", "http" })
            {
                Assert.Equal(name, registry.Get(name).Name, $"{name} is registered");
            }
        });

        runner.Add("AlertNotifier: notifications are kept for the web interface", () =>
        {
            using var factory = new SimpleHttpClientFactory();
            var notifier = new AlertNotifier(new NullLogger<AlertNotifier>(), factory);

            var rule = new AlertRuleConfig { Name = "down", Type = "loss", Pattern = "==100%" };
            notifier.NotifyAsync(Event("raised"), rule, CancellationToken.None).GetAwaiter().GetResult();
            notifier.NotifyAsync(Event("cleared"), rule, CancellationToken.None).GetAwaiter().GetResult();

            var recent = notifier.Recent;
            Assert.Equal(2, recent.Count, "both notifications were kept");
            Assert.Equal("cleared", recent[0].State, "the newest notification comes first");
        });

        runner.Add("AlertNotifier: history is capped", () =>
        {
            using var factory = new SimpleHttpClientFactory();
            var notifier = new AlertNotifier(new NullLogger<AlertNotifier>(), factory);
            var rule = new AlertRuleConfig { Name = "down", Type = "loss", Pattern = "==100%" };

            for (var i = 0; i < AlertNotifier.RecentCapacity + 20; i++)
            {
                notifier.NotifyAsync(Event("raised"), rule, CancellationToken.None).GetAwaiter().GetResult();
            }

            Assert.Equal(AlertNotifier.RecentCapacity, notifier.Recent.Count, "the buffer does not grow without bound");
        });
    }

    private static ServiceProvider BuildProvider(TempDirectory directory)
    {
        var config = ConfigLoader.Build(
            new SmokePingConfig
            {
                General = { DataDirectory = directory.Path },
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
