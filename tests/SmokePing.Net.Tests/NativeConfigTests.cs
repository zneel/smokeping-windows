using SmokePing.Net.Configuration;

using Xunit;

namespace SmokePing.Net.Tests;

/// <summary>
/// Tests for reading SmokePing's own configuration format, which is what makes
/// adopting an existing installation possible without rewriting its config by hand.
/// </summary>
public sealed class NativeConfigTests
{
    /// <summary>NativeConfig: the format is recognised, and JSON is not mistaken for it</summary>
    [Fact]
    public void NativeConfig_The_Format_Is_Recognised_And_JSON_Is_Not_Mistaken_For_It()
    {
            Verify.True(SmokePingConfigParser.LooksLikeNativeFormat(Sample), "a section header gives it away");
            Verify.True(
                SmokePingConfigParser.LooksLikeNativeFormat("# a comment\n\n*** General ***\n"),
                "a leading comment does not hide it");
            Verify.False(SmokePingConfigParser.LooksLikeNativeFormat("{\n  \"general\": {}\n}"), "JSON is JSON");
            Verify.False(SmokePingConfigParser.LooksLikeNativeFormat("// note\n{ }"), "even behind a comment");
    }

    /// <summary>NativeConfig: sections, variables and nesting are parsed</summary>
    [Fact]
    public void NativeConfig_Sections_Variables_And_Nesting_Are_Parsed()
    {
            var sections = Parse(Sample);

            Verify.Equal(5, sections.Count, "five sections");
            Verify.Equal("General", sections[0].Name, "the first is General");
            Verify.Equal("Peter Random", sections[0].Get("owner")!, "its variables are read");

            var targets = sections.Single(s => s.Name == "Targets");
            Verify.Equal("FPing", targets.Get("probe")!, "the root carries settings");
            Verify.Equal(1, targets.Children.Count, "one top-level target");

            var europe = targets.Children[0];
            Verify.Equal("Europe", europe.Name, "named by its section");
            Verify.Equal(2, europe.Children.Count, "with two children below it");
            Verify.Equal("zurich.example.com", europe.Children[0].Get("host")!, "and their own settings");
    }

    /// <summary>NativeConfig: the archive table is read as rows, not assignments</summary>
    [Fact]
    public void NativeConfig_The_Archive_Table_Is_Read_As_Rows_Not_Assignments()
    {
            var database = Parse(Sample).Single(s => s.Name == "Database");

            Verify.Equal("300", database.Get("step")!, "step is an assignment");
            Verify.Equal(3, database.Rows.Count, "three archive rows");
            Verify.Equal("AVERAGE", database.Rows[0][0], "the consolidation function");
            Verify.Equal("28800", database.Rows[0][3], "and the row count");
    }

    /// <summary>NativeConfig: continued lines are joined</summary>
    [Fact]
    public void NativeConfig_Continued_Lines_Are_Joined()
    {
            var sections = Parse("""
                *** General ***
                remark = Welcome to the SmokePing website of xxx Company. \
                         Here you will learn all about our network.
                owner = someone
                """);

            var remark = sections[0].Get("remark")!;
            Verify.Contains(remark, "xxx Company.", "the first line is there");
            Verify.Contains(remark, "learn all about", "and so is the continuation");
            Verify.Equal("someone", sections[0].Get("owner")!, "the next assignment is unaffected");
    }

    /// <summary>NativeConfig: content before any section is an error</summary>
    [Fact]
    public void NativeConfig_Content_Before_Any_Section_Is_An_Error()
    {
            Verify.Throws<ConfigurationException>(
                () => Parse("owner = nobody\n*** General ***\n"),
                "a stray assignment has no section to belong to");
    }

    /// <summary>NativeConfig: over-deep nesting is an error</summary>
    [Fact]
    public void NativeConfig_Over_Deep_Nesting_Is_An_Error()
    {
            Verify.Throws<ConfigurationException>(
                () => Parse("*** Targets ***\n+ One\n+++ Three\n"),
                "a third-level section needs a second-level parent");
    }

    /// <summary>NativeConfig: translation maps probes, units and the tree</summary>
    [Fact]
    public void NativeConfig_Translation_Maps_Probes_Units_And_The_Tree()
    {
            var config = SmokePingConfigTranslator.Translate(Parse(Sample), "config");

            Verify.Equal("/var/smokeping/data", config.General.DataDirectory, "datadir is carried over");
            Verify.Equal(300, config.Defaults.Step!.Value, "step comes from the Database section");
            Verify.Equal(20, config.Defaults.Pings!.Value, "and so do the pings");
            Verify.Equal("icmp", config.Defaults.Probe!, "FPing is the ICMP probe");
            Verify.Equal(64, config.Defaults.PacketSize!.Value, "the probe section's parameters apply");

            var europe = config.Targets.Single();
            Verify.Equal("Europe", europe.Id, "the section name becomes the id");

            var zurich = europe.Children[0];
            Verify.Equal("Zurich", zurich.Title!, "the menu entry becomes the title");
            Verify.Equal("zurich.example.com", zurich.Host!, "the host is carried over");
            Verify.Equal(1, zurich.AlertRules!.Count, "and its alert");

            var web = europe.Children[1];
            Verify.Equal("http", web.Probe!, "Curl is the HTTP probe");
            Verify.Equal("https://example.com/", web.Url!, "urlformat becomes the url");
            Verify.Equal(10000, web.TimeoutMs!.Value, "a timeout of 10 seconds is 10000 milliseconds");
    }

