using SmokePing.Net.Configuration;

namespace SmokePing.Net.Storage;

/// <summary>One stretch of time the link misbehaved.</summary>
/// <param name="Start">Unix seconds of the first bad reading.</param>
/// <param name="End">Unix seconds just past the last bad reading.</param>
/// <param name="PeakRoundTrip">Slowest answer within it, NaN when nothing came back.</param>
/// <param name="PeakJitter">Largest variation within it, NaN when unknown.</param>
/// <param name="Sent">Probes the stretch covers.</param>
/// <param name="Lost">How many went unanswered.</param>
/// <param name="Kinds">Which of latency, jitter and loss triggered it.</param>
/// <param name="Severity">How many times over threshold the worst reading was.</param>
public readonly record struct TraceEvent(
    long Start,
    long End,
    float PeakRoundTrip,
    float PeakJitter,
    int Sent,
    int Lost,
    IReadOnlyList<string> Kinds,
    double Severity)
{
    public int DurationSeconds => (int)(End - Start);
}

/// <summary>The lines a reading has to cross to count as a peak, for one window.</summary>
/// <param name="Baseline">The window's typical round-trip time in milliseconds.</param>
/// <param name="RoundTripMs">Above this, latency counts as a peak.</param>
/// <param name="JitterBaseline">The window's typical jitter in milliseconds.</param>
/// <param name="JitterMs">Above this, jitter counts as a peak.</param>
public readonly record struct TraceThresholds(
    double Baseline,
    double RoundTripMs,
    double JitterBaseline,
    double JitterMs);

/// <summary>What a window of trace amounted to.</summary>
public readonly record struct TraceSummary(
    int Sent,
    int Lost,
    double LossPercent,
    double MedianRoundTrip,
    double NinetyFifthRoundTrip,
    double MaximumRoundTrip,
    double MedianJitter,
    double MaximumJitter,
    int EventCount);

/// <summary>
/// Reads a window of recorded trace and says where the trouble was.
///
/// Two things matter here and both are about not losing the spike. Thresholds come
/// from the window's own behaviour, because a reading that means trouble on a 4 ms
/// LAN is unremarkable on a 90 ms transatlantic hop, and a fixed limit would either
/// cry wolf on one or stay silent on the other. And every reduction keeps maxima:
/// a window has far more readings than a chart has columns, and averaging them
/// together would smooth away the one bad second that the whole exercise is about.
/// </summary>
public static class TraceAnalysis
{
    /// <summary>
    /// Bad readings this far apart still count as one disturbance. Without it a hiccup
    /// that flickers reads as a dozen separate events a second apart.
    /// </summary>
    private const int BridgeSamples = 2;

    /// <summary>
    /// Scale factor turning a median absolute deviation into something comparable with
    /// a standard deviation for normally distributed data, so "deviations" in the
    /// configuration means roughly what a reader expects it to.
    /// </summary>
    private const double MadToSigma = 1.4826;

    /// <summary>Works out where the lines sit for this particular window.</summary>
    public static TraceThresholds ComputeThresholds(IReadOnlyList<TraceSample> samples, SpikeConfig config)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(config);

        var latencies = samples.Where(s => s.HasAnswer).Select(s => (double)s.Mean).ToArray();
        var jitters = samples.Where(s => !float.IsNaN(s.Jitter)).Select(s => (double)s.Jitter).ToArray();

        var baseline = Median(latencies);
        var jitterBaseline = Median(jitters);

        // A floor proportional to the baseline as well as an absolute one: on a slow
        // link an extra 10 ms is noise, and on a fast one a 25% jump is not.
        var latencyMargin = Math.Max(
            config.Deviations * MadToSigma * MedianAbsoluteDeviation(latencies, baseline),
            Math.Max(config.LatencyFloorMs, baseline * 0.25));

        var jitterMargin = Math.Max(
            config.Deviations * MadToSigma * MedianAbsoluteDeviation(jitters, jitterBaseline),
            Math.Max(config.JitterFloorMs, jitterBaseline * 0.5));

