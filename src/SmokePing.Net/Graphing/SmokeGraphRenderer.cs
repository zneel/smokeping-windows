using System.Globalization;
using System.Net;
using System.Text;
using SmokePing.Net.Storage;

namespace SmokePing.Net.Graphing;

/// <summary>Everything needed to draw one graph.</summary>
public sealed class GraphRequest
{
    public required IReadOnlyList<Sample> Samples { get; init; }

    /// <summary>Probes per round; decides the loss colour scale.</summary>
    public required int Pings { get; init; }

    public required long FromTimestamp { get; init; }

    public required long ToTimestamp { get; init; }

    /// <summary>Seconds covered by one sample, used to size the columns.</summary>
    public required int StepSeconds { get; init; }

    public required string Title { get; init; }

    /// <summary>Probe description drawn under the title.</summary>
    public string Subtitle { get; init; } = string.Empty;

    public int PlotWidth { get; init; } = 600;

    public int PlotHeight { get; init; } = 200;

    /// <summary>Small graphs drop the legend and the axis labels.</summary>
    public bool Compact { get; init; }

    public GraphTheme Theme { get; init; } = GraphTheme.Light;

    /// <summary>Offset applied to timestamps before formatting the time axis.</summary>
    public TimeSpan UtcOffset { get; init; } = TimeSpan.Zero;
}

/// <summary>
/// Draws a SmokePing graph as standalone SVG: the median round-trip time as a line
/// coloured by packet loss, wrapped in the "smoke" - nested grey bands showing the
/// spread of the individual probes in each round.
///
/// Both the smoke shading and the loss colours follow the original implementation, so
/// graphs stay comparable with an existing SmokePing installation.
/// </summary>
public static class SmokeGraphRenderer
{
    private const int MarginLeft = 62;
    private const int MarginRight = 18;
    private const int MarginTop = 34;
    private const int LegendHeight = 76;
    private const int AxisHeight = 22;

