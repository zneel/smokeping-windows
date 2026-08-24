using System.Globalization;
using System.Net;
using System.Text;

namespace SmokePing.Net.Graphing;

/// <summary>Renders a laid-out graph as standalone SVG.</summary>
public static class SvgSceneWriter
{
    public static string Write(GraphScene scene, string documentTitle)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var svg = new StringBuilder(16 * 1024);
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {scene.Width} {scene.Height}\" width=\"{scene.Width}\" height=\"{scene.Height}\" font-family=\"'Segoe UI',system-ui,sans-serif\" role=\"img\">");
        svg.Append(CultureInfo.InvariantCulture, $"<title>{Escape(documentTitle)}</title>");
        svg.Append(CultureInfo.InvariantCulture, $"<rect width=\"{scene.Width}\" height=\"{scene.Height}\" fill=\"{scene.Background}\"/>");

        var inTooltipGroup = false;

        foreach (var primitive in scene.Primitives)
        {
            // Hover regions all share one fill, and there is one per sample, so
            // hoisting it onto a group keeps long graphs from bloating.
            if (primitive is TooltipPrimitive && !inTooltipGroup)
            {
                svg.Append("<g fill=\"transparent\">");
                inTooltipGroup = true;
            }
            else if (primitive is not TooltipPrimitive && inTooltipGroup)
            {
                svg.Append("</g>");
                inTooltipGroup = false;
            }

            Append(svg, primitive);
        }

        if (inTooltipGroup)
        {
            svg.Append("</g>");
        }

        svg.Append("</svg>");
        return svg.ToString();
    }

    private static void Append(StringBuilder svg, GraphPrimitive primitive)
    {
        switch (primitive)
        {
            case RectanglePrimitive r:
                svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{F(r.X)}\" y=\"{F(r.Y)}\" width=\"{F(r.Width)}\" height=\"{F(r.Height)}\" fill=\"{r.Fill}\"/>");
                break;

            case FramePrimitive f:
                svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{F(f.X)}\" y=\"{F(f.Y)}\" width=\"{F(f.Width)}\" height=\"{F(f.Height)}\" fill=\"none\" stroke=\"{f.Stroke}\" stroke-width=\"{F(f.StrokeWidth)}\"/>");
                break;

            case PolygonPrimitive p:
            {
                var points = new StringBuilder();
                foreach (var point in p.Points)
                {
                    points.Append(CultureInfo.InvariantCulture, $"{F(point.X)},{F(point.Y)} ");
                }

                svg.Append(CultureInfo.InvariantCulture, $"<polygon points=\"{points.ToString().TrimEnd()}\" fill=\"{p.Fill}\" fill-opacity=\"{F(p.Opacity)}\" stroke=\"none\"/>");
                break;
            }

            case LinePrimitive l:
                svg.Append(CultureInfo.InvariantCulture, $"<line x1=\"{F(l.X1)}\" y1=\"{F(l.Y1)}\" x2=\"{F(l.X2)}\" y2=\"{F(l.Y2)}\" stroke=\"{l.Stroke}\" stroke-width=\"{F(l.StrokeWidth)}\"/>");
                break;

            case CirclePrimitive c:
                svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{F(c.CentreX)}\" cy=\"{F(c.CentreY)}\" r=\"{F(c.Radius)}\" fill=\"{c.Fill}\"/>");
                break;

            case TextPrimitive t:
            {
                var anchor = t.Anchor switch
                {
                    TextAnchor.Middle => " text-anchor=\"middle\"",
                    TextAnchor.End => " text-anchor=\"end\"",
                    _ => string.Empty,
                };

                var weight = t.Bold ? " font-weight=\"600\"" : string.Empty;
                var rotation = t.RotationDegrees == 0
                    ? string.Empty
                    : string.Create(CultureInfo.InvariantCulture, $" transform=\"rotate({F(t.RotationDegrees)} {F(t.X)} {F(t.Y)})\"");

                svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{F(t.X)}\" y=\"{F(t.Y)}\" font-size=\"{F(t.FontSize)}\" fill=\"{t.Fill}\"{anchor}{weight}{rotation}>{Escape(t.Text)}</text>");
                break;
            }

            case TooltipPrimitive h:
                svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{F(h.X)}\" y=\"{F(h.Y)}\" width=\"{F(h.Width)}\" height=\"{F(h.Height)}\"><title>{Escape(h.Text)}</title></rect>");
                break;

            default:
                throw new NotSupportedException($"Unknown primitive {primitive.GetType().Name}.");
        }
    }

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Escape(string value) => WebUtility.HtmlEncode(value);
}