    /// <summary>NativeConfig: the archive table drives retention</summary>
    [Fact]
    public void NativeConfig_The_Archive_Table_Drives_Retention()
    {
            var config = SmokePingConfigTranslator.Translate(Parse(Sample), "config");

            // The MIN archive is not reproduced; upstream never reads its own.
            Verify.Equal(2, config.Database.Archives.Count, "only the AVERAGE rows are kept");
            Verify.Equal(1, config.Database.Archives[0].Steps, "the base archive runs at the step");
            Verify.Equal(28800, config.Database.Archives[0].Rows, "for 28800 rows");
            Verify.Equal(12, config.Database.Archives[1].Steps, "the second consolidates twelve rounds");
    }

    /// <summary>NativeConfig: alerts are translated with upstream's edgetrigger default</summary>
    [Fact]
    public void NativeConfig_Alerts_Are_Translated_With_Upstream_S_Edgetrigger_Default()
    {
            var config = SmokePingConfigTranslator.Translate(Parse(Sample), "config");
            var alert = config.Alerts.Single();

            Verify.Equal("someloss", alert.Name, "named by its section");
            Verify.Equal("loss", alert.Type, "of the right type");
            Verify.Equal(">0%,*12*,>0%,*12*,>0%", alert.Pattern, "with its pattern intact");
            Verify.False(alert.EdgeTrigger, "and edgetrigger off, as upstream defaults it");
    }

    /// <summary>NativeConfig: an unimplemented probe is named, not silently ignored</summary>
    [Fact]
    public void NativeConfig_An_Unimplemented_Probe_Is_Named_Not_Silently_Ignored()
    {
            var error = Verify.Throws<ConfigurationException>(
                () => SmokePingConfigTranslator.Translate(
                    Parse("*** Targets ***\nprobe = SSH\n+ One\nhost = a.example.com\n"),
                    "config"),
                "SSH is not one of the four probes implemented");

            Verify.Contains(error.Message, "SSH", "the message names the probe");
    }

    /// <summary>NativeConfig: a matcher alert is refused rather than approximated</summary>
    [Fact]
    public void NativeConfig_A_Matcher_Alert_Is_Refused_Rather_Than_Approximated()
    {
            Verify.Throws<ConfigurationException>(
                () => SmokePingConfigTranslator.Translate(
                    Parse("*** Alerts ***\n+bad\ntype = matcher\npattern = CheckLoss(l=>5,x=>3)\n"),
                    "config"),
                "matchers branch on prior state, which a pattern cannot express");
    }

    /// <summary>NativeConfig: a full native configuration loads end to end</summary>
    [Fact]
    public void NativeConfig_A_Full_Native_Configuration_Loads_End_To_End()
    {
            using var directory = new TempDirectory();
            var path = directory.File("config");
            File.WriteAllText(path, Sample);

            var loaded = ConfigLoader.Load(path);

            Verify.Equal(2, loaded.Targets.Count, "both measured targets are there");
            Verify.Equal("Europe/Zurich", loaded.Targets[0].Id, "ids follow the section hierarchy");
            Verify.Equal("icmp", loaded.Targets[0].ProbeType, "with the mapped probe");
            Verify.Equal("http", loaded.Targets[1].ProbeType, "and the other one too");
            Verify.Equal(1, loaded.Alerts.Count, "the alert compiled");
    }

    /// <summary>NativeConfig: unsupported targets can be skipped instead of refused</summary>
    [Fact]
    public void NativeConfig_Unsupported_Targets_Can_Be_Skipped_Instead_Of_Refused()
    {
            var text = Sample + "\n++ Multi\nmenu = Multi\nhost = /Europe/Zurich /Europe/Web\n";

            using var directory = new TempDirectory();
            var path = directory.File("config");
            File.WriteAllText(path, text);

            Verify.Throws<ConfigurationException>(
                () => ConfigLoader.Load(path),
                "by default a target that cannot be measured stops the load");

            var loaded = ConfigLoader.Load(path, skipUnsupported: true);
            Verify.Equal(2, loaded.Targets.Count, "the measurable targets still load");
            Verify.Equal(1, loaded.SkippedTargets.Count, "and the skipped one is reported");
            Verify.Contains(loaded.SkippedTargets[0], "multi-host", "with the reason");
    }

    /// <summary>NativeConfig: an included file is read</summary>
    [Fact]
    public void NativeConfig_An_Included_File_Is_Read()
    {
            using var directory = new TempDirectory();
            File.WriteAllText(directory.File("targets.conf"), "*** Targets ***\nprobe = FPing\n+ One\nhost = a.example.com\n");

            var path = directory.File("config");
            File.WriteAllText(path, "*** General ***\nowner = someone\n@include targets.conf\n");

            var loaded = ConfigLoader.Load(path);
            Verify.Equal(1, loaded.Targets.Count, "the included targets are measured");
    }

    /// <summary>NativeConfig: a missing include is reported with its path</summary>
    [Fact]
    public void NativeConfig_A_Missing_Include_Is_Reported_With_Its_Path()
    {
            using var directory = new TempDirectory();
            var path = directory.File("config");
            File.WriteAllText(path, "*** General ***\n@include nope.conf\n");

            var error = Verify.Throws<ConfigurationException>(
                () => ConfigLoader.Load(path),
                "an include that is not there cannot be skipped over");

            Verify.Contains(error.Message, "nope.conf", "the message names the file");
    }


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

    

    private static IReadOnlyList<ConfigSection> Parse(string text) =>
        SmokePingConfigParser.Parse(text.Split('\n'), "config");
}