    public static string Render(GraphRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var theme = request.Theme;
        var marginTop = HeadingHeight(request);
        var marginBottom = request.Compact ? AxisHeight : AxisHeight + LegendHeight;
        var width = MarginLeft + request.PlotWidth + MarginRight;
        var height = marginTop + request.PlotHeight + marginBottom;

        var plot = new PlotArea(MarginLeft, marginTop, request.PlotWidth, request.PlotHeight);
        var scale = YScale.For(request.Samples, request.PlotHeight);

        var svg = new StringBuilder(16 * 1024);
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\" width=\"{width}\" height=\"{height}\" font-family=\"'Segoe UI',system-ui,sans-serif\" role=\"img\">");
        svg.Append(CultureInfo.InvariantCulture, $"<title>{Escape(request.Title)}</title>");
        svg.Append(CultureInfo.InvariantCulture, $"<rect width=\"{width}\" height=\"{height}\" fill=\"{theme.Background}\"/>");
        svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{plot.Left}\" y=\"{plot.Top}\" width=\"{plot.Width}\" height=\"{plot.Height}\" fill=\"{theme.PlotBackground}\"/>");

        DrawHeading(svg, request, plot, marginTop);
        DrawNoDataBands(svg, request, plot);
        DrawGrid(svg, request, plot, scale);
        DrawOutageMarkers(svg, request, plot);

        if (scale.HasData)
        {
            DrawSmoke(svg, request, plot, scale);
            DrawMedian(svg, request, plot, scale);
        }
        else
        {
            // Rounds that were taken but answered by nothing are not the same as no
            // rounds at all: one is an outage, the other is a hole in the record.
            var measured = request.Samples.Any(sample => sample.Sent > 0);
            var message = measured ? "no probe was answered in this period" : "no data for this period";
            var cx = plot.Left + (plot.Width / 2);
            var cy = plot.Top + (plot.Height / 2);
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{cx}\" y=\"{cy}\" text-anchor=\"middle\" font-size=\"12\" fill=\"{theme.MutedText}\">{message}</text>");
        }

        svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{plot.Left}\" y=\"{plot.Top}\" width=\"{plot.Width}\" height=\"{plot.Height}\" fill=\"none\" stroke=\"{theme.Axis}\" stroke-width=\"1\"/>");

        DrawTimeAxis(svg, request, plot);

        // Overview thumbnails squeeze a whole day into a column a pixel wide, where a
        // per-round tooltip is unusable - and a wall of them would dominate the file.
        if (!request.Compact)
        {
            DrawHoverTargets(svg, request, plot);
        }

        if (!request.Compact)
        {
            DrawLegend(svg, request, plot);
        }

        svg.Append("</svg>");
        return svg.ToString();
    }

    /// <summary>
    /// Height reserved above the plot. A caller that already labels the graph can pass
    /// an empty title, and the space it would have taken is given back to the plot.
    /// </summary>
    private static int HeadingHeight(GraphRequest request)
    {
        var height = request.Title.Length > 0 ? (request.Compact ? 18 : 22) : 8;
        if (HasSubtitle(request))
        {
            height += 12;
        }

        return height;
    }

    private static bool HasSubtitle(GraphRequest request) => !request.Compact && request.Subtitle.Length > 0;

    private static void DrawHeading(StringBuilder svg, GraphRequest request, PlotArea plot, int marginTop)
    {
        var theme = request.Theme;

        if (request.Title.Length > 0)
        {
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{plot.Left}\" y=\"{(request.Compact ? 12 : 15)}\" font-size=\"{(request.Compact ? 11 : 13)}\" font-weight=\"600\" fill=\"{theme.Text}\">{Escape(request.Title)}</text>");
        }

        if (HasSubtitle(request))
        {
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{plot.Left}\" y=\"{marginTop - 4}\" font-size=\"10\" fill=\"{theme.MutedText}\">{Escape(request.Subtitle)}</text>");
        }
    }

    /// <summary>Shades the periods where nothing was recorded, so gaps read as gaps.</summary>
    private static void DrawNoDataBands(StringBuilder svg, GraphRequest request, PlotArea plot)
    {
        foreach (var sample in request.Samples)
        {
            if (sample.Sent > 0)
            {
                continue;
            }

            var x = plot.XFor(sample.Timestamp, request);
            var w = plot.WidthFor(request);
            svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{F(x)}\" y=\"{plot.Top}\" width=\"{F(w)}\" height=\"{plot.Height}\" fill=\"{request.Theme.NoData}\"/>");
        }
    }

    /// <summary>
    /// Marks rounds where every probe was lost. There is no median to draw for those,
    /// so without this an outage looks exactly like a gap in the record.
    /// </summary>
    private static void DrawOutageMarkers(StringBuilder svg, GraphRequest request, PlotArea plot)
    {
        var width = Math.Max(plot.WidthFor(request), 1.0);
        var height = Math.Min(5, plot.Height);
        var started = false;

        foreach (var sample in request.Samples)
        {
            if (sample.Sent == 0 || sample.Lost < sample.Sent)
            {
                continue;
            }

            if (!started)
            {
                svg.Append(CultureInfo.InvariantCulture, $"<g fill=\"{LossColours.TotalLossColour}\">");
                started = true;
            }

            var x = plot.XFor(sample.Timestamp, request);
            svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{F(x)}\" y=\"{plot.Bottom - height}\" width=\"{F(width)}\" height=\"{height}\"/>");
        }

        if (started)
        {
            svg.Append("</g>");
        }
    }

    private static void DrawGrid(StringBuilder svg, GraphRequest request, PlotArea plot, YScale scale)
    {
        var theme = request.Theme;
        foreach (var tick in scale.Ticks)
        {
            var y = plot.Bottom - scale.ToPixels(tick);
            svg.Append(CultureInfo.InvariantCulture, $"<line x1=\"{plot.Left}\" y1=\"{F(y)}\" x2=\"{plot.Right}\" y2=\"{F(y)}\" stroke=\"{theme.Grid}\" stroke-width=\"1\"/>");
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{plot.Left - 6}\" y=\"{F(y + 3.5)}\" text-anchor=\"end\" font-size=\"10\" fill=\"{theme.MutedText}\">{Escape(FormatMilliseconds(tick))}</text>");
        }

        if (!request.Compact)
        {
            var labelY = plot.Top + (plot.Height / 2);
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"12\" y=\"{labelY}\" font-size=\"10\" fill=\"{theme.MutedText}\" transform=\"rotate(-90 12 {labelY})\" text-anchor=\"middle\">round trip time</text>");
        }
    }

    /// <summary>
    /// Draws the smoke: nested bands between opposite quantiles, from the full
    /// min-max range on the outside to the innermost pair around the median.
    /// The grey ramp is upstream's - lighter for the outer, rarer values.
    /// </summary>
    private static void DrawSmoke(StringBuilder svg, GraphRequest request, PlotArea plot, YScale scale)
    {
        var bands = Sample.QuantileCount / 2;
        var half = bands + 0.5;

        for (var band = 0; band < bands; band++)
        {
            var lower = band;
            var upper = Sample.QuantileCount - 1 - band;

            var level = (int)(190 / half * (half - (band + 1))) + 50;
            var grey = level.ToString("x2", CultureInfo.InvariantCulture);
            var colour = request.Theme == GraphTheme.Dark
                ? InvertGrey(level)
                : $"#{grey}{grey}{grey}";

            foreach (var run in ContiguousRuns(request.Samples, lower, upper))
            {
                var path = new StringBuilder();
                path.Append('M');
                foreach (var sample in run)
                {
                    var x = plot.XFor(sample.Timestamp, request) + (plot.WidthFor(request) / 2);
                    path.Append(CultureInfo.InvariantCulture, $"{F(x)},{F(plot.Bottom - scale.ToPixels(sample.Quantiles[upper]))} ");
                }

                for (var i = run.Count - 1; i >= 0; i--)
                {
                    var sample = run[i];
                    var x = plot.XFor(sample.Timestamp, request) + (plot.WidthFor(request) / 2);
                    path.Append(CultureInfo.InvariantCulture, $"{F(x)},{F(plot.Bottom - scale.ToPixels(sample.Quantiles[lower]))} ");
                }

                path.Append('Z');
                svg.Append(CultureInfo.InvariantCulture, $"<path d=\"{path}\" fill=\"{colour}\" fill-opacity=\"0.85\" stroke=\"none\"/>");
            }
        }
    }

    /// <summary>
    /// Draws the median as a line whose colour changes with the packet loss of each
    /// round, which is what makes loss visible without a second graph.
    /// </summary>
    private static void DrawMedian(StringBuilder svg, GraphRequest request, PlotArea plot, YScale scale)
    {
        var half = plot.WidthFor(request) / 2;
        Sample? previous = null;

        // The stroke width and cap are the same for every segment; only the colour
        // changes, so hoisting them onto a group keeps long graphs from bloating.
        svg.Append("<g stroke-width=\"1.6\" stroke-linecap=\"round\">");

        foreach (var sample in request.Samples)
        {
            if (sample.Median is not { } median)
            {
                previous = null;
                continue;
            }

            var colour = LossColours.ForLoss(sample.Lost, sample.Sent == 0 ? request.Pings : sample.Sent);
            var x = plot.XFor(sample.Timestamp, request) + half;
            var y = plot.Bottom - scale.ToPixels((float)median);

            if (previous?.Median is { } previousMedian)
            {
                var px = plot.XFor(previous.Timestamp, request) + half;
                var py = plot.Bottom - scale.ToPixels((float)previousMedian);
                svg.Append(CultureInfo.InvariantCulture, $"<line x1=\"{F(px)}\" y1=\"{F(py)}\" x2=\"{F(x)}\" y2=\"{F(y)}\" stroke=\"{colour}\"/>");
            }
            else
            {
                svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{F(x)}\" cy=\"{F(y)}\" r=\"1.1\" fill=\"{colour}\"/>");
            }

            previous = sample;
        }

        svg.Append("</g>");
    }

    private static void DrawTimeAxis(StringBuilder svg, GraphRequest request, PlotArea plot)
    {
        var theme = request.Theme;
        var span = request.ToTimestamp - request.FromTimestamp;
        var (interval, format) = ChooseTimeTicks(span);

        var offsetSeconds = (long)request.UtcOffset.TotalSeconds;
        var first = ((request.FromTimestamp + offsetSeconds) / interval * interval) - offsetSeconds;

        for (var t = first; t <= request.ToTimestamp; t += interval)
        {
            if (t < request.FromTimestamp)
            {
                continue;
            }

            var x = plot.XFor(t, request);
            svg.Append(CultureInfo.InvariantCulture, $"<line x1=\"{F(x)}\" y1=\"{plot.Top}\" x2=\"{F(x)}\" y2=\"{plot.Bottom}\" stroke=\"{theme.Grid}\" stroke-width=\"1\"/>");
            svg.Append(CultureInfo.InvariantCulture, $"<line x1=\"{F(x)}\" y1=\"{plot.Bottom}\" x2=\"{F(x)}\" y2=\"{plot.Bottom + 4}\" stroke=\"{theme.Axis}\" stroke-width=\"1\"/>");

            var label = DateTimeOffset.FromUnixTimeSeconds(t).ToOffset(request.UtcOffset).ToString(format, CultureInfo.InvariantCulture);
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{F(x)}\" y=\"{plot.Bottom + 15}\" text-anchor=\"middle\" font-size=\"9\" fill=\"{theme.MutedText}\">{Escape(label)}</text>");
        }
    }

    /// <summary>Adds one invisible column per sample carrying a native tooltip.</summary>

    private static void DrawHoverTargets(StringBuilder svg, GraphRequest request, PlotArea plot)
    {
        var w = Math.Max(plot.WidthFor(request), 1.0);

        svg.Append("<g fill=\"transparent\">");

        foreach (var sample in request.Samples)
        {
            if (sample.Sent == 0)
            {
                continue;
            }

            var x = plot.XFor(sample.Timestamp, request);
            var time = DateTimeOffset.FromUnixTimeSeconds(sample.Timestamp).ToOffset(request.UtcOffset);
            var median = sample.Median is { } m ? FormatMilliseconds((float)m) : "no response";
            var tooltip = string.Create(
                CultureInfo.InvariantCulture,
                $"{time:yyyy-MM-dd HH:mm}  median {median}  loss {sample.Lost}/{sample.Sent}");

            svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{F(x)}\" y=\"{plot.Top}\" width=\"{F(w)}\" height=\"{plot.Height}\"><title>{Escape(tooltip)}</title></rect>");
        }

        svg.Append("</g>");
    }

    /// <summary>Draws the loss colour key and the summary statistics for the period.</summary>
    private static void DrawLegend(StringBuilder svg, GraphRequest request, PlotArea plot)
    {
        var theme = request.Theme;
        var top = plot.Bottom + AxisHeight + 12;

        svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{plot.Left}\" y=\"{top}\" font-size=\"10\" fill=\"{theme.MutedText}\">probes lost per round</text>");

        var x = plot.Left;
        var y = top + 8;
        foreach (var band in LossColours.BuildScale(request.Pings))
        {
            svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{y}\" width=\"9\" height=\"9\" fill=\"{band.Colour}\"/>");
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{x + 13}\" y=\"{y + 8}\" font-size=\"9\" fill=\"{theme.Text}\">{Escape(band.Label)}</text>");
            x += 21 + (band.Label.Length * 5);
        }

        var stats = GraphStatistics.Compute(request.Samples);
        var summary = stats.HasData
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"median {FormatMilliseconds((float)stats.Median)}   min {FormatMilliseconds((float)stats.Minimum)}   max {FormatMilliseconds((float)stats.Maximum)}   sd {FormatMilliseconds((float)stats.StandardDeviation)}   loss {stats.LossPercent:F1}%")
            : "no data for this period";

        svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{plot.Left}\" y=\"{y + 26}\" font-size=\"10\" fill=\"{theme.Text}\">{Escape(summary)}</text>");
    }

    /// <summary>
    /// Splits the samples into runs where both quantiles of a band are present, so a
    /// gap in the data leaves a hole instead of a band drawn straight across it.
    /// </summary>
    private static List<List<Sample>> ContiguousRuns(IReadOnlyList<Sample> samples, int lower, int upper)
    {
        var runs = new List<List<Sample>>();
        List<Sample>? current = null;

        foreach (var sample in samples)
        {
            if (float.IsNaN(sample.Quantiles[lower]) || float.IsNaN(sample.Quantiles[upper]))
            {
                current = null;
                continue;
            }

            if (current is null)
            {
                current = [];
                runs.Add(current);
            }

            current.Add(sample);
        }

        // A single point has no area to fill.
        runs.RemoveAll(run => run.Count < 2);
        return runs;
    }

    private static (long Interval, string Format) ChooseTimeTicks(long spanSeconds) => spanSeconds switch
    {
        <= 2 * 3600 => (900, "HH:mm"),
        <= 8 * 3600 => (3600, "HH:mm"),
        <= 36 * 3600 => (6 * 3600, "HH:mm"),
        <= 5 * 86400 => (86400, "MMM d"),
        <= 40 * 86400 => (7 * 86400, "MMM d"),
        <= 200 * 86400 => (30 * 86400, "MMM d"),
        _ => (90 * 86400, "MMM yyyy"),
    };

    /// <summary>Formats a duration the way SmokePing labels its axes: unit-scaled, never noisy.</summary>
    public static string FormatMilliseconds(double milliseconds) => milliseconds switch
    {
        < 1 => string.Create(CultureInfo.InvariantCulture, $"{milliseconds * 1000:F0} us"),
        < 10 => string.Create(CultureInfo.InvariantCulture, $"{milliseconds:F2} ms"),
        < 1000 => string.Create(CultureInfo.InvariantCulture, $"{milliseconds:F1} ms"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{milliseconds / 1000:F2} s"),
    };

    /// <summary>Maps upstream's grey ramp onto a dark background.</summary>
    private static string InvertGrey(int level)
    {
        var inverted = (255 - level).ToString("x2", CultureInfo.InvariantCulture);
        return $"#{inverted}{inverted}{inverted}";
    }

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    private sealed record PlotArea(int Left, int Top, int Width, int Height)
    {
        public int Right => Left + Width;

        public int Bottom => Top + Height;

        /// <summary>Left edge of the column representing a sample.</summary>
        public double XFor(long timestamp, GraphRequest request)
        {
            var span = Math.Max(request.ToTimestamp - request.FromTimestamp, 1);
            return Left + ((double)(timestamp - request.FromTimestamp) / span * Width);
        }

        /// <summary>Width of one sample column in pixels.</summary>
        public double WidthFor(GraphRequest request)
        {
            var span = Math.Max(request.ToTimestamp - request.FromTimestamp, 1);
            return (double)request.StepSeconds / span * Width;
        }
    }
}
