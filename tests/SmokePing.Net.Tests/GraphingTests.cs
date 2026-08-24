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

        runner.Add("YScale: the axis follows the median, not an outlier", () =>
        {
            // A steady 20ms target with one 400ms probe. Scaling to the outlier would
            // squash the line everyone reads into the bottom of the plot, so the axis
            // tracks the median and lets the outlier clip - as the original does.
            var steady = SampleWithMedian(0, 20);
            var spike = new Sample
            {
                Timestamp = 300,
                Sent = 20,
                Lost = 0,
                Quantiles = Quantiles.FromSamples([20.0, 400.0]),
                MedianValue = 20,
            };

            var scale = YScale.For([steady, spike], 200);

            Assert.True(scale.HasData, "there is data to scale");
            Assert.True(scale.ToPixels(20) > 150, "the median sits high in the plot, not squashed at the bottom");
            Assert.Equal(200.0, scale.ToPixels(400), "the outlier clips to the frame instead of rescaling it");
            Assert.True(scale.Ticks.Count > 1, "the axis is labelled");
        });

        runner.Add("YScale: an empty period still produces a usable axis", () =>
        {
            var scale = YScale.For([Sample.Empty(0)], 200);

            Assert.False(scale.HasData, "no data was recorded");
            Assert.True(scale.Ticks.Count > 0, "the axis is still drawn");
            Assert.Equal(0.0, scale.ToPixels(double.NaN), "NaN does not escape onto the canvas");
        });

        runner.Add("GraphStatistics: the round trip figures describe the median series", () =>
        {
            // The figures describe the per-round medians, not the spread of individual
            // probes, which is what the smoke already shows.
            var stats = GraphStatistics.Compute([
                SampleWithMedian(0, 20),
                SampleWithMedian(60, 30),
                SampleWithMedian(120, 40),
            ]);

            Assert.True(stats.HasData, "three rounds have data");
            Assert.Close(30, stats.MedianAverage, 0.001, "the average of the per-round medians");
            Assert.Close(20, stats.MedianMinimum, 0.001, "the lowest per-round median");
            Assert.Close(40, stats.MedianMaximum, 0.001, "the highest per-round median");
            Assert.Close(40, stats.MedianNow, 0.001, "the most recent per-round median");
            Assert.Equal(3, stats.RoundsWithData, "all three rounds counted");
            Assert.Close(0, stats.LossAverage, 0.001, "nothing was lost");
        });

        runner.Add("GraphStatistics: loss is reported as average, extremes and current", () =>
        {
            var stats = GraphStatistics.Compute([
                SampleWithLoss(0, 20, 0),
                SampleWithLoss(300, 20, 10),
                SampleWithLoss(600, 20, 2),
            ]);

            Assert.Close(20, stats.LossAverage, 0.01, "the mean of 0%, 50% and 10%");
            Assert.Close(50, stats.LossMaximum, 0.01, "the worst round");
            Assert.Close(0, stats.LossMinimum, 0.01, "the best round");
            Assert.Close(10, stats.LossNow, 0.01, "the most recent round");
        });

        runner.Add("GraphStatistics: a period with no variation has no noise to divide by", () =>
        {
            var stats = GraphStatistics.Compute([SampleAt(0, 20, 20), SampleAt(300, 20, 20)]);

            Assert.Close(0, stats.StandardDeviation, 0.001, "identical rounds do not vary");
            Assert.Close(0, stats.SignalToNoise, 0.001, "and the ratio does not divide by zero");
        });

        runner.Add("GraphStatistics: loss is counted even when nothing answered", () =>
        {
            var stats = GraphStatistics.Compute([
                new Sample { Timestamp = 0, Sent = 10, Lost = 10, Quantiles = Sample.CreateNaNQuantiles() },
            ]);

            Assert.False(stats.HasData, "no round trip times were recorded");
            Assert.Close(100, stats.LossAverage, 0.001, "but the loss is still reported");
        });

        runner.Add("SmokeGraphRenderer: the output is well-formed SVG", () =>
        {
            var svg = Render(BuildSamples(24));

            var document = XDocument.Parse(svg);
            Assert.Equal("svg", document.Root!.Name.LocalName, "the root element is an svg");
            Assert.Contains(svg, "smoke", "the title is carried into the picture");
        });

        runner.Add("SmokeGraphLayout: the smoke is laid out as nested bands", () =>
        {
            var bands = Scene(BuildSamples(24)).Primitives.OfType<PolygonPrimitive>().ToList();

            Assert.Equal(Sample.QuantileCount / 2, bands.Count, "one band per pair of opposite quantiles");

            // Each band is a closed ribbon: the upper edge forward, the lower edge back.
            foreach (var band in bands)
            {
                Assert.Equal(48, band.Points.Count, "both edges of the ribbon are present");
            }
        });

        runner.Add("SmokeGraphLayout: a gap splits the smoke instead of bridging it", () =>
        {
            var samples = BuildSamples(24).ToList();
            samples[12] = Sample.Empty(samples[12].Timestamp);

            var bands = Scene(samples).Primitives.OfType<PolygonPrimitive>().Count();

            Assert.Equal(Sample.QuantileCount / 2 * 2, bands, "each band becomes two separate runs");
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
            Assert.Equal(
                dead.Count,
                OutageMarkers(Scene(dead, compact: true)),
                "every dead round is marked on the plot");
            XDocument.Parse(svg);
        });

        runner.Add("SmokeGraphRenderer: a healthy period draws no outage markers", () =>
        {
            // The colour itself also appears in the legend, so count the plot markers.
            Assert.Equal(
                0,
                OutageMarkers(Scene(BuildSamples(8), compact: true)),
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

            Assert.Contains(full, "loss color:", "the full graph carries a legend");
            Assert.Contains(full, "median rtt:", "with the round trip figures");
            Assert.Contains(full, "packet loss:", "and the loss figures");
            Assert.Contains(full, "probe:", "and what took the measurements");
            Assert.False(
                compact.Contains("loss color:", StringComparison.Ordinal),
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

    /// <summary>
    /// Counts the bars marking rounds in which every probe was lost. Callers pass a
    /// compact scene so the legend's swatch in the same colour is not counted.
    /// </summary>
    private static int OutageMarkers(GraphScene scene) => scene.Primitives
        .OfType<RectanglePrimitive>()
        .Count(r => r.Fill == LossColours.TotalLossColour);

    /// <summary>A clean round with a known median, for tests about the figures.</summary>
    private static Sample SampleWithMedian(long timestamp, double median) => new()
    {
        Timestamp = timestamp,
        Sent = 20,
        Lost = 0,
        Quantiles = Quantiles.FromSamples([median]),
        MedianValue = (float)median,
    };

    private static GraphScene Scene(IReadOnlyList<Sample> samples, bool compact = false) =>
        SmokeGraphLayout.Build(BuildRequest(samples, compact: compact));

    private static string Render(
        IReadOnlyList<Sample> samples,
        string title = "smoke test",
        bool compact = false,
        GraphTheme? theme = null) =>
        SmokeGraphRenderer.Render(BuildRequest(samples, title, compact, theme));

    private static GraphRequest BuildRequest(
        IReadOnlyList<Sample> samples,
        string title = "smoke test",
        bool compact = false,
        GraphTheme? theme = null) =>
        new()
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
        };

    private static IReadOnlyList<Sample> BuildSamples(int count) =>
        Enumerable.Range(0, count)
            .Select(i => SampleAt(i * 300, 10 + (i % 5), 40 + (i % 7)))
            .ToList();

    /// <summary>A round with a given number of probes lost, all answers at 20ms.</summary>
    private static Sample SampleWithLoss(long timestamp, int sent, int lost)
    {
        var received = Enumerable.Repeat(20.0, sent - lost).ToList();
        return new Sample
        {
            Timestamp = timestamp,
            Sent = sent,
            Lost = lost,
            Quantiles = Quantiles.FromSamples(received, sent),
            MedianValue = Quantiles.Median(received),
        };
    }

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
            MedianValue = Quantiles.Median(values),
        };
    }
}
