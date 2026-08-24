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

    private static string DisplayRange(int from, int to, int pings) =>
        from == to
            ? string.Create(CultureInfo.InvariantCulture, $"{from}/{pings}")
            : string.Create(CultureInfo.InvariantCulture, $"{from}-{to}/{pings}");
}
