namespace SmokePing.Net.Graphing;

/// <summary>A point in graph coordinates.</summary>
public readonly record struct ScenePoint(double X, double Y);

/// <summary>Horizontal alignment of a text primitive relative to its anchor point.</summary>
public enum TextAnchor
{
    Start,
    Middle,
    End,
}

/// <summary>
/// One drawing instruction. A graph is laid out once into these and then handed to a
/// backend, so the SVG served over HTTP and the native window draw the same picture
/// from the same measurements rather than from two separate implementations.
/// </summary>
public abstract record GraphPrimitive;

/// <summary>A filled rectangle.</summary>
public sealed record RectanglePrimitive(double X, double Y, double Width, double Height, string Fill)
    : GraphPrimitive;

/// <summary>An outlined rectangle, used for the plot frame.</summary>
public sealed record FramePrimitive(double X, double Y, double Width, double Height, string Stroke, double StrokeWidth)
    : GraphPrimitive;

/// <summary>A filled polygon; the smoke bands are drawn with these.</summary>
public sealed record PolygonPrimitive(IReadOnlyList<ScenePoint> Points, string Fill, double Opacity)
    : GraphPrimitive;

public sealed record LinePrimitive(double X1, double Y1, double X2, double Y2, string Stroke, double StrokeWidth)
    : GraphPrimitive;

public sealed record CirclePrimitive(double CentreX, double CentreY, double Radius, string Fill)
    : GraphPrimitive;

/// <summary>Text drawn at a point, optionally rotated about that point.</summary>
public sealed record TextPrimitive(
    double X,
    double Y,
    string Text,
    double FontSize,
    bool Bold,
    string Fill,
    TextAnchor Anchor = TextAnchor.Start,
    double RotationDegrees = 0)
    : GraphPrimitive;

/// <summary>
/// An invisible region carrying hover text. The SVG backend turns it into a
/// &lt;title&gt;; the native control hit-tests it on mouse move.
/// </summary>
public sealed record TooltipPrimitive(double X, double Y, double Width, double Height, string Text)
    : GraphPrimitive;

/// <summary>A laid-out graph: a size, a background and the primitives to draw.</summary>
public sealed class GraphScene
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required string Background { get; init; }

    public required IReadOnlyList<GraphPrimitive> Primitives { get; init; }

    /// <summary>Hover regions, in the order they should be hit-tested.</summary>
    public IEnumerable<TooltipPrimitive> Tooltips => Primitives.OfType<TooltipPrimitive>();
}
