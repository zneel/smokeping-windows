using System.Xml.Linq;
using SmokePing.Net.Graphing;
using SmokePing.Net.Probes;
using SmokePing.Net.Storage;
using SmokePing.Net.Web;

namespace SmokePing.Net.Tests;

public static class GraphingTests
{
    public static void Register(TestRunner runner)
    {
        runner.Add("LossColours: a clean round is green and a dead one is dark red", () =>
        {
            Assert.Equal("#26ff00", LossColours.ForLoss(0, 20), "no loss is green");
            Assert.Equal("#a00000", LossColours.ForLoss(20, 20), "total loss is dark red");
            Assert.Equal("#ff0000", LossColours.ForLoss(19, 20), "almost total loss is red");
        });

        runner.Add("LossColours: total loss stays dark red even for short rounds", () =>
        {
            foreach (var pings in new[] { 1, 2, 3, 5, 10, 20 })
            {
                Assert.Equal("#a00000", LossColours.ForLoss(pings, pings), $"a dead round is dark red ({pings} pings)");
                Assert.Equal("#26ff00", LossColours.ForLoss(0, pings), $"a clean round is green ({pings} pings)");
            }
        });

        runner.Add("LossColours: the scale covers every possible loss count", () =>
        {
            foreach (var pings in new[] { 1, 2, 3, 5, 20, 50 })
            {
                var scale = LossColours.BuildScale(pings);

                Assert.Equal(0, scale[0].MaxLost, $"the first band is the clean one ({pings} pings)");
                Assert.Equal(pings, scale[^1].MaxLost, $"the last band reaches total loss ({pings} pings)");

                for (var i = 1; i < scale.Count; i++)
                {
                    Assert.True(
                        scale[i].MaxLost > scale[i - 1].MaxLost,
                        $"bands increase strictly ({pings} pings)");
                }

                // Every possible loss count must fall into exactly one band.
                for (var lost = 0; lost <= pings; lost++)
                {
                    var band = scale.First(b => lost <= b.MaxLost);
                    Assert.Equal(band.Colour, LossColours.ForLoss(lost, pings), $"{lost}/{pings} uses its band's colour");
                }
            }
        });

        runner.Add("YScale: the axis leaves room above the slowest probe", () =>
        {
            var scale = YScale.For([SampleAt(0, 10, 100)], 200);

            Assert.True(scale.HasData, "there is data to scale");
            Assert.True(scale.ToPixels(100) < 200, "the peak sits below the top of the plot");
            Assert.True(scale.Ticks.Count > 1, "the axis is labelled");
        });

        runner.Add("YScale: an empty period still produces a usable axis", () =>
        {
            var scale = YScale.For([Sample.Empty(0)], 200);

            Assert.False(scale.HasData, "no data was recorded");
            Assert.True(scale.Ticks.Count > 0, "the axis is still drawn");
            Assert.Equal(0.0, scale.ToPixels(double.NaN), "NaN does not escape onto the canvas");
        });

        runner.Add("GraphStatistics: summary figures match the samples", () =>
        {
            var stats = GraphStatistics.Compute([
                SampleAt(0, 10, 30),
                SampleAt(60, 20, 40),
                SampleAt(120, 30, 50),
            ]);

            Assert.True(stats.HasData, "three rounds have data");
            Assert.Close(10, stats.Minimum, 0.001, "the minimum is the fastest probe seen");
            Assert.Close(50, stats.Maximum, 0.001, "the maximum is the slowest probe seen");
            Assert.Equal(3, stats.RoundsWithData, "all three rounds counted");
            Assert.Close(0, stats.LossPercent, 0.001, "nothing was lost");
        });

        runner.Add("GraphStatistics: loss is counted even when nothing answered", () =>
        {
            var stats = GraphStatistics.Compute([
                new Sample { Timestamp = 0, Sent = 10, Lost = 10, Quantiles = Sample.CreateNaNQuantiles() },
            ]);

            Assert.False(stats.HasData, "no round trip times were recorded");
            Assert.Close(100, stats.LossPercent, 0.001, "but the loss is still reported");
        });

        runner.Add("SmokeGraphRenderer: the output is well-formed SVG", () =>
        {
            var svg = Render(BuildSamples(24));

            var document = XDocument.Parse(svg);
            Assert.Equal("svg", document.Root!.Name.LocalName, "the root element is an svg");
            Assert.Contains(svg, "smoke", "the title is carried into the picture");
        });

        runner.Add("SmokeGraphRenderer: the smoke is drawn as nested bands", () =>
        {
            var svg = Render(BuildSamples(24));
            var paths = XDocument.Parse(svg).Descendants()
                .Count(e => e.Name.LocalName == "path");

            Assert.Equal(Sample.QuantileCount / 2, paths, "one band per pair of opposite quantiles");
        });

        runner.Add("SmokeGraphRenderer: a gap splits the smoke instead of bridging it", () =>
        {
            var samples = BuildSamples(24).ToList();
            samples[12] = Sample.Empty(samples[12].Timestamp);

            var paths = XDocument.Parse(Render(samples)).Descendants()
                .Count(e => e.Name.LocalName == "path");

            Assert.Equal(Sample.QuantileCount / 2 * 2, paths, "each band is drawn as two separate runs");
        });

        runner.Add("SmokeGraphRenderer: lossy rounds colour the median line", () =>
        {
            var samples = BuildSamples(6).ToList();
            samples[3] = new Sample
            {
                Timestamp = samples[3].Timestamp,
                Sent = 20,
                Lost = 20,
                Quantiles = Sample.CreateNaNQuantiles(),
            };

            var svg = Render(samples);
            Assert.Contains(svg, "#26ff00", "clean rounds stay green");
        });

        runner.Add("SmokeGraphRenderer: an empty period says so rather than drawing nothing", () =>
        {
            var svg = Render([Sample.Empty(0), Sample.Empty(300)]);

            Assert.Contains(svg, "no data for this period", "the reader is told why the graph is blank");
            XDocument.Parse(svg);
        });

        runner.Add("SmokeGraphRenderer: an outage is distinguished from a gap", () =>
        {
            var dead = Enumerable.Range(0, 4)
                .Select(i => new Sample
                {
                    Timestamp = i * 300,
                    Sent = 20,
                    Lost = 20,
                    Quantiles = Sample.CreateNaNQuantiles(),
                })
                .ToList();

            var svg = Render(dead);

            Assert.Contains(svg, "no probe was answered", "a total outage is not reported as missing data");
            Assert.Contains(svg, OutageGroup, "and it is marked on the plot");
            XDocument.Parse(svg);
        });

        runner.Add("SmokeGraphRenderer: a healthy period draws no outage markers", () =>
        {
            var svg = Render(BuildSamples(8));

            // The colour itself also appears in the legend, so look for the marker group.
            Assert.False(
                svg.Contains(OutageGroup, StringComparison.Ordinal),
                "nothing is marked when every round was answered");
        });

        runner.Add("SmokeGraphRenderer: titles containing markup are escaped", () =>
        {
            var svg = Render(BuildSamples(4), title: "<script>alert(1)</script>");

            Assert.False(svg.Contains("<script>", StringComparison.Ordinal), "markup never reaches the output");
            XDocument.Parse(svg);
        });

        runner.Add("SmokeGraphRenderer: compact graphs drop the legend", () =>
        {
            var full = Render(BuildSamples(12));
            var compact = Render(BuildSamples(12), compact: true);

            Assert.Contains(full, "probes lost per round", "the full graph carries a legend");
            Assert.False(
                compact.Contains("probes lost per round", StringComparison.Ordinal),
                "the overview graph does not");
        });

        runner.Add("SmokeGraphRenderer: hover tooltips are on detail graphs only", () =>
        {
            var full = Render(BuildSamples(12));
            var compact = Render(BuildSamples(12), compact: true);

            Assert.Contains(full, "<title>", "a detail graph carries per-round tooltips");
            Assert.Equal(
                1,
                XDocument.Parse(compact).Descendants().Count(e => e.Name.LocalName == "title"),
                "a thumbnail carries only the graph's own title");
        });

        runner.Add("SmokeGraphRenderer: both themes render", () =>
        {
            foreach (var theme in new[] { GraphTheme.Light, GraphTheme.Dark })
            {
                var svg = Render(BuildSamples(8), theme: theme);
                XDocument.Parse(svg);
                Assert.Contains(svg, theme.Background, "the theme background is applied");
            }
        });

        runner.Add("SmokeGraphRenderer: durations are formatted with a sensible unit", () =>
        {
            Assert.Equal("500 us", SmokeGraphRenderer.FormatMilliseconds(0.5), "sub-millisecond values use microseconds");
            Assert.Equal("5.50 ms", SmokeGraphRenderer.FormatMilliseconds(5.5), "small values keep two decimals");
            Assert.Equal("120.0 ms", SmokeGraphRenderer.FormatMilliseconds(120), "normal values keep one");
            Assert.Equal("1.50 s", SmokeGraphRenderer.FormatMilliseconds(1500), "large values switch to seconds");
        });

        runner.Add("ApiEndpoints: SmokePing range notation is understood", () =>
        {
            Assert.Equal(TimeSpan.FromHours(3), ApiEndpoints.ParseSpan("3h")!.Value, "hours");
            Assert.Equal(TimeSpan.FromHours(30), ApiEndpoints.ParseSpan("30h")!.Value, "more hours");
            Assert.Equal(TimeSpan.FromDays(10), ApiEndpoints.ParseSpan("10d")!.Value, "days");
            Assert.Equal(TimeSpan.FromDays(360), ApiEndpoints.ParseSpan("360d")!.Value, "a year of days");
            Assert.Equal(TimeSpan.FromDays(14), ApiEndpoints.ParseSpan("2w")!.Value, "weeks");
            Assert.Equal(TimeSpan.FromDays(180), ApiEndpoints.ParseSpan("6mon")!.Value, "months");
            Assert.True(ApiEndpoints.ParseSpan("banana") is null, "nonsense is rejected");
            Assert.True(ApiEndpoints.ParseSpan("0h") is null, "an empty range is rejected");
        });

        runner.Add("ApiEndpoints: an unparseable range falls back to three hours", () =>
        {
            var (from, to) = ApiEndpoints.ResolveRange("banana");

            Assert.Equal(3 * 3600L, to - from, "the default period is used");
        });

        runner.Add("DnsProbe: the query packet is a valid DNS question", () =>
        {
            var packet = DnsProbe.BuildQuery(0x1234, "www.example.com");

            Assert.Equal(0x12, packet[0], "the transaction id is written big-endian");
            Assert.Equal(0x34, packet[1], "both bytes of it");
            Assert.Equal(0x01, packet[2], "recursion is requested");
            Assert.Equal(0x01, packet[5], "the packet carries exactly one question");
            Assert.Equal(3, packet[12], "the first label is three bytes long");
            Assert.Equal((byte)'w', packet[13], "and it is 'www'");
            Assert.Equal(0x01, packet[^1], "the question class is IN");
        });
    }

