using System.Xml.Linq;
using SmokePing.Net.Graphing;
using SmokePing.Net.Probes;
using SmokePing.Net.Storage;
using Xunit;
using SmokePing.Net.Web;

namespace SmokePing.Net.Tests;

public sealed class GraphingTests
{
    /// <summary>LossColours: a clean round is green and a dead one is dark red</summary>
    [Fact]
    public void LossColours_A_Clean_Round_Is_Green_And_A_Dead_One_Is_Dark_Red()
    {
            Verify.Equal("#26ff00", LossColours.ForLoss(0, 20), "no loss is green");
            Verify.Equal("#a00000", LossColours.ForLoss(20, 20), "total loss is dark red");
            Verify.Equal("#ff0000", LossColours.ForLoss(19, 20), "almost total loss is red");
    }

    /// <summary>LossColours: total loss stays dark red even for short rounds</summary>
    [Fact]
    public void LossColours_Total_Loss_Stays_Dark_Red_Even_For_Short_Rounds()
    {
            foreach (var pings in new[] { 1, 2, 3, 5, 10, 20 })
            {
                Verify.Equal("#a00000", LossColours.ForLoss(pings, pings), $"a dead round is dark red ({pings} pings)");
                Verify.Equal("#26ff00", LossColours.ForLoss(0, pings), $"a clean round is green ({pings} pings)");
            }
    }

    /// <summary>LossColours: the scale covers every possible loss count</summary>
    [Fact]
    public void LossColours_The_Scale_Covers_Every_Possible_Loss_Count()
    {
            foreach (var pings in new[] { 1, 2, 3, 5, 20, 50 })
            {
                var scale = LossColours.BuildScale(pings);

                Verify.Equal(0, scale[0].MaxLost, $"the first band is the clean one ({pings} pings)");
                Verify.Equal(pings, scale[^1].MaxLost, $"the last band reaches total loss ({pings} pings)");

                for (var i = 1; i < scale.Count; i++)
                {
                    Verify.True(
                        scale[i].MaxLost > scale[i - 1].MaxLost,
                        $"bands increase strictly ({pings} pings)");
                }

                // Every possible loss count must fall into exactly one band.
                for (var lost = 0; lost <= pings; lost++)
                {
                    var band = scale.First(b => lost <= b.MaxLost);
                    Verify.Equal(band.Colour, LossColours.ForLoss(lost, pings), $"{lost}/{pings} uses its band's colour");
                }
            }
    }

    /// <summary>YScale: the axis follows the median, not an outlier</summary>
    [Fact]
    public void YScale_The_Axis_Follows_The_Median_Not_An_Outlier()
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

