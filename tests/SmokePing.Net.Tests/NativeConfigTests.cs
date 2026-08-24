using SmokePing.Net.Configuration;

namespace SmokePing.Net.Tests;

/// <summary>
/// Tests for reading SmokePing's own configuration format, which is what makes
/// adopting an existing installation possible without rewriting its config by hand.
/// </summary>
public static class NativeConfigTests
{
    private const string Sample = """
        *** General ***
        owner    = Peter Random
        contact  = some@address.nowhere
        datadir  = /var/smokeping/data

        *** Alerts ***
        to = alertee@address.somewhere

        +someloss
        type = loss
        # in percent
        pattern = >0%,*12*,>0%,*12*,>0%
        comment = loss 3 times  in a row

        *** Database ***
        step     = 300
        pings    = 20

        AVERAGE  0.5   1  28800
        AVERAGE  0.5  12   9600
            MIN  0.5  12   9600

        *** Probes ***
        + FPing
        packetsize = 64

        *** Targets ***
        probe = FPing
        menu = Top
        title = Network Latency Grapher

        + Europe
        menu = Europe

        ++ Zurich
        menu = Zurich
        title = Zurich office
        alerts = someloss
        host = zurich.example.com

        ++ Web
        menu = Web
        probe = Curl
        urlformat = https://example.com/
        timeout = 10
        host = example.com
        """;

