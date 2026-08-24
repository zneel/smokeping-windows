using SmokePing.Net.Configuration;

namespace SmokePing.Net.Tests;

public static class ConfigurationTests
{
    public static void Register(TestRunner runner)
    {
        runner.Add("ConfigLoader: settings are inherited down the tree", () =>
        {
            var config = Minimal();
            config.Defaults.Step = 120;
            config.Defaults.Pings = 4;
            config.Targets[0].Pings = 6;
            config.Targets[0].Children[0].Pings = 8;

            var loaded = ConfigLoader.Build(config, "/etc/smokeping.json");
            var target = loaded.Targets.Single(t => t.Id == "group/host");

            Assert.Equal(120, target.StepSeconds, "the step comes from the defaults section");
            Assert.Equal(8, target.Pings, "the closest override wins");
        });

        runner.Add("ConfigLoader: a node without an override keeps its parent's value", () =>
        {
            var config = Minimal();
            config.Targets[0].Probe = "tcp";
            config.Targets[0].Port = 443;

            var loaded = ConfigLoader.Build(config, "/etc/smokeping.json");
            var target = loaded.Targets.Single();

            Assert.Equal("tcp", target.ProbeType, "the probe is inherited from the folder");
            Assert.Equal(443, target.Port!.Value, "so is the port");
        });

        runner.Add("ConfigLoader: built-in defaults apply when nothing is configured", () =>
        {
            var loaded = ConfigLoader.Build(Minimal(), "/etc/smokeping.json");
            var target = loaded.Targets.Single();

            Assert.Equal("icmp", target.ProbeType, "ICMP is the default probe");
            Assert.Equal(300, target.StepSeconds, "300 seconds is the default step");
            Assert.Equal(20, target.Pings, "20 pings is the default round");
            Assert.Equal(56, target.PacketSize, "56 bytes is the default payload");
        });

        runner.Add("ConfigLoader: ids build a path and folders stay out of the target list", () =>
        {
            var loaded = ConfigLoader.Build(Minimal(), "/etc/smokeping.json");

            Assert.Equal(1, loaded.Targets.Count, "only the node with a host is measured");
            Assert.Equal("group/host", loaded.Targets[0].Id, "the id is the path through the tree");
            Assert.Equal(1, loaded.Menu.Count, "the menu keeps the folder");
            Assert.False(loaded.Menu[0].IsTarget, "a folder is not a target");
        });

        runner.Add("ConfigLoader: the data directory resolves against the config file", () =>
        {
            var config = Minimal();
            config.General.DataDirectory = "var/data";

            var loaded = ConfigLoader.Build(config, Path.Combine(Path.GetTempPath(), "smokeping.json"));

            Assert.Equal(
                Path.GetFullPath(Path.Combine(Path.GetTempPath(), "var/data")),
                loaded.DataDirectory,
                "a relative data directory is relative to the configuration");
        });

        runner.Add("ConfigLoader: duplicate target ids are rejected", () =>
        {
            var config = Minimal();
            config.Targets[0].Children.Add(new TargetNode { Id = "host", Host = "192.0.2.2" });

            Assert.Throws<ConfigurationException>(
                () => ConfigLoader.Build(config, "/etc/smokeping.json"),
                "two siblings cannot share an id");
        });

        runner.Add("ConfigLoader: ids that would escape the data directory are rejected", () =>
        {
            var config = Minimal();
            config.Targets[0].Children[0].Id = "../escape";

            Assert.Throws<ConfigurationException>(
                () => ConfigLoader.Build(config, "/etc/smokeping.json"),
                "path traversal is refused at load time");
        });

        runner.Add("ConfigLoader: an empty folder is a configuration mistake", () =>
        {
            var config = Minimal();
            config.Targets.Add(new TargetNode { Id = "empty", Title = "Empty" });

            Assert.Throws<ConfigurationException>(
                () => ConfigLoader.Build(config, "/etc/smokeping.json"),
                "a node with neither host nor children measures nothing");
        });

        runner.Add("ConfigLoader: a round longer than its step is warned about, not refused", () =>
        {
            var config = Minimal();
            config.Defaults.Step = 60;
            config.Defaults.Pings = 20;
            config.Defaults.PingIntervalMs = 5000;

            // Upstream allows this - the round simply overruns and the next one starts
            // at the following boundary - so refusing to start would reject a working
            // configuration carried over from an existing installation.
            var loaded = ConfigLoader.Build(config, "/etc/smokeping.json");

            Assert.Equal(1, loaded.Warnings.Count, "the configuration loads with a warning");
            Assert.Contains(loaded.Warnings[0], "rounds will be skipped", "which says what will happen");
        });

        runner.Add("ConfigLoader: probes get upstream's own default timeouts", () =>
        {
            MeasuredTarget WithProbe(string probe, string? url = null)
            {
                var config = Minimal();
                config.Defaults.Probe = probe;
                config.Defaults.Url = url;
                config.Defaults.Query = probe == "dns" ? "example.com" : null;
                config.Defaults.Port = probe == "tcp" ? 443 : null;
                return ConfigLoader.Build(config, "/etc/smokeping.json").Targets.Single();
            }

            // A single figure across all probes is wrong in both directions: upstream
            // gives DNS five seconds and an HTTP fetch ten.
            Assert.Equal(1500, WithProbe("icmp").TimeoutMs, "ICMP");
            Assert.Equal(5000, WithProbe("dns").TimeoutMs, "DNS, as AnotherDNS defaults it");
            Assert.Equal(10000, WithProbe("http", "http://example.com/").TimeoutMs, "HTTP, as Curl defaults it");
            Assert.Equal(5000, WithProbe("tcp").TimeoutMs, "TCP");
        });

        runner.Add("ConfigLoader: an explicit timeout still wins", () =>
        {
            var config = Minimal();
            config.Defaults.Probe = "http";
            config.Defaults.Url = "http://example.com/";
            config.Defaults.TimeoutMs = 2500;

            Assert.Equal(
                2500,
                ConfigLoader.Build(config, "/etc/smokeping.json").Targets.Single().TimeoutMs,
                "the configured value is not overridden by the probe default");
        });

        runner.Add("ConfigLoader: a target cannot reference an undefined alert", () =>
        {
            var config = Minimal();
            config.Targets[0].Children[0].AlertRules = ["nosuchrule"];

            Assert.Throws<ConfigurationException>(
                () => ConfigLoader.Build(config, "/etc/smokeping.json"),
                "a typo in an alert name is caught at start-up");
        });

        runner.Add("ConfigLoader: an invalid alert pattern is reported against its alert", () =>
        {
            var config = Minimal();
            config.Alerts.Add(new AlertRuleConfig { Name = "broken", Type = "loss", Pattern = ">>>" });

            var error = Assert.Throws<ConfigurationException>(
                () => ConfigLoader.Build(config, "/etc/smokeping.json"),
                "a malformed pattern stops start-up");

            Assert.Contains(error.Message, "broken", "the message names the offending alert");
        });

        runner.Add("ConfigLoader: alerts are usable once defined", () =>
        {
            var config = Minimal();
            config.Alerts.Add(new AlertRuleConfig
            {
                Name = "someloss",
                Type = "loss",
                Pattern = ">0%,>0%",
                Comment = "loss twice",
            });
            config.Targets[0].AlertRules = ["someloss"];

            var loaded = ConfigLoader.Build(config, "/etc/smokeping.json");

            Assert.Equal(1, loaded.Alerts.Count, "the alert is compiled");
            Assert.Equal(1, loaded.Targets[0].AlertRules.Count, "and inherited by the target below the folder");
        });

        runner.Add("ConfigLoader: a configuration with nothing to measure is rejected", () =>
        {
            var config = new SmokePingConfig();

            Assert.Throws<ConfigurationException>(
                () => ConfigLoader.Build(config, "/etc/smokeping.json"),
                "an empty target list is pointless");
        });

        runner.Add("ConfigLoader: a missing file is reported clearly", () =>
        {
            var error = Assert.Throws<ConfigurationException>(
                () => ConfigLoader.Load(Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid())),
                "a missing configuration file is a configuration error");

            Assert.Contains(error.Message, "does not exist", "the message says what is wrong");
        });