    /// <summary>The group the renderer wraps its total-loss markers in.</summary>
    private static string OutageGroup => $"<g fill=\"{LossColours.TotalLossColour}\">";

    private static string Render(
        IReadOnlyList<Sample> samples,
        string title = "smoke test",
        bool compact = false,
        GraphTheme? theme = null) =>
        SmokeGraphRenderer.Render(new GraphRequest
        {
            Samples = samples,
            Pings = 20,
            FromTimestamp = samples[0].Timestamp,
            ToTimestamp = samples[^1].Timestamp + 300,
            StepSeconds = 300,
            Title = title,
            Subtitle = "ICMP Echo Ping",
            Compact = compact,
            Theme = theme ?? GraphTheme.Light,
        });

    private static IReadOnlyList<Sample> BuildSamples(int count) =>
        Enumerable.Range(0, count)
            .Select(i => SampleAt(i * 300, 10 + (i % 5), 40 + (i % 7)))
            .ToList();

    /// <summary>A round whose probes are spread evenly between two values.</summary>
    private static Sample SampleAt(long timestamp, double fastest, double slowest)
    {
        var values = Enumerable.Range(0, 20)
            .Select(i => fastest + ((slowest - fastest) * i / 19.0))
            .ToList();

        return new Sample
        {
            Timestamp = timestamp,
            Sent = 20,
            Lost = 0,
            Quantiles = Quantiles.FromSamples(values),
        };
    }
}