    public static void Register(TestRunner runner)
    {
        runner.Add("NativeConfig: the format is recognised, and JSON is not mistaken for it", () =>
        {
            Assert.True(SmokePingConfigParser.LooksLikeNativeFormat(Sample), "a section header gives it away");
            Assert.True(
                SmokePingConfigParser.LooksLikeNativeFormat("# a comment\n\n*** General ***\n"),
                "a leading comment does not hide it");
            Assert.False(SmokePingConfigParser.LooksLikeNativeFormat("{\n  \"general\": {}\n}"), "JSON is JSON");
            Assert.False(SmokePingConfigParser.LooksLikeNativeFormat("// note\n{ }"), "even behind a comment");
        });

        runner.Add("NativeConfig: sections, variables and nesting are parsed", () =>
        {
            var sections = Parse(Sample);

            Assert.Equal(5, sections.Count, "five sections");
            Assert.Equal("General", sections[0].Name, "the first is General");
            Assert.Equal("Peter Random", sections[0].Get("owner")!, "its variables are read");

            var targets = sections.Single(s => s.Name == "Targets");
            Assert.Equal("FPing", targets.Get("probe")!, "the root carries settings");
            Assert.Equal(1, targets.Children.Count, "one top-level target");

            var europe = targets.Children[0];
            Assert.Equal("Europe", europe.Name, "named by its section");
            Assert.Equal(2, europe.Children.Count, "with two children below it");
            Assert.Equal("zurich.example.com", europe.Children[0].Get("host")!, "and their own settings");
        });

        runner.Add("NativeConfig: the archive table is read as rows, not assignments", () =>
        {
            var database = Parse(Sample).Single(s => s.Name == "Database");

            Assert.Equal("300", database.Get("step")!, "step is an assignment");
            Assert.Equal(3, database.Rows.Count, "three archive rows");
            Assert.Equal("AVERAGE", database.Rows[0][0], "the consolidation function");
            Assert.Equal("28800", database.Rows[0][3], "and the row count");
        });

        runner.Add("NativeConfig: continued lines are joined", () =>
        {
            var sections = Parse("""
                *** General ***
                remark = Welcome to the SmokePing website of xxx Company. \
                         Here you will learn all about our network.
                owner = someone
                """);

            var remark = sections[0].Get("remark")!;
            Assert.Contains(remark, "xxx Company.", "the first line is there");
            Assert.Contains(remark, "learn all about", "and so is the continuation");
            Assert.Equal("someone", sections[0].Get("owner")!, "the next assignment is unaffected");
        });

        runner.Add("NativeConfig: content before any section is an error", () =>
        {
            Assert.Throws<ConfigurationException>(
                () => Parse("owner = nobody\n*** General ***\n"),
                "a stray assignment has no section to belong to");
        });

        runner.Add("NativeConfig: over-deep nesting is an error", () =>
        {
            Assert.Throws<ConfigurationException>(
                () => Parse("*** Targets ***\n+ One\n+++ Three\n"),
                "a third-level section needs a second-level parent");
        });

        runner.Add("NativeConfig: translation maps probes, units and the tree", () =>
        {
            var config = SmokePingConfigTranslator.Translate(Parse(Sample), "config");

            Assert.Equal("/var/smokeping/data", config.General.DataDirectory, "datadir is carried over");
            Assert.Equal(300, config.Defaults.Step!.Value, "step comes from the Database section");
            Assert.Equal(20, config.Defaults.Pings!.Value, "and so do the pings");
            Assert.Equal("icmp", config.Defaults.Probe!, "FPing is the ICMP probe");
            Assert.Equal(64, config.Defaults.PacketSize!.Value, "the probe section's parameters apply");

            var europe = config.Targets.Single();
            Assert.Equal("Europe", europe.Id, "the section name becomes the id");

            var zurich = europe.Children[0];
            Assert.Equal("Zurich", zurich.Title!, "the menu entry becomes the title");
            Assert.Equal("zurich.example.com", zurich.Host!, "the host is carried over");
            Assert.Equal(1, zurich.AlertRules!.Count, "and its alert");

            var web = europe.Children[1];
            Assert.Equal("http", web.Probe!, "Curl is the HTTP probe");
            Assert.Equal("https://example.com/", web.Url!, "urlformat becomes the url");
            Assert.Equal(10000, web.TimeoutMs!.Value, "a timeout of 10 seconds is 10000 milliseconds");
        });

        runner.Add("NativeConfig: the archive table drives retention", () =>
        {
            var config = SmokePingConfigTranslator.Translate(Parse(Sample), "config");

            // The MIN archive is not reproduced; upstream never reads its own.
            Assert.Equal(2, config.Database.Archives.Count, "only the AVERAGE rows are kept");
            Assert.Equal(1, config.Database.Archives[0].Steps, "the base archive runs at the step");
            Assert.Equal(28800, config.Database.Archives[0].Rows, "for 28800 rows");
            Assert.Equal(12, config.Database.Archives[1].Steps, "the second consolidates twelve rounds");
        });

        runner.Add("NativeConfig: alerts are translated with upstream's edgetrigger default", () =>
        {
            var config = SmokePingConfigTranslator.Translate(Parse(Sample), "config");
            var alert = config.Alerts.Single();

            Assert.Equal("someloss", alert.Name, "named by its section");
            Assert.Equal("loss", alert.Type, "of the right type");
            Assert.Equal(">0%,*12*,>0%,*12*,>0%", alert.Pattern, "with its pattern intact");
            Assert.False(alert.EdgeTrigger, "and edgetrigger off, as upstream defaults it");
        });

        runner.Add("NativeConfig: an unimplemented probe is named, not silently ignored", () =>
        {
            var error = Assert.Throws<ConfigurationException>(
                () => SmokePingConfigTranslator.Translate(
                    Parse("*** Targets ***\nprobe = SSH\n+ One\nhost = a.example.com\n"),
                    "config"),
                "SSH is not one of the four probes implemented");

            Assert.Contains(error.Message, "SSH", "the message names the probe");
        });

        runner.Add("NativeConfig: a matcher alert is refused rather than approximated", () =>
        {
            Assert.Throws<ConfigurationException>(
                () => SmokePingConfigTranslator.Translate(
                    Parse("*** Alerts ***\n+bad\ntype = matcher\npattern = CheckLoss(l=>5,x=>3)\n"),
                    "config"),
                "matchers branch on prior state, which a pattern cannot express");
        });

        runner.Add("NativeConfig: a full native configuration loads end to end", () =>
        {
            using var directory = new TempDirectory();
            var path = directory.File("config");
            File.WriteAllText(path, Sample);

            var loaded = ConfigLoader.Load(path);

            Assert.Equal(2, loaded.Targets.Count, "both measured targets are there");
            Assert.Equal("Europe/Zurich", loaded.Targets[0].Id, "ids follow the section hierarchy");
            Assert.Equal("icmp", loaded.Targets[0].ProbeType, "with the mapped probe");
            Assert.Equal("http", loaded.Targets[1].ProbeType, "and the other one too");
            Assert.Equal(1, loaded.Alerts.Count, "the alert compiled");
        });

        runner.Add("NativeConfig: unsupported targets can be skipped instead of refused", () =>
        {
            var text = Sample + "\n++ Multi\nmenu = Multi\nhost = /Europe/Zurich /Europe/Web\n";

            using var directory = new TempDirectory();
            var path = directory.File("config");
            File.WriteAllText(path, text);

            Assert.Throws<ConfigurationException>(
                () => ConfigLoader.Load(path),
                "by default a target that cannot be measured stops the load");

            var loaded = ConfigLoader.Load(path, skipUnsupported: true);
            Assert.Equal(2, loaded.Targets.Count, "the measurable targets still load");
            Assert.Equal(1, loaded.SkippedTargets.Count, "and the skipped one is reported");
            Assert.Contains(loaded.SkippedTargets[0], "multi-host", "with the reason");
        });

        runner.Add("NativeConfig: an included file is read", () =>
        {
            using var directory = new TempDirectory();
            File.WriteAllText(directory.File("targets.conf"), "*** Targets ***\nprobe = FPing\n+ One\nhost = a.example.com\n");

            var path = directory.File("config");
            File.WriteAllText(path, "*** General ***\nowner = someone\n@include targets.conf\n");

            var loaded = ConfigLoader.Load(path);
            Assert.Equal(1, loaded.Targets.Count, "the included targets are measured");
        });

        runner.Add("NativeConfig: a missing include is reported with its path", () =>
        {
            using var directory = new TempDirectory();
            var path = directory.File("config");
            File.WriteAllText(path, "*** General ***\n@include nope.conf\n");

            var error = Assert.Throws<ConfigurationException>(
                () => ConfigLoader.Load(path),
                "an include that is not there cannot be skipped over");

            Assert.Contains(error.Message, "nope.conf", "the message names the file");
        });
    }

    private static IReadOnlyList<ConfigSection> Parse(string text) =>
        SmokePingConfigParser.Parse(text.Split('\n'), "config");
}
