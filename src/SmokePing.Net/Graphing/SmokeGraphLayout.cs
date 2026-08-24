using System.Globalization;
using SmokePing.Net.Storage;

namespace SmokePing.Net.Graphing;

/// <summary>
/// Lays out a SmokePing graph: the median round-trip time as a line coloured by
/// packet loss, wrapped in the "smoke" - nested grey bands showing the spread of the
/// individual probes in each round.
///
/// The result is a backend-independent <see cref="GraphScene"/>. Both the smoke
/// shading and the loss colours follow the original implementation, so graphs stay
/// comparable with an existing SmokePing installation.
/// </summary>
public static class SmokeGraphLayout
{
    private const int MarginLeft = 62;
    private const int MarginRight = 18;
    private const int LegendHeight = 76;
    private const int AxisHeight = 22;

    /// <summary>
    /// Pixels the scene occupies outside the plot itself. A caller fitting a graph to
    /// a window subtracts these from the space it has to get the plot size to ask for.
    /// </summary>
    public static (int Horizontal, int Vertical) ChromeFor(GraphRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var vertical = HeadingHeight(request) + (request.Compact ? AxisHeight : AxisHeight + LegendHeight);
        return (MarginLeft + MarginRight, vertical);
    }

    public static GraphScene Build(GraphRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var theme = request.Theme;
        var marginTop = HeadingHeight(request);
        var marginBottom = request.Compact ? AxisHeight : AxisHeight + LegendHeight;
        var width = MarginLeft + request.PlotWidth + MarginRight;
        var height = marginTop + request.PlotHeight + marginBottom;

        var plot = new PlotArea(MarginLeft, marginTop, request.PlotWidth, request.PlotHeight);
        var scale = YScale.For(request.Samples, request.PlotHeight);
        var items = new List<GraphPrimitive>();

        items.Add(new RectanglePrimitive(plot.Left, plot.Top, plot.Width, plot.Height, theme.PlotBackground));

        AddHeading(items, request, plot, marginTop);
        AddNoDataBands(items, request, plot);
        AddGrid(items, request, plot, scale);
        AddOutageMarkers(items, request, plot);

        if (scale.HasData)
        {
            AddSmoke(items, request, plot, scale);
            AddMedian(items, request, plot, scale);
        }
        else
        {
            // Rounds that were taken but answered by nothing are not the same as no
            // rounds at all: one is an outage, the other is a hole in the record.
            var measured = request.Samples.Any(sample => sample.Sent > 0);
            var message = measured ? "no probe was answered in this period" : "no data for this period";
            items.Add(new TextPrimitive(
                plot.Left + (plot.Width / 2.0),
                plot.Top + (plot.Height / 2.0),
                message,
                12,
                Bold: false,
                theme.MutedText,
                TextAnchor.Middle));
        }

        items.Add(new FramePrimitive(plot.Left, plot.Top, plot.Width, plot.Height, theme.Axis, 1));

        AddTimeAxis(items, request, plot);

        // Overview thumbnails squeeze hours into a column a pixel wide, where a
        // per-round tooltip is unusable - and a wall of them would dominate the file.
        if (!request.Compact)
        {
            AddHoverTargets(items, request, plot);
            AddLegend(items, request, plot);
        }

        return new GraphScene
        {
            Width = width,
            Height = height,
            Background = theme.Background,
            Primitives = items,
        };
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

    private static void AddHeading(List<GraphPrimitive> items, GraphRequest request, PlotArea plot, int marginTop)
    {
        var theme = request.Theme;

        if (request.Title.Length > 0)
        {
            items.Add(new TextPrimitive(
                plot.Left,
                request.Compact ? 12 : 15,
                request.Title,
                request.Compact ? 11 : 13,
                Bold: true,
                theme.Text));
        }

        if (HasSubtitle(request))
        {
            items.Add(new TextPrimitive(plot.Left, marginTop - 4, request.Subtitle, 10, Bold: false, theme.MutedText));
        }
    }

    /// <summary>Shades the periods where nothing was recorded, so gaps read as gaps.</summary>
    private static void AddNoDataBands(List<GraphPrimitive> items, GraphRequest request, PlotArea plot)
    {
        foreach (var sample in request.Samples)
        {
            if (sample.Sent > 0)
            {
                continue;
            }

            items.Add(new RectanglePrimitive(
                plot.XFor(sample.Timestamp, request),
                plot.Top,
                plot.WidthFor(request),
                plot.Height,
                request.Theme.NoData));
        }
    }

    /// <summary>
    /// Marks rounds where every probe was lost. There is no median to draw for those,
    /// so without this an outage looks exactly like a gap in the record.
    /// </summary>
    private static void AddOutageMarkers(List<GraphPrimitive> items, GraphRequest request, PlotArea plot)
    {
        var width = Math.Max(plot.WidthFor(request), 1.0);
        var height = Math.Min(5, plot.Height);

        foreach (var sample in request.Samples)
        {
            if (sample.Sent == 0 || sample.Lost < sample.Sent)
            {
                continue;
            }

            items.Add(new RectanglePrimitive(
                plot.XFor(sample.Timestamp, request),
                plot.Bottom - height,
                width,
                height,
                LossColours.TotalLossColour));
        }
    }

    private static void AddGrid(List<GraphPrimitive> items, GraphRequest request, PlotArea plot, YScale scale)
    {
        var theme = request.Theme;

        foreach (var tick in scale.Ticks)
        {
            var y = plot.Bottom - scale.ToPixels(tick);
            items.Add(new LinePrimitive(plot.Left, y, plot.Right, y, theme.Grid, 1));
            items.Add(new TextPrimitive(
                plot.Left - 6,
                y + 3.5,
                SmokeGraphRenderer.FormatMilliseconds(tick),
                10,
                Bold: false,
                theme.MutedText,
                TextAnchor.End));
        }

        if (!request.Compact)
        {
            items.Add(new TextPrimitive(
                12,
                plot.Top + (plot.Height / 2.0),
                "round trip time",
                10,
                Bold: false,
                theme.MutedText,
                TextAnchor.Middle,
                RotationDegrees: -90));
        }
    }

    /// <summary>
    /// Draws the smoke: nested bands between opposite quantiles, from the full
    /// min-max range on the outside to the innermost pair around the median. The grey
    /// ramp is upstream's - lighter for the outer, rarer values.
    /// </summary>
    private static void AddSmoke(List<GraphPrimitive> items, GraphRequest request, PlotArea plot, YScale scale)
    {
        var bands = Sample.QuantileCount / 2;
        var half = bands + 0.5;

        for (var band = 0; band < bands; band++)
        {
            var lower = band;
            var upper = Sample.QuantileCount - 1 - band;

            var level = (int)(190 / half * (half - (band + 1))) + 50;
            var colour = request.Theme == GraphTheme.Dark ? Grey(255 - level) : Grey(level);

            foreach (var run in ContiguousRuns(request.Samples, lower, upper))
            {
                var points = new List<ScenePoint>(run.Count * 2);

                foreach (var sample in run)
                {
                    var x = plot.XFor(sample.Timestamp, request) + (plot.WidthFor(request) / 2);
                    points.Add(new ScenePoint(x, plot.Bottom - scale.ToPixels(sample.Quantiles[upper])));
                }

                for (var i = run.Count - 1; i >= 0; i--)
                {
                    var x = plot.XFor(run[i].Timestamp, request) + (plot.WidthFor(request) / 2);
                    points.Add(new ScenePoint(x, plot.Bottom - scale.ToPixels(run[i].Quantiles[lower])));
                }

                items.Add(new PolygonPrimitive(points, colour, 0.85));
            }
        }
    }

    /// <summary>
    /// Draws the median as a line whose colour changes with the packet loss of each
    /// round, which is what makes loss visible without a second graph.
    /// </summary>
    private static void AddMedian(List<GraphPrimitive> items, GraphRequest request, PlotArea plot, YScale scale)
    {
        var half = plot.WidthFor(request) / 2;
        Sample? previous = null;

        foreach (var sample in request.Samples)
        {
            if (sample.Median is not { } median)
            {
                previous = null;
                continue;
            }

            var colour = LossColours.ForLoss(sample.Lost, sample.Sent == 0 ? request.Pings : sample.Sent);
            var x = plot.XFor(sample.Timestamp, request) + half;
            var y = plot.Bottom - scale.ToPixels(median);

            if (previous?.Median is { } previousMedian)
            {
                var px = plot.XFor(previous.Timestamp, request) + half;
                var py = plot.Bottom - scale.ToPixels(previousMedian);
                items.Add(new LinePrimitive(px, py, x, y, colour, 1.6));
            }
            else
            {
                items.Add(new CirclePrimitive(x, y, 1.1, colour));
            }

            previous = sample;
        }
    }

    private static void AddTimeAxis(List<GraphPrimitive> items, GraphRequest request, PlotArea plot)
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
            items.Add(new LinePrimitive(x, plot.Top, x, plot.Bottom, theme.Grid, 1));
            items.Add(new LinePrimitive(x, plot.Bottom, x, plot.Bottom + 4, theme.Axis, 1));

            var label = DateTimeOffset.FromUnixTimeSeconds(t)
                .ToOffset(request.UtcOffset)
                .ToString(format, CultureInfo.InvariantCulture);

            items.Add(new TextPrimitive(x, plot.Bottom + 15, label, 9, Bold: false, theme.MutedText, TextAnchor.Middle));
        }
    }

    /// <summary>Adds one hover region per sample.</summary>
    private static void AddHoverTargets(List<GraphPrimitive> items, GraphRequest request, PlotArea plot)
    {
        var width = Math.Max(plot.WidthFor(request), 1.0);

        foreach (var sample in request.Samples)
        {
            if (sample.Sent == 0)
            {
                continue;
            }

            var time = DateTimeOffset.FromUnixTimeSeconds(sample.Timestamp).ToOffset(request.UtcOffset);
            var median = sample.Median is { } m ? SmokeGraphRenderer.FormatMilliseconds(m) : "no response";
            var text = string.Create(
                CultureInfo.InvariantCulture,
                $"{time:yyyy-MM-dd HH:mm}  median {median}  loss {sample.Lost}/{sample.Sent}");

            items.Add(new TooltipPrimitive(plot.XFor(sample.Timestamp, request), plot.Top, width, plot.Height, text));
        }
    }

    /// <summary>Adds the loss colour key and the summary statistics for the period.</summary>
    private static void AddLegend(List<GraphPrimitive> items, GraphRequest request, PlotArea plot)
    {
        var theme = request.Theme;
        var top = plot.Bottom + AxisHeight + 12;

        items.Add(new TextPrimitive(plot.Left, top, "probes lost per round", 10, Bold: false, theme.MutedText));

        var x = (double)plot.Left;
        var y = top + 8;

        foreach (var band in LossColours.BuildScale(request.Pings))
        {
            items.Add(new RectanglePrimitive(x, y, 9, 9, band.Colour));
            items.Add(new TextPrimitive(x + 13, y + 8, band.Label, 9, Bold: false, theme.Text));
            x += 21 + (band.Label.Length * 5);
        }

        var stats = GraphStatistics.Compute(request.Samples);
        var summary = stats.HasData
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"median {SmokeGraphRenderer.FormatMilliseconds(stats.Median)}   " +
                $"min {SmokeGraphRenderer.FormatMilliseconds(stats.Minimum)}   " +
                $"max {SmokeGraphRenderer.FormatMilliseconds(stats.Maximum)}   " +
                $"sd {SmokeGraphRenderer.FormatMilliseconds(stats.StandardDeviation)}   " +
                $"loss {stats.LossPercent:F1}%")
            : "no data for this period";

        items.Add(new TextPrimitive(plot.Left, y + 26, summary, 10, Bold: false, theme.Text));
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

    private static string Grey(int level)
    {
        var component = level.ToString("x2", CultureInfo.InvariantCulture);
        return $"#{component}{component}{component}";
    }

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
