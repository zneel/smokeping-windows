using SmokePing.Net.Configuration;

using Xunit;

namespace SmokePing.Net.Tests;

public sealed class ConfigurationTests
{
    /// <summary>ConfigLoader: settings are inherited down the tree</summary>
    [Fact]
    public void ConfigLoader_Settings_Are_Inherited_Down_The_Tree()
    {
            var config = Minimal();
            config.Defaults.Step = 120;
            config.Defaults.Pings = 4;
            config.Targets[0].Pings = 6;
            config.Targets[0].Children[0].Pings = 8;

            var loaded = ConfigLoader.Build(config, "/etc/smokeping.json");
            var target = loaded.Targets.Single(t => t.Id == "group/host");

            Verify.Equal(120, target.StepSeconds, "the step comes from the defaults section");
            Verify.Equal(8, target.Pings, "the closest override wins");
    }

    /// <summary>ConfigLoader: a node without an override keeps its parent's value</summary>
    [Fact]
    public void ConfigLoader_A_Node_Without_An_Override_Keeps_Its_Parent_S_Value()
    {
            var config = Minimal();
            config.Targets[0].Probe = "tcp";
            config.Targets[0].Port = 443;

            var loaded = ConfigLoader.Build(config, "/etc/smokeping.json");
            var target = loaded.Targets.Single();

            Verify.Equal("tcp", target.ProbeType, "the probe is inherited from the folder");
            Verify.Equal(443, target.Port!.Value, "so is the port");
    }

    /// <summary>ConfigLoader: built-in defaults apply when nothing is configured</summary>
    [Fact]
    public void ConfigLoader_Built_In_Defaults_Apply_When_Nothing_Is_Configured()
    {
            var loaded = ConfigLoader.Build(Minimal(), "/etc/smokeping.json");
            var target = loaded.Targets.Single();

            Verify.Equal("icmp", target.ProbeType, "ICMP is the default probe");
            Verify.Equal(300, target.StepSeconds, "300 seconds is the default step");
            Verify.Equal(20, target.Pings, "20 pings is the default round");
            Verify.Equal(56, target.PacketSize, "56 bytes is the default payload");
    }

    /// <summary>ConfigLoader: ids build a path and folders stay out of the target list</summary>
    [Fact]
    public void ConfigLoader_Ids_Build_A_Path_And_Folders_Stay_Out_Of_The_Target_List()
    {
            var loaded = ConfigLoader.Build(Minimal(), "/etc/smokeping.json");

            Verify.Equal(1, loaded.Targets.Count, "only the node with a host is measured");
            Verify.Equal("group/host", loaded.Targets[0].Id, "the id is the path through the tree");
            Verify.Equal(1, loaded.Menu.Count, "the menu keeps the folder");
            Verify.False(loaded.Menu[0].IsTarget, "a folder is not a target");
    }

    /// <summary>ConfigLoader: the data directory resolves against the config file</summary>
    [Fact]
    public void ConfigLoader_The_Data_Directory_Resolves_Against_The_Config_File()
    {
            var config = Minimal();
            config.General.DataDirectory = "var/data";

            var loaded = ConfigLoader.Build(config, Path.Combine(Path.GetTempPath(), "smokeping.json"));

            Verify.Equal(
                Path.GetFullPath(Path.Combine(Path.GetTempPath(), "var/data")),
                loaded.DataDirectory,
                "a relative data directory is relative to the configuration");
    }

    /// <summary>ConfigLoader: duplicate target ids are rejected</summary>
    [Fact]
    public void ConfigLoader_Duplicate_Target_Ids_Are_Rejected()
    {
            var config = Minimal();
            config.Targets[0].Children.Add(new TargetNode { Id = "host", Host = "192.0.2.2" });

            Verify.Throws<ConfigurationException>(
                () => ConfigLoader.Build(config, "/etc/smokeping.json"),
                "two siblings cannot share an id");
    }

    /// <summary>ConfigLoader: ids that would escape the data directory are rejected</summary>
    [Fact]
    public void ConfigLoader_Ids_That_Would_Escape_The_Data_Directory_Are_Rejected()
    {
            var config = Minimal();
            config.Targets[0].Children[0].Id = "../escape";

            Verify.Throws<ConfigurationException>(
                () => ConfigLoader.Build(config, "/etc/smokeping.json"),
                "path traversal is refused at load time");
    }

    /// <summary>ConfigLoader: an empty folder is a configuration mistake</summary>
    [Fact]
    public void ConfigLoader_An_Empty_Folder_Is_A_Configuration_Mistake()
    {
            var config = Minimal();
            config.Targets.Add(new TargetNode { Id = "empty", Title = "Empty" });

            Verify.Throws<ConfigurationException>(
                () => ConfigLoader.Build(config, "/etc/smokeping.json"),
                "a node with neither host nor children measures nothing");
    }

    /// <summary>ConfigLoader: a round longer than its step is warned about, not refused</summary>
    [Fact]
    public void ConfigLoader_A_Round_Longer_Than_Its_Step_Is_Warned_About_Not_Refused()
    {
            var config = Minimal();
            config.Defaults.Step = 60;
            config.Defaults.Pings = 20;
            config.Defaults.PingIntervalMs = 5000;

            // Upstream allows this - the round simply overruns and the next one starts
            // at the following boundary - so refusing to start would reject a working
            // configuration carried over from an existing installation.
            var loaded = ConfigLoader.Build(config, "/etc/smokeping.json");

            Verify.Equal(1, loaded.Warnings.Count, "the configuration loads with a warning");
            Verify.Contains(loaded.Warnings[0], "rounds will be skipped", "which says what will happen");
    }

