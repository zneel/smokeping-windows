using System.Globalization;
using SmokePing.Net.Storage;

namespace SmokePing.Net.Graphing;

/// <summary>
/// Linear value axis for a graph: works out a sensible upper bound from the data and
/// produces round tick values.
/// </summary>
public sealed class YScale
{
    private readonly double _maximum;
    private readonly int _pixels;

    private YScale(double maximum, int pixels, IReadOnlyList<float> ticks, bool hasData)
    {
        _maximum = maximum;
        _pixels = pixels;
        Ticks = ticks;
        HasData = hasData;
    }

    public IReadOnlyList<float> Ticks { get; }

    /// <summary>False when no round in the period produced a measurement.</summary>
    public bool HasData { get; }

    public static YScale For(IReadOnlyList<Sample> samples, int pixels)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var peak = 0.0;
        var hasData = false;

        foreach (var sample in samples)
        {
            // The top quantile is the slowest probe of the round: the graph must fit it.
            var top = sample.Quantiles[Sample.QuantileCount - 1];
            if (!float.IsNaN(top))
            {
                hasData = true;
                peak = Math.Max(peak, top);
            }
        }

        // Leave headroom so the smoke never touches the frame, and keep a sane floor
        // for targets that answer in well under a millisecond.
        var maximum = hasData ? NiceCeiling(peak * 1.1) : 10.0;
        if (maximum <= 0)
        {
            maximum = 1.0;
        }

        return new YScale(maximum, pixels, BuildTicks(maximum), hasData);
    }

    /// <summary>Converts a value in milliseconds to a height in pixels above the axis.</summary>
    public double ToPixels(double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        return Math.Clamp(value / _maximum, 0, 1) * _pixels;
    }

    /// <summary>Rounds up to 1, 2, 2.5 or 5 times a power of ten.</summary>
    private static double NiceCeiling(double value)
    {
        if (value <= 0)
        {
            return 1;
        }

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalised = value / magnitude;

        var nice = normalised switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 2.5 => 2.5,
            <= 5 => 5,
            _ => 10,
        };

        return nice * magnitude;
    }

    private static IReadOnlyList<float> BuildTicks(double maximum)
    {
        const int TargetTicks = 5;
        var step = NiceCeiling(maximum / TargetTicks);
        var ticks = new List<float>();

        for (var value = step; value <= maximum + (step / 1000); value += step)
        {
            ticks.Add((float)value);
        }

        return ticks;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"0 - {_maximum} ms over {_pixels} px");
}
