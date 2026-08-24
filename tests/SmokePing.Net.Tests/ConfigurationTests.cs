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

        runner.Add("ConfigLoader: a round that cannot fit inside its step is rejected", () =>
        {
            var config = Minimal();
            config.Defaults.Step = 60;
            config.Defaults.Pings = 20;
            config.Defaults.PingIntervalMs = 5000;

            var error = Assert.Throws<ConfigurationException>(
                () => ConfigLoader.Build(config, "/etc/smokeping.json"),
                "20 pings 5 seconds apart cannot fit in a 60 second step");

            Assert.Contains(error.Message, "does not fit", "the message explains the arithmetic");
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
