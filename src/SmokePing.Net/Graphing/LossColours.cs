using System.Globalization;

namespace SmokePing.Net.Graphing;

/// <summary>One band of the loss colour legend.</summary>
/// <param name="MaxLost">Highest number of lost probes still in this band.</param>
/// <param name="Label">Legend text, e.g. "1/20".</param>
/// <param name="Colour">Web colour used for the median line.</param>
public readonly record struct LossBand(int MaxLost, string Label, string Colour);

/// <summary>
/// The loss colour scale. The thresholds and colours are the ones SmokePing has
/// always used, so a graph reads the same way as the original: green for a clean
/// round, through blue and purple, to dark red for total loss.
/// </summary>
public static class LossColours
{
    /// <summary>Colour of a round that lost nothing.</summary>
    public const string CleanColour = "#26ff00";

    /// <summary>Colour of a round in which every probe was lost.</summary>
    public const string TotalLossColour = "#a00000";

    /// <summary>
    /// Loss thresholds for a round of <paramref name="pings"/> probes, ascending.
    /// Each threshold is a percentage of the round with a floor, so small rounds still
    /// produce usable bands. Where two thresholds collapse onto the same count the
    /// later one wins, which keeps total loss dark red however short the round is.
    /// </summary>
    private static SortedDictionary<int, string> Thresholds(int pings)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pings, 1);

        var candidates = new (int Limit, string Colour)[]
        {
            (Math.Max((int)(0.01 * pings), 1), "#00b8ff"),
            (Math.Max((int)(0.05 * pings), 2), "#0059ff"),
            (Math.Max((int)(0.10 * pings), 3), "#7e00ff"),
            (Math.Max((int)(0.25 * pings), 4), "#ff00ff"),
            (Math.Max((int)(0.50 * pings), 5), "#ff5500"),
            (pings - 1, "#ff0000"),
            (pings, "#a00000"),
        };

        var thresholds = new SortedDictionary<int, string>();
        foreach (var (limit, colour) in candidates)
        {
            if (limit >= 1 && limit <= pings)
            {
                thresholds[limit] = colour;
            }
        }

        return thresholds;
    }

    /// <summary>Builds the legend shown under a graph.</summary>
    public static IReadOnlyList<LossBand> BuildScale(int pings)
    {
        var bands = new List<LossBand> { new(0, "0", CleanColour) };
        var previousLimit = 0;

        foreach (var (limit, colour) in Thresholds(pings))
        {
            bands.Add(new LossBand(limit, DisplayRange(previousLimit + 1, limit, pings), colour));
            previousLimit = limit;
        }

        return bands;
    }

    /// <summary>
    /// Colour for a loss fraction, scaled to the configured round size. Consolidated
    /// buckets cover several rounds, so the fraction is the portable quantity: the
    /// scale itself is always the one the legend prints.
    /// </summary>
    public static string ForLossFraction(double lossFraction, int pings) =>
        ForLoss((int)Math.Round(Math.Clamp(lossFraction, 0, 1) * pings), pings);

    /// <summary>Colour for a round in which <paramref name="lost"/> of <paramref name="pings"/> probes were lost.</summary>
    public static string ForLoss(int lost, int pings)
    {
        if (lost <= 0)
        {
            return CleanColour;
        }

        foreach (var (limit, colour) in Thresholds(pings))
        {
            if (lost <= limit)
            {
                return colour;
            }
        }

        return TotalLossColour;
    }

    /// <summary>
    /// The washed-out version of a loss colour, used to shade the background of
    /// periods that lost probes. This is upstream's transform: convert to HSL, move
    /// the lightness two thirds of the way to white, and convert back.
    /// </summary>
    public static string ToBackground(string colour)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(colour);

        var rgb = Convert.ToInt32(colour.TrimStart('#'), 16);
        var r = ((rgb >> 16) & 0xFF) / 255.0;
        var g = ((rgb >> 8) & 0xFF) / 255.0;
        var b = (rgb & 0xFF) / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2;

        double hue = 0;
        double saturation = 0;

        if (max > min)
        {
            var delta = max - min;
            saturation = lightness > 0.5 ? delta / (2 - max - min) : delta / (max + min);

            if (max == r)
            {
                hue = ((g - b) / delta) + (g < b ? 6 : 0);
            }
            else if (max == g)
            {
                hue = ((b - r) / delta) + 2;
            }
            else
            {
                hue = ((r - g) / delta) + 4;
            }

            hue /= 6;
        }

        lightness = ((1 - lightness) * (2.0 / 3.0)) + lightness;

        var (nr, ng, nb) = HslToRgb(hue, saturation, lightness);
        return $"#{nr:x2}{ng:x2}{nb:x2}";
    }

    private static (int R, int G, int B) HslToRgb(double hue, double saturation, double lightness)
    {
        if (saturation == 0)
        {
            var grey = (int)(lightness * 255);
            return (grey, grey, grey);
        }

        var q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - (lightness * saturation);
        var p = (2 * lightness) - q;

        // Truncated, not rounded: the original formats with sprintf "%.2x", and
        // rounding shifts several palette entries by one unit per channel.
        return (
            (int)(HueToChannel(p, q, hue + (1.0 / 3.0)) * 255),
            (int)(HueToChannel(p, q, hue) * 255),
            (int)(HueToChannel(p, q, hue - (1.0 / 3.0)) * 255));
    }

    private static double HueToChannel(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        if (t < 1.0 / 6.0)
        {
            return p + ((q - p) * 6 * t);
        }

        if (t < 1.0 / 2.0)
        {
            return q;
        }

        if (t < 2.0 / 3.0)
        {
            return p + ((q - p) * ((2.0 / 3.0) - t) * 6);
        }

        return p;
    }

    /// <summary>
    /// Labels a band the way the original does: bare numbers, with only the
    /// everything-was-lost band spelled out as "N/N".
    /// </summary>
    private static string DisplayRange(int from, int to, int pings)
    {
        if (to >= pings)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{pings}/{pings}");
        }

        return from == to
            ? from.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{from}-{to}");
    }
}