        runner.Add("CommandLineOptions: a relative config is found from a subdirectory", () =>
        {
            // "dotnet run" starts the process in the project directory, and a service
            // starts in the system directory; neither resolves a repository-relative
            // path on its own, so the search climbs towards the root.
            using var directory = new TempDirectory();
            var configDirectory = Path.Combine(directory.Path, "config");
            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(Path.Combine(configDirectory, "smokeping.json"), "{}");

            var deep = Path.Combine(directory.Path, "src", "app", "bin");
            Directory.CreateDirectory(deep);

            var previous = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(deep);
                var options = CommandLineOptions.Parse(["--config", Path.Combine("config", "smokeping.json")]);

                Assert.Equal(
                    Path.Combine(configDirectory, "smokeping.json"),
                    options.ConfigPath,
                    "the configuration is found further up the tree");
            }
            finally
            {
                Directory.SetCurrentDirectory(previous);
            }
        });

        runner.Add("CommandLineOptions: an absolute config path is taken as given", () =>
        {
            var absolute = Path.Combine(Path.GetTempPath(), "smokeping-absolute.json");
            var options = CommandLineOptions.Parse(["--config", absolute]);

            Assert.Equal(absolute, options.ConfigPath, "an absolute path is never searched for");
        });

        runner.Add("ConfigLoader: the shipped sample configuration is valid", () =>
        {
            var path = FindSampleConfig();
            if (path is null)
            {
                // The sample lives in the repository, not next to the test binary.
                return;
            }

            var loaded = ConfigLoader.Load(path);
            Assert.True(loaded.Targets.Count > 0, "the sample defines targets");
            Assert.True(loaded.Alerts.Count > 0, "the sample defines alerts");
        });
    }

    /// <summary>Walks up from the test binary looking for the repository's sample config.</summary>
    private static string? FindSampleConfig()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "config", "smokeping.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>A tree with one folder and one measured host below it.</summary>
    private static SmokePingConfig Minimal() => new()
    {
        Targets =
        [
            new TargetNode
            {
                Id = "group",
                Title = "Group",
                Children =
                [
                    new TargetNode { Id = "host", Title = "Host", Host = "192.0.2.1" },
                ],
            },
        ],
    };
}