    /// <summary>ConfigLoader: probes get upstream's own default timeouts</summary>
    [Fact]
    public void ConfigLoader_Probes_Get_Upstream_S_Own_Default_Timeouts()
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
            Verify.Equal(1500, WithProbe("icmp").TimeoutMs, "ICMP");
            Verify.Equal(5000, WithProbe("dns").TimeoutMs, "DNS, as AnotherDNS defaults it");
            Verify.Equal(10000, WithProbe("http", "http://example.com/").TimeoutMs, "HTTP, as Curl defaults it");
            Verify.Equal(5000, WithProbe("tcp").TimeoutMs, "TCP");
    }

    /// <summary>ConfigLoader: an explicit timeout still wins</summary>
    [Fact]
    public void ConfigLoader_An_Explicit_Timeout_Still_Wins()
    {
            var config = Minimal();
            config.Defaults.Probe = "http";
            config.Defaults.Url = "http://example.com/";
            config.Defaults.TimeoutMs = 2500;

            Verify.Equal(
                2500,
                ConfigLoader.Build(config, "/etc/smokeping.json").Targets.Single().TimeoutMs,
                "the configured value is not overridden by the probe default");
    }

    /// <summary>ConfigLoader: a target cannot reference an undefined alert</summary>
    [Fact]
    public void ConfigLoader_A_Target_Cannot_Reference_An_Undefined_Alert()
    {
            var config = Minimal();
            config.Targets[0].Children[0].AlertRules = ["nosuchrule"];

            Verify.Throws<ConfigurationException>(
                () => ConfigLoader.Build(config, "/etc/smokeping.json"),
                "a typo in an alert name is caught at start-up");
    }

    /// <summary>ConfigLoader: an invalid alert pattern is reported against its alert</summary>
    [Fact]
    public void ConfigLoader_An_Invalid_Alert_Pattern_Is_Reported_Against_Its_Alert()
    {
            var config = Minimal();
            config.Alerts.Add(new AlertRuleConfig { Name = "broken", Type = "loss", Pattern = ">>>" });

            var error = Verify.Throws<ConfigurationException>(
                () => ConfigLoader.Build(config, "/etc/smokeping.json"),
                "a malformed pattern stops start-up");

            Verify.Contains(error.Message, "broken", "the message names the offending alert");
    }

    /// <summary>ConfigLoader: alerts are usable once defined</summary>
    [Fact]
    public void ConfigLoader_Alerts_Are_Usable_Once_Defined()
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

            Verify.Equal(1, loaded.Alerts.Count, "the alert is compiled");
            Verify.Equal(1, loaded.Targets[0].AlertRules.Count, "and inherited by the target below the folder");
    }

    /// <summary>ConfigLoader: a configuration with nothing to measure is rejected</summary>
    [Fact]
    public void ConfigLoader_A_Configuration_With_Nothing_To_Measure_Is_Rejected()
    {
            var config = new SmokePingConfig();

            Verify.Throws<ConfigurationException>(
                () => ConfigLoader.Build(config, "/etc/smokeping.json"),
                "an empty target list is pointless");
    }

    /// <summary>ConfigLoader: a missing file is reported clearly</summary>
    [Fact]
    public void ConfigLoader_A_Missing_File_Is_Reported_Clearly()
    {
            var error = Verify.Throws<ConfigurationException>(
                () => ConfigLoader.Load(Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid())),
                "a missing configuration file is a configuration error");

            Verify.Contains(error.Message, "does not exist", "the message says what is wrong");
    }

    /// <summary>CommandLineOptions: a relative config is found from a subdirectory</summary>
    [Fact]
    public void CommandLineOptions_A_Relative_Config_Is_Found_From_A_Subdirectory()
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

                Verify.Equal(
                    Path.Combine(configDirectory, "smokeping.json"),
                    options.ConfigPath,
                    "the configuration is found further up the tree");
            }
            finally
            {
                Directory.SetCurrentDirectory(previous);
            }
    }

    /// <summary>CommandLineOptions: an absolute config path is taken as given</summary>
    [Fact]
    public void CommandLineOptions_An_Absolute_Config_Path_Is_Taken_As_Given()
    {
            var absolute = Path.Combine(Path.GetTempPath(), "smokeping-absolute.json");
            var options = CommandLineOptions.Parse(["--config", absolute]);

            Verify.Equal(absolute, options.ConfigPath, "an absolute path is never searched for");
    }

    /// <summary>ConfigLoader: the shipped sample configuration is valid</summary>
    [Fact]
    public void ConfigLoader_The_Shipped_Sample_Configuration_Is_Valid()
    {
            var path = FindSampleConfig();
            if (path is null)
            {
                // The sample lives in the repository, not next to the test binary.
                return;
            }

            var loaded = ConfigLoader.Load(path);
            Verify.True(loaded.Targets.Count > 0, "the sample defines targets");
            Verify.True(loaded.Alerts.Count > 0, "the sample defines alerts");
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
