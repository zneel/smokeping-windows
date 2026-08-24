using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.Versioning;
using SmokePing.Net.Graphing;

namespace SmokePing.Net.Desktop;

/// <summary>
/// Draws a laid-out graph with GDI+.
///
/// This is the native counterpart to the SVG writer. Both consume the same
/// <see cref="GraphScene"/>, so the desktop client and the web interface cannot
/// drift apart: only the drawing calls differ, never the layout.
/// </summary>
[SupportedOSPlatform("windows")]
public static class GdiSceneRenderer
{
    private const string FontFamily = "Segoe UI";

    public static void Draw(GraphScene scene, Graphics graphics)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(graphics);

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using (var background = new SolidBrush(Parse(scene.Background)))
        {
            graphics.FillRectangle(background, 0, 0, scene.Width, scene.Height);
        }

        foreach (var primitive in scene.Primitives)
        {
            DrawPrimitive(primitive, graphics);
        }
    }

    private static void DrawPrimitive(GraphPrimitive primitive, Graphics graphics)
    {
        switch (primitive)
        {
            case RectanglePrimitive r:
            {
                using var brush = new SolidBrush(Parse(r.Fill));
                graphics.FillRectangle(brush, (float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
                break;
            }

            case FramePrimitive f:
            {
                using var pen = new Pen(Parse(f.Stroke), (float)f.StrokeWidth);
                graphics.DrawRectangle(pen, (float)f.X, (float)f.Y, (float)f.Width, (float)f.Height);
                break;
            }

            case PolygonPrimitive p:
            {
                if (p.Points.Count < 3)
                {
                    break;
                }

                var points = new PointF[p.Points.Count];
                for (var i = 0; i < p.Points.Count; i++)
                {
                    points[i] = new PointF((float)p.Points[i].X, (float)p.Points[i].Y);
                }

                var colour = Parse(p.Fill);
                using var brush = new SolidBrush(Color.FromArgb(
                    (int)Math.Round(Math.Clamp(p.Opacity, 0, 1) * 255),
                    colour));

                graphics.FillPolygon(brush, points);
                break;
            }

            case LinePrimitive l:
            {
                using var pen = new Pen(Parse(l.Stroke), (float)l.StrokeWidth)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                };

                graphics.DrawLine(pen, (float)l.X1, (float)l.Y1, (float)l.X2, (float)l.Y2);
                break;
            }

            case CirclePrimitive c:
            {
                using var brush = new SolidBrush(Parse(c.Fill));
                graphics.FillEllipse(
                    brush,
                    (float)(c.CentreX - c.Radius),
                    (float)(c.CentreY - c.Radius),
                    (float)(c.Radius * 2),
                    (float)(c.Radius * 2));
                break;
            }

            case TextPrimitive t:
                DrawText(t, graphics);
                break;

            case TooltipPrimitive:
                // Hover regions are hit-tested by the control, never painted.
                break;

            default:
                // Mirrors the SVG writer: a primitive added to the layout must be
                // handled here too, rather than silently vanishing from the native view.
                throw new NotSupportedException($"Unknown primitive {primitive.GetType().Name}.");
        }
    }

    private static void DrawText(TextPrimitive text, Graphics graphics)
    {
        // SVG font sizes are in pixels while GDI+ defaults to points, so the font is
        // created with an explicit pixel unit to keep both backends the same size.
        using var font = new Font(
            FontFamily,
            (float)text.FontSize,
            text.Bold ? FontStyle.Bold : FontStyle.Regular,
            GraphicsUnit.Pixel);

        using var brush = new SolidBrush(Parse(text.Fill));

        var format = text.Anchor switch
        {
            TextAnchor.Middle => StringAlignment.Center,
            TextAnchor.End => StringAlignment.Far,
            _ => StringAlignment.Near,
        };

        using var stringFormat = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = format,
            LineAlignment = StringAlignment.Near,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };

        var state = graphics.Save();
        try
        {
            graphics.TranslateTransform((float)text.X, (float)text.Y);
            if (text.RotationDegrees != 0)
            {
                graphics.RotateTransform((float)text.RotationDegrees);
            }

            // An SVG y coordinate is the text baseline; GDI+ draws from the top edge.
            var ascent = font.FontFamily.GetCellAscent(font.Style);
            var lineSpacing = font.FontFamily.GetLineSpacing(font.Style);
            var baselineOffset = font.GetHeight(graphics) * ascent / lineSpacing;

            graphics.DrawString(text.Text, font, brush, new PointF(0, -baselineOffset), stringFormat);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    /// <summary>Parses the "#rrggbb" colours the layout produces.</summary>
    private static Color Parse(string colour) => ColorTranslator.FromHtml(colour);
}