        return new TraceThresholds(
            baseline,
            baseline + latencyMargin,
            jitterBaseline,
            jitterBaseline + jitterMargin);
    }

    /// <summary>
    /// Groups the readings that crossed a line into events, newest first, keeping the
    /// worst <paramref name="maximumEvents"/> when there are more than that.
    /// </summary>
    public static IReadOnlyList<TraceEvent> FindEvents(
        IReadOnlyList<TraceSample> samples,
        int stepSeconds,
        TraceThresholds thresholds,
        int maximumEvents = 200)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfLessThan(stepSeconds, 1);

        var events = new List<TraceEvent>();
        var run = new List<TraceSample>();
        var quiet = 0;

        void Close()
        {
            if (run.Count == 0)
            {
                return;
            }

            // Trailing quiet readings were only kept to bridge a flicker; they are not
            // part of the disturbance and should not stretch its reported duration.
            while (run.Count > 0 && !IsHot(run[^1], thresholds))
            {
                run.RemoveAt(run.Count - 1);
            }

            if (run.Count > 0)
            {
                events.Add(Build(run, stepSeconds, thresholds));
            }

            run.Clear();
        }

        foreach (var sample in samples)
        {
            if (!sample.HasData)
            {
                // A gap is missing information, not a problem: the recorder was not
                // running. Ending the run here stops it being bridged across a restart.
                Close();
                quiet = 0;
                continue;
            }

            if (IsHot(sample, thresholds))
            {
                run.Add(sample);
                quiet = 0;
                continue;
            }

            if (run.Count == 0)
            {
                continue;
            }

            if (++quiet > BridgeSamples)
            {
                Close();
                quiet = 0;
            }
            else
            {
                run.Add(sample);
            }
        }

        Close();

        if (events.Count > maximumEvents)
        {
            events = [.. events.OrderByDescending(e => e.Severity).Take(maximumEvents)];
        }

        events.Sort((a, b) => b.Start.CompareTo(a.Start));
        return events;
    }

    /// <summary>Totals a window, for the readout above the chart.</summary>
    public static TraceSummary Summarise(IReadOnlyList<TraceSample> samples, int eventCount)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var latencies = samples.Where(s => s.HasAnswer).Select(s => (double)s.Mean).ToArray();
        var jitters = samples.Where(s => !float.IsNaN(s.Jitter)).Select(s => (double)s.Jitter).ToArray();
        var sent = samples.Sum(s => s.Sent);
        var lost = samples.Sum(s => s.Lost);

        var peaks = samples.Where(s => !float.IsNaN(s.Maximum)).Select(s => (double)s.Maximum).ToArray();
        var jitterPeaks = samples.Where(s => !float.IsNaN(s.JitterMaximum))
            .Select(s => (double)s.JitterMaximum).ToArray();

        return new TraceSummary(
            sent,
            lost,
            sent == 0 ? 0 : lost * 100.0 / sent,
            Median(latencies),
            Percentile(latencies, 0.95),
            peaks.Length == 0 ? double.NaN : peaks.Max(),
            Median(jitters),
            jitterPeaks.Length == 0 ? double.NaN : jitterPeaks.Max(),
            eventCount);
    }

    /// <summary>
    /// Reduces a series to at most <paramref name="maximumPoints"/> columns for
    /// drawing, keeping the extremes of each column rather than averaging them, so a
    /// single bad reading still reaches the top of the chart.
    /// </summary>
    public static IReadOnlyList<TraceSample> Downsample(IReadOnlyList<TraceSample> samples, int maximumPoints)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPoints, 1);

        if (samples.Count <= maximumPoints)
        {
            return samples;
        }

        var perColumn = (int)Math.Ceiling((double)samples.Count / maximumPoints);
        var result = new List<TraceSample>((samples.Count / perColumn) + 1);

        for (var start = 0; start < samples.Count; start += perColumn)
        {
            var end = Math.Min(start + perColumn, samples.Count);
            var sent = 0;
            var lost = 0;
            var min = float.NaN;
            var max = float.NaN;
            var jitterMax = float.NaN;
            var sum = 0.0;
            var count = 0;
            var jitterSum = 0.0;
            var jitterCount = 0;

            for (var i = start; i < end; i++)
            {
                var s = samples[i];
                sent += s.Sent;
                lost += s.Lost;

                if (!float.IsNaN(s.Minimum))
                {
                    min = float.IsNaN(min) ? s.Minimum : Math.Min(min, s.Minimum);
                }

                if (!float.IsNaN(s.Maximum))
                {
                    max = float.IsNaN(max) ? s.Maximum : Math.Max(max, s.Maximum);
                }

                if (!float.IsNaN(s.Mean))
                {
                    sum += s.Mean;
                    count++;
                }

                if (!float.IsNaN(s.Jitter))
                {
                    jitterSum += s.Jitter;
                    jitterCount++;
                }

                if (!float.IsNaN(s.JitterMaximum))
                {
                    jitterMax = float.IsNaN(jitterMax) ? s.JitterMaximum : Math.Max(jitterMax, s.JitterMaximum);
                }
            }

            result.Add(new TraceSample(
                samples[start].Timestamp,
                sent,
                lost,
                min,
                count == 0 ? float.NaN : (float)(sum / count),
                max,
                jitterCount == 0 ? float.NaN : (float)(jitterSum / jitterCount),
                jitterMax));
        }

        return result;
    }

    private static bool IsHot(TraceSample sample, TraceThresholds thresholds) =>
        sample.Lost > 0 ||
        (!float.IsNaN(sample.Maximum) && sample.Maximum > thresholds.RoundTripMs) ||
        (!float.IsNaN(sample.JitterMaximum) && sample.JitterMaximum > thresholds.JitterMs);

    private static TraceEvent Build(List<TraceSample> run, int stepSeconds, TraceThresholds thresholds)
    {
        var kinds = new List<string>(3);
        var peakRtt = float.NaN;
        var peakJitter = float.NaN;
        var sent = 0;
        var lost = 0;

        foreach (var sample in run)
        {
            sent += sample.Sent;
            lost += sample.Lost;

            if (!float.IsNaN(sample.Maximum) && (float.IsNaN(peakRtt) || sample.Maximum > peakRtt))
            {
                peakRtt = sample.Maximum;
            }

            if (!float.IsNaN(sample.JitterMaximum) && (float.IsNaN(peakJitter) || sample.JitterMaximum > peakJitter))
            {
                peakJitter = sample.JitterMaximum;
            }
        }

        if (!float.IsNaN(peakRtt) && peakRtt > thresholds.RoundTripMs)
        {
            kinds.Add("latency");
        }

        if (!float.IsNaN(peakJitter) && peakJitter > thresholds.JitterMs)
        {
            kinds.Add("jitter");
        }

        if (lost > 0)
        {
            kinds.Add("loss");
        }

        // How many times over the line the worst reading was, with each unanswered
        // probe counting as a whole multiple of its own: for anything interactive a
        // dropped packet hurts more than a slow one.
        double severity = lost;
        if (kinds.Contains("latency") && thresholds.RoundTripMs > 0)
        {
            severity = Math.Max(severity, peakRtt / thresholds.RoundTripMs);
        }

        if (kinds.Contains("jitter") && thresholds.JitterMs > 0)
        {
            severity = Math.Max(severity, peakJitter / thresholds.JitterMs);
        }

        return new TraceEvent(
            run[0].Timestamp,
            run[^1].Timestamp + stepSeconds,
            peakRtt,
            peakJitter,
            sent,
            lost,
            kinds,
            severity);
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0)
        {
            return double.NaN;
        }

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    private static double Percentile(double[] values, double fraction)
    {
        if (values.Length == 0)
        {
            return double.NaN;
        }

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var index = Math.Clamp((int)(fraction * sorted.Length), 0, sorted.Length - 1);
        return sorted[index];
    }

    /// <summary>
    /// Spread measured as the median distance from the middle. Used instead of a
    /// standard deviation because the spikes being looked for would otherwise inflate
    /// the very number meant to decide whether they are unusual.
    /// </summary>
    private static double MedianAbsoluteDeviation(double[] values, double median)
    {
        if (values.Length == 0 || double.IsNaN(median))
        {
            return 0;
        }

        var deviations = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            deviations[i] = Math.Abs(values[i] - median);
        }

        return Median(deviations);
    }
}
