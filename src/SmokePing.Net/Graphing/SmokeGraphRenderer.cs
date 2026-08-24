using System.Globalization;

namespace SmokePing.Net.Graphing;

/// <summary>Everything needed to draw one graph.</summary>
public sealed class GraphRequest
{
    public required IReadOnlyList<Storage.Sample> Samples { get; init; }

    /// <summary>Probes per round; decides the loss colour scale.</summary>
    public required int Pings { get; init; }

    public required long FromTimestamp { get; init; }

    public required long ToTimestamp { get; init; }

    /// <summary>Seconds covered by one sample, used to size the columns.</summary>
    public required int StepSeconds { get; init; }

    public required string Title { get; init; }

    /// <summary>Probe description, printed on the legend's "probe:" row.</summary>
    public string Subtitle { get; init; } = string.Empty;

    public int PlotWidth { get; init; } = 600;

    public int PlotHeight { get; init; } = 200;

    /// <summary>Small graphs drop the legend, the hover regions and the axis labels.</summary>
    public bool Compact { get; init; }

    public GraphTheme Theme { get; init; } = GraphTheme.Light;

    /// <summary>
    /// Shades rounds that lost probes across the full height of the plot. Upstream
    /// calls this loss_background and leaves it off; it is on here because loss is
    /// the main thing these graphs exist to show.
    /// </summary>
    public bool ShowLossBackground { get; init; } = true;

    /// <summary>Offset applied to timestamps before formatting the time axis.</summary>
    public TimeSpan UtcOffset { get; init; } = TimeSpan.Zero;
}

/// <summary>
/// Renders a SmokePing graph to SVG. Layout lives in <see cref="SmokeGraphLayout"/>
/// and is shared with the native renderer in the desktop client, so both draw the
/// same picture from the same measurements.
/// </summary>
public static class SmokeGraphRenderer
{
    public static string Render(GraphRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scene = SmokeGraphLayout.Build(request);
        var documentTitle = request.Title.Length > 0 ? request.Title : "SmokePing graph";
        return SvgSceneWriter.Write(scene, documentTitle);
    }

    /// <summary>Formats a duration the way SmokePing labels its axes: unit-scaled, never noisy.</summary>
    public static string FormatMilliseconds(double milliseconds) => milliseconds switch
    {
        < 1 => string.Create(CultureInfo.InvariantCulture, $"{milliseconds * 1000:F0} us"),
        < 10 => string.Create(CultureInfo.InvariantCulture, $"{milliseconds:F2} ms"),
        < 1000 => string.Create(CultureInfo.InvariantCulture, $"{milliseconds:F1} ms"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{milliseconds / 1000:F2} s"),
    };
}