            Verify.True(scale.HasData, "there is data to scale");
            Verify.True(scale.ToPixels(20) > 150, "the median sits high in the plot, not squashed at the bottom");
            Verify.Equal(200.0, scale.ToPixels(400), "the outlier clips to the frame instead of rescaling it");
            Verify.True(scale.Ticks.Count > 1, "the axis is labelled");
    }

    /// <summary>YScale: an empty period still produces a usable axis</summary>
    [Fact]
    public void YScale_An_Empty_Period_Still_Produces_A_Usable_Axis()
    {
            var scale = YScale.For([Sample.Empty(0)], 200);

            Verify.False(scale.HasData, "no data was recorded");
            Verify.True(scale.Ticks.Count > 0, "the axis is still drawn");
            Verify.Equal(0.0, scale.ToPixels(double.NaN), "NaN does not escape onto the canvas");
    }

    /// <summary>GraphStatistics: the round trip figures describe the median series</summary>
    [Fact]
    public void GraphStatistics_The_Round_Trip_Figures_Describe_The_Median_Series()
    {
            // The figures describe the per-round medians, not the spread of individual
            // probes, which is what the smoke already shows.
            var stats = GraphStatistics.Compute([
                SampleWithMedian(0, 20),
                SampleWithMedian(60, 30),
                SampleWithMedian(120, 40),
            ]);

            Verify.True(stats.HasData, "three rounds have data");
            Verify.Close(30, stats.MedianAverage, 0.001, "the average of the per-round medians");
            Verify.Close(20, stats.MedianMinimum, 0.001, "the lowest per-round median");
            Verify.Close(40, stats.MedianMaximum, 0.001, "the highest per-round median");
            Verify.Close(40, stats.MedianNow, 0.001, "the most recent per-round median");
            Verify.Equal(3, stats.RoundsWithData, "all three rounds counted");
            Verify.Close(0, stats.LossAverage, 0.001, "nothing was lost");
    }

    /// <summary>GraphStatistics: loss is reported as average, extremes and current</summary>
    [Fact]
    public void GraphStatistics_Loss_Is_Reported_As_Average_Extremes_And_Current()
    {
            var stats = GraphStatistics.Compute([
                SampleWithLoss(0, 20, 0),
                SampleWithLoss(300, 20, 10),
                SampleWithLoss(600, 20, 2),
            ]);

            Verify.Close(20, stats.LossAverage, 0.01, "the mean of 0%, 50% and 10%");
            Verify.Close(50, stats.LossMaximum, 0.01, "the worst round");
            Verify.Close(0, stats.LossMinimum, 0.01, "the best round");
            Verify.Close(10, stats.LossNow, 0.01, "the most recent round");
    }

    /// <summary>GraphStatistics: a period with no variation has no noise to divide by</summary>
    [Fact]
    public void GraphStatistics_A_Period_With_No_Variation_Has_No_Noise_To_Divide_By()
    {
            var stats = GraphStatistics.Compute([SampleAt(0, 20, 20), SampleAt(300, 20, 20)]);

            Verify.Close(0, stats.StandardDeviation, 0.001, "identical rounds do not vary");
            Verify.Close(0, stats.SignalToNoise, 0.001, "and the ratio does not divide by zero");
    }

    /// <summary>GraphStatistics: loss is counted even when nothing answered</summary>
    [Fact]
    public void GraphStatistics_Loss_Is_Counted_Even_When_Nothing_Answered()
    {
            var stats = GraphStatistics.Compute([
                new Sample { Timestamp = 0, Sent = 10, Lost = 10, Quantiles = Sample.CreateNaNQuantiles() },
            ]);

            Verify.False(stats.HasData, "no round trip times were recorded");
            Verify.Close(100, stats.LossAverage, 0.001, "but the loss is still reported");
    }

    /// <summary>SmokeGraphRenderer: the output is well-formed SVG</summary>
    [Fact]
    public void SmokeGraphRenderer_The_Output_Is_Well_Formed_SVG()
    {
            var svg = Render(BuildSamples(24));

            var document = XDocument.Parse(svg);
            Verify.Equal("svg", document.Root!.Name.LocalName, "the root element is an svg");
            Verify.Contains(svg, "smoke", "the title is carried into the picture");
    }

    /// <summary>SmokeGraphLayout: the smoke is laid out as nested bands</summary>
    [Fact]
    public void SmokeGraphLayout_The_Smoke_Is_Laid_Out_As_Nested_Bands()
    {
            var bands = Scene(BuildSamples(24)).Primitives.OfType<PolygonPrimitive>().ToList();

            Verify.Equal(Sample.QuantileCount / 2, bands.Count, "one band per pair of opposite quantiles");

            // Each band is a closed ribbon: the upper edge forward, the lower edge back.
            foreach (var band in bands)
            {
                Verify.Equal(48, band.Points.Count, "both edges of the ribbon are present");
            }
    }

    /// <summary>SmokeGraphLayout: a gap splits the smoke instead of bridging it</summary>
    [Fact]
    public void SmokeGraphLayout_A_Gap_Splits_The_Smoke_Instead_Of_Bridging_It()
    {
            var samples = BuildSamples(24).ToList();
            samples[12] = Sample.Empty(samples[12].Timestamp);

            var bands = Scene(samples).Primitives.OfType<PolygonPrimitive>().Count();

            Verify.Equal(Sample.QuantileCount / 2 * 2, bands, "each band becomes two separate runs");
    }

    /// <summary>SmokeGraphRenderer: lossy rounds colour the median line</summary>
    [Fact]
    public void SmokeGraphRenderer_Lossy_Rounds_Colour_The_Median_Line()
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
            Verify.Contains(svg, "#26ff00", "clean rounds stay green");
    }

    /// <summary>SmokeGraphRenderer: an empty period says so rather than drawing nothing</summary>
    [Fact]
    public void SmokeGraphRenderer_An_Empty_Period_Says_So_Rather_Than_Drawing_Nothing()
    {
            var svg = Render([Sample.Empty(0), Sample.Empty(300)]);

            Verify.Contains(svg, "no data for this period", "the reader is told why the graph is blank");
            XDocument.Parse(svg);
    }

    /// <summary>SmokeGraphRenderer: an outage is distinguished from a gap</summary>
    [Fact]
    public void SmokeGraphRenderer_An_Outage_Is_Distinguished_From_A_Gap()
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

            Verify.Contains(svg, "no probe was answered", "a total outage is not reported as missing data");
            Verify.Equal(
                dead.Count,
                OutageMarkers(Scene(dead, compact: true)),
                "every dead round is marked on the plot");
            XDocument.Parse(svg);
    }

    /// <summary>SmokeGraphRenderer: a healthy period draws no outage markers</summary>
    [Fact]
    public void SmokeGraphRenderer_A_Healthy_Period_Draws_No_Outage_Markers()
    {
            // The colour itself also appears in the legend, so count the plot markers.
            Verify.Equal(
                0,
                OutageMarkers(Scene(BuildSamples(8), compact: true)),
                "nothing is marked when every round was answered");
    }

    /// <summary>SmokeGraphRenderer: titles containing markup are escaped</summary>
    [Fact]
    public void SmokeGraphRenderer_Titles_Containing_Markup_Are_Escaped()
    {
            var svg = Render(BuildSamples(4), title: "<script>alert(1)</script>");

            Verify.False(svg.Contains("<script>", StringComparison.Ordinal), "markup never reaches the output");
            XDocument.Parse(svg);
    }

    /// <summary>SmokeGraphRenderer: compact graphs drop the legend</summary>
    [Fact]
    public void SmokeGraphRenderer_Compact_Graphs_Drop_The_Legend()
    {
            var full = Render(BuildSamples(12));
            var compact = Render(BuildSamples(12), compact: true);

            Verify.Contains(full, "loss color:", "the full graph carries a legend");
            Verify.Contains(full, "median rtt:", "with the round trip figures");
            Verify.Contains(full, "packet loss:", "and the loss figures");
            Verify.Contains(full, "probe:", "and what took the measurements");
            Verify.False(
                compact.Contains("loss color:", StringComparison.Ordinal),
                "the overview graph does not");
    }

    /// <summary>SmokeGraphRenderer: hover tooltips are on detail graphs only</summary>
    [Fact]
    public void SmokeGraphRenderer_Hover_Tooltips_Are_On_Detail_Graphs_Only()
    {
            var full = Render(BuildSamples(12));
            var compact = Render(BuildSamples(12), compact: true);

            Verify.Contains(full, "<title>", "a detail graph carries per-round tooltips");
            Verify.Equal(
                1,
                XDocument.Parse(compact).Descendants().Count(e => e.Name.LocalName == "title"),
                "a thumbnail carries only the graph's own title");
    }

    /// <summary>SmokeGraphRenderer: both themes render</summary>
    [Fact]
    public void SmokeGraphRenderer_Both_Themes_Render()
    {
            foreach (var theme in new[] { GraphTheme.Light, GraphTheme.Dark })
            {
                var svg = Render(BuildSamples(8), theme: theme);
                XDocument.Parse(svg);
                Verify.Contains(svg, theme.Background, "the theme background is applied");
            }
    }

    /// <summary>SmokeGraphRenderer: durations are formatted with a sensible unit</summary>
    [Fact]
    public void SmokeGraphRenderer_Durations_Are_Formatted_With_A_Sensible_Unit()
    {
            Verify.Equal("500 us", SmokeGraphRenderer.FormatMilliseconds(0.5), "sub-millisecond values use microseconds");
            Verify.Equal("5.50 ms", SmokeGraphRenderer.FormatMilliseconds(5.5), "small values keep two decimals");
            Verify.Equal("120.0 ms", SmokeGraphRenderer.FormatMilliseconds(120), "normal values keep one");
            Verify.Equal("1.50 s", SmokeGraphRenderer.FormatMilliseconds(1500), "large values switch to seconds");
    }

    /// <summary>ApiEndpoints: SmokePing range notation is understood</summary>
    [Fact]
    public void ApiEndpoints_SmokePing_Range_Notation_Is_Understood()
    {
            Verify.Equal(TimeSpan.FromHours(3), ApiEndpoints.ParseSpan("3h")!.Value, "hours");
            Verify.Equal(TimeSpan.FromHours(30), ApiEndpoints.ParseSpan("30h")!.Value, "more hours");
            Verify.Equal(TimeSpan.FromDays(10), ApiEndpoints.ParseSpan("10d")!.Value, "days");
            Verify.Equal(TimeSpan.FromDays(360), ApiEndpoints.ParseSpan("360d")!.Value, "a year of days");
            Verify.Equal(TimeSpan.FromDays(14), ApiEndpoints.ParseSpan("2w")!.Value, "weeks");
            Verify.Equal(TimeSpan.FromDays(180), ApiEndpoints.ParseSpan("6mon")!.Value, "months");
            Verify.True(ApiEndpoints.ParseSpan("banana") is null, "nonsense is rejected");
            Verify.True(ApiEndpoints.ParseSpan("0h") is null, "an empty range is rejected");
    }

    /// <summary>ApiEndpoints: an unparseable range falls back to three hours</summary>
    [Fact]
    public void ApiEndpoints_An_Unparseable_Range_Falls_Back_To_Three_Hours()
    {
            var (from, to) = ApiEndpoints.ResolveRange("banana");

            Verify.Equal(3 * 3600L, to - from, "the default period is used");
    }

    /// <summary>DnsProbe: record types are understood, and nonsense is rejected</summary>
    [Fact]
    public void DnsProbe_Record_Types_Are_Understood()
    {
            // The probe used to build its own query packets and could only ask for A
            // records. Record type is a configuration setting now, so the names have
            // to be validated where a typo can still be reported.
            Verify.True(DnsProbe.IsKnownRecordType("A"), "A is a record type");
            Verify.True(DnsProbe.IsKnownRecordType("aaaa"), "and case does not matter");
            Verify.True(DnsProbe.IsKnownRecordType("MX"), "so is MX");
            Verify.True(DnsProbe.IsKnownRecordType("TXT"), "and TXT");
            Verify.False(DnsProbe.IsKnownRecordType("BANANA"), "BANANA is not");

            Verify.Throws<ArgumentException>(
                () => DnsProbe.ParseRecordType("BANANA"),
                "and parsing one says so rather than quietly asking for something else");
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
