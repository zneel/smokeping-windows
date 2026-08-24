using System.ComponentModel;
using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using SmokePing.Net.Graphing;
using SmokePing.Net.Storage;

namespace SmokePing.Net.Desktop;

/// <summary>
/// A control that draws one SmokePing graph natively. It lays the graph out to its
/// own client size, so the picture is drawn at the window's resolution rather than
/// being scaled, and hit-tests the layout's hover regions to show per-round detail.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GraphControl : Control
{
    private readonly ToolTip _toolTip = new() { InitialDelay = 200, ReshowDelay = 100 };

    private GraphScene? _scene;
    private IReadOnlyList<Sample> _samples = [];
    private TooltipPrimitive? _hovered;
    private string _message = "No data loaded.";

    public GraphControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw,
            true);

        Theme = GraphTheme.Dark;
    }

    /// <summary>Probes per round, which decides the loss colour scale.</summary>
    [Browsable(false)]
    public int Pings { get; private set; } = 20;

    [Browsable(false)]
    public long FromTimestamp { get; private set; }

    [Browsable(false)]
    public long ToTimestamp { get; private set; }

    [Browsable(false)]
    public int StepSeconds { get; private set; } = 300;

    [Browsable(false)]
    public string GraphTitle { get; set; } = string.Empty;

    [Browsable(false)]
    public string GraphSubtitle { get; set; } = string.Empty;

    [Browsable(false)]
    public GraphTheme Theme { get; set; }

    /// <summary>Drops the legend and the hover regions, for thumbnail-sized graphs.</summary>
    [Browsable(false)]
    public bool CompactLayout { get; set; }

    /// <summary>Replaces the data shown and repaints.</summary>
    public void SetData(TargetDataDto data)
    {
        ArgumentNullException.ThrowIfNull(data);

        _samples = data.Samples.Select(s => s.ToSample()).ToList();
        Pings = Math.Max(data.Pings, 1);
        FromTimestamp = data.From;
        ToTimestamp = data.To;
        StepSeconds = Math.Max(data.StepSeconds, 1);
        _message = string.Empty;

        Rebuild();
    }

    /// <summary>Shows an explanatory message instead of a graph.</summary>
    public void SetMessage(string message)
    {
        _message = message;
        _samples = [];
        _scene = null;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Rebuild();
    }

    private void Rebuild()
    {
        if (_samples.Count == 0 || ClientSize.Width < 40 || ClientSize.Height < 30)
        {
            _scene = null;
            Invalidate();
            return;
        }

        // Ask the layout how much room its chrome needs, then give it the rest.
        var probe = BuildRequest(1, 1);
        var (horizontal, vertical) = SmokeGraphLayout.ChromeFor(probe);

        var plotWidth = Math.Max(ClientSize.Width - horizontal, 60);
        var plotHeight = Math.Max(ClientSize.Height - vertical, 30);

        _scene = SmokeGraphLayout.Build(BuildRequest(plotWidth, plotHeight));
        _hovered = null;
        Invalidate();
    }

    private GraphRequest BuildRequest(int plotWidth, int plotHeight) => new()
    {
        Samples = _samples,
        Pings = Pings,
        FromTimestamp = FromTimestamp,
        ToTimestamp = ToTimestamp,
        StepSeconds = StepSeconds,
        Title = GraphTitle,
        Subtitle = GraphSubtitle,
        PlotWidth = plotWidth,
        PlotHeight = plotHeight,
        Compact = CompactLayout,
        Theme = Theme,
        UtcOffset = TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.Now),
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_scene is { } scene)
        {
            GdiSceneRenderer.Draw(scene, e.Graphics);
            return;
        }

        using var background = new SolidBrush(ColorTranslator.FromHtml(Theme.Background));
        e.Graphics.FillRectangle(background, ClientRectangle);

        if (_message.Length == 0)
        {
            return;
        }

        using var brush = new SolidBrush(ColorTranslator.FromHtml(Theme.MutedText));
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        e.Graphics.DrawString(_message, Font, brush, ClientRectangle, format);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        ArgumentNullException.ThrowIfNull(e);

        if (_scene is not { } scene)
        {
            return;
        }

        var hit = scene.Tooltips.FirstOrDefault(t =>
            e.X >= t.X && e.X <= t.X + t.Width &&
            e.Y >= t.Y && e.Y <= t.Y + t.Height);

        if (ReferenceEquals(hit, _hovered))
        {
            return;
        }

        _hovered = hit;
        if (hit is null)
        {
            _toolTip.Hide(this);
        }
        else
        {
            _toolTip.SetToolTip(this, hit.Text);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = null;
        _toolTip.Hide(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }
}
