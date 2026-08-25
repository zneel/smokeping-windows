using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmokePing.Net.Alerting;
using SmokePing.Net.Configuration;
using SmokePing.Net.Graphing;
using System.Text.Json;
using SmokePing.Net.Probes;
using SmokePing.Net.Services;
using SmokePing.Net.Rrd;
using SmokePing.Net.Storage;

namespace SmokePing.Net.Web;

/// <summary>The JSON and SVG endpoints backing the web interface.</summary>
public static partial class ApiEndpoints
{
    /// <summary>Periods offered on a target's detail page, mirroring upstream's defaults.</summary>
    public static readonly (string Label, string Range)[] DetailRanges =
    [
        ("Last 3 Hours", "3h"),
        ("Last 30 Hours", "30h"),
        ("Last 10 Days", "10d"),
        ("Last 360 Days", "360d"),
    ];

    public static void MapSmokePingApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/config", (LoadedConfiguration config) => Results.Ok(new
        {
            siteName = config.Raw.General.SiteName,
            owner = config.Raw.General.Owner,
            contactEmail = config.Raw.General.ContactEmail,
            detailRanges = DetailRanges.Select(r => new { label = r.Label, range = r.Range }),
            trace = new
            {
                enabled = config.Targets.Any(t => t.Traced),
                intervalSeconds = config.Raw.Trace.IntervalSeconds,
                fineHours = config.Raw.Trace.FineHours,
                coarseDays = config.Raw.Trace.CoarseDays,
                ranges = TraceRanges.Select(r => new { label = r.Label, range = r.Range }),
            },
            menu = config.Menu,
            targetCount = config.Targets.Count,
        }));

        app.MapGet("/api/targets", (LoadedConfiguration config, ProbeRegistry probes, HostResolver resolver) => Results.Ok(
            config.Targets.Select(target => new
            {
                target.Id,
                target.Title,
                target.Host,
                resolvedHost = target.HasDynamicHost ? resolver.Resolve(target.Host) : target.Host,
                target.ProbeType,
                target.StepSeconds,
                target.Pings,
                target.Description,
                target.AlertRules,
                probeDescription = probes.Get(target.ProbeType).Describe(target),
            })));

        app.MapGet("/api/targets/{*id}", (
            string id,
            string? range,
            LoadedConfiguration config,
            MeasurementStore store,
            ProbeRegistry probes,
            HostResolver resolver) =>
        {
            if (!config.TryGetTarget(id, out var target))
            {
                return Results.NotFound(new { error = $"Unknown target '{id}'." });
            }

            var (from, to) = ResolveRange(range);
            var (samples, stepSeconds) = store.Read(target, from, to);
            var statistics = GraphStatistics.Compute(samples);

            return Results.Ok(new
            {
                target.Id,
                target.Title,
                target.Host,
                resolvedHost = target.HasDynamicHost ? resolver.Resolve(target.Host) : target.Host,
                target.ProbeType,
                target.Description,
                target.Pings,
                probeDescription = probes.Get(target.ProbeType).Describe(target),
                stepSeconds,
                from,
                to,
                statistics,
                lossScale = LossColours.BuildScale(target.Pings),
                samples = samples.Select(s => new
                {
                    t = s.Timestamp,
                    s.Sent,
                    s.Lost,
                    median = s.Median,
                    jitter = s.JitterMilliseconds,
                    quantiles = s.Quantiles.Select(q => float.IsNaN(q) ? (float?)null : q),
                }),
            });
        });

        app.MapGet("/api/graph/{*id}", (
            string id,
            string? range,
            int? width,
            int? height,
            string? theme,
            int? offsetMinutes,
            bool? compact,
            string? title,
            string? subtitle,
            LoadedConfiguration config,
            MeasurementStore store,
            ProbeRegistry probes,
            HostResolver resolver) =>
        {
            // The graph id carries a .svg suffix so it can be used directly in an <img>.
            id = id.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? id[..^4] : id;

            if (!config.TryGetTarget(id, out var target))
            {
                return Results.NotFound(new { error = $"Unknown target '{id}'." });
            }

            var (from, to) = ResolveRange(range);
            var (samples, stepSeconds) = store.Read(target, from, to);

            var request = new GraphRequest
            {
                Samples = samples,
                Pings = target.Pings,
                FromTimestamp = from,
                ToTimestamp = to,
                StepSeconds = stepSeconds,
                // The heading is part of the picture so an embedded graph explains
                // itself; a caller that already labels it can pass an empty override.
                Title = title ?? $"{target.Title} - {(target.HasDynamicHost ? resolver.Resolve(target.Host) ?? target.Host : target.Host)}",
                Subtitle = subtitle ?? probes.Get(target.ProbeType).Describe(target),
                PlotWidth = Math.Clamp(width ?? 600, 120, 2000),
                PlotHeight = Math.Clamp(height ?? 200, 30, 1000),
                Compact = compact ?? false,
                Theme = GraphTheme.FromName(theme),
                UtcOffset = TimeSpan.FromMinutes(Math.Clamp(offsetMinutes ?? 0, -14 * 60, 14 * 60)),
            };

            return Results.Text(SmokeGraphRenderer.Render(request), "image/svg+xml");
        });

        app.MapGet("/api/alerts", (AlertNotifier notifier, AlertEngine engine, LoadedConfiguration config) =>
            Results.Ok(new
            {
                rules = config.Alerts.Values.Select(rule => new
                {
                    rule.Name,
                    rule.Config.Type,
                    rule.Config.Pattern,
                    rule.Config.Comment,
                    rule.Config.EdgeTrigger,
                    rule.Config.Priority,
                }),
                active = engine.Active.Values.OrderBy(a => a.TargetId),
                recent = notifier.Recent,
            }));

        app.MapGet("/api/charts", (
            string? range,
            int? entries,
            LoadedConfiguration config,
            MeasurementStore store) =>
        {
            var (from, to) = ResolveRange(range ?? "10h");
            var limit = Math.Clamp(entries ?? 5, 1, 50);

            // Upstream's sorters rank on the single most recent round, so these do
            // too: "Top Max" is the slowest probe of that round, not the highest
            // median over the window.
            var rows = config.Targets.Select(target =>
            {
                var (samples, _) = store.Read(target, from, to);
                var latest = samples.LastOrDefault(sample => sample.Sent > 0);
                return new { target, statistics = GraphStatistics.Compute(samples), latest };
            })
            .Where(row => row.statistics.HasData && row.latest is not null)
            .ToList();

            object Chart(string title, IEnumerable<object> items) => new { title, items };

            IEnumerable<object> Top(Func<GraphStatistics, Storage.Sample, double> value) => rows
                .OrderByDescending(row => value(row.statistics, row.latest!))
                .Take(limit)
                .Select(row => new
                {
                    row.target.Id,
                    row.target.Title,
                    row.target.Host,
                    value = value(row.statistics, row.latest!),
                });

            // The slowest probe of the latest round is the top stored quantile.
            static double LatestMaximum(Storage.Sample sample)
            {
                for (var i = Storage.Sample.QuantileCount - 1; i >= 0; i--)
                {
                    if (!float.IsNaN(sample.Quantiles[i]))
                    {
                        return sample.Quantiles[i];
                    }
                }

                return 0;
            }

            return Results.Ok(new
            {
                from,
                to,
                charts = new[]
                {
                    Chart("Top Standard Deviation", Top((s, _) => s.StandardDeviation)),
                    Chart("Top Jitter", Top((_, latest) => latest.JitterMilliseconds ?? 0)),
                    Chart("Top Max Roundtrip Time", Top((_, latest) => LatestMaximum(latest))),
                    Chart("Top Packet Loss", Top((_, latest) => latest.LossFraction * 100)),
                    Chart("Top Median Roundtrip Time", Top((_, latest) => latest.Median ?? 0)),
                },
            });
        });

        // The recorded trace: what the link was actually doing, second by second,
        // whether or not anybody was watching at the time.
        app.MapGet("/api/trace/{*id}", (
            string id,
            string? range,
            long? from,
            long? to,
            int? points,
            LoadedConfiguration config,
            TraceStore traces) =>
        {
            if (!config.TryGetTarget(id, out var target))
            {
                return Results.NotFound(new { error = $"Unknown target '{id}'." });
            }

            if (!target.Traced)
            {
                return Results.NotFound(new { error = $"Target '{id}' is not traced." });
            }

            var (start, stop) = ResolveWindow(range, from, to);
            var (samples, resolution) = traces.Read(target, start, stop);

            // Peaks are found at the resolution they were recorded at and only then is
            // the series thinned for drawing. Detecting on the thinned series would
            // mean deciding what counts as a spike from data a spike has already been
            // averaged out of.
            var thresholds = TraceAnalysis.ComputeThresholds(samples, config.Raw.Trace.Spike);
            var events = TraceAnalysis.FindEvents(samples, resolution, thresholds);
            var summary = TraceAnalysis.Summarise(samples, events.Count);
            var series = TraceAnalysis.Downsample(samples, Math.Clamp(points ?? 900, 50, 4000));

            return Results.Ok(new
            {
                target.Id,
                target.Title,
                target.Host,
                from = start,
                to = stop,
                resolutionSeconds = resolution,
                columnSeconds = series.Count == 0 ? resolution : Math.Max(resolution, (stop - start) / series.Count),
                recordedSeconds = samples.Count(s => s.HasData) * (long)resolution,
                thresholds = new
                {
                    baselineMs = Round(thresholds.Baseline),
                    roundTripMs = Round(thresholds.RoundTripMs),
                    jitterBaselineMs = Round(thresholds.JitterBaseline),
                    jitterMs = Round(thresholds.JitterMs),
                },
                summary = new
                {
                    summary.Sent,
                    summary.Lost,
                    lossPercent = Round(summary.LossPercent),
                    medianMs = Round(summary.MedianRoundTrip),
                    p95Ms = Round(summary.NinetyFifthRoundTrip),
                    maxMs = Round(summary.MaximumRoundTrip),
                    medianJitterMs = Round(summary.MedianJitter),
                    maxJitterMs = Round(summary.MaximumJitter),
                    summary.EventCount,
                },
                events = events.Select(e => new
                {
                    start = e.Start,
                    end = e.End,
                    e.DurationSeconds,
                    peakMs = Round(e.PeakRoundTrip),
                    peakJitterMs = Round(e.PeakJitter),
                    e.Sent,
                    e.Lost,
                    e.Kinds,
                    severity = Round(e.Severity),
                }),
                samples = series.Select(s => new
                {
                    t = s.Timestamp,
                    s.Sent,
                    s.Lost,
                    min = Round(s.Minimum),
                    avg = Round(s.Mean),
                    max = Round(s.Maximum),
                    jitter = Round(s.Jitter),
                    jitterMax = Round(s.JitterMaximum),
                }),
            });
        });

        app.MapGet("/api/health", (LoadedConfiguration config, MeasurementStore store, TraceStore traces) => Results.Ok(new
        {
            status = "ok",
            targets = config.Targets.Count,
            storageFormat = store.Format,
            tracedTargets = config.Targets.Count(t => t.Traced),
            traceIntervalSeconds = traces.StepSeconds,
            traceBytesPerTarget = traces.BytesPerTarget,
            dataDirectory = store.DataDirectory,
            utcNow = DateTimeOffset.UtcNow,
        }));
    }

    /// <summary>Periods offered on the trace panel.</summary>
    public static readonly (string Label, string Range)[] TraceRanges =
    [
        ("15 min", "15m"),
        ("1 hour", "1h"),
        ("3 hours", "3h"),
        ("12 hours", "12h"),
        ("24 hours", "24h"),
    ];

    /// <summary>
    /// Resolves the window a trace request asks for. Explicit endpoints win over a
    /// range, which is how clicking a peak zooms to the moment it happened rather than
    /// to some period ending now.
    /// </summary>
    public static (long From, long To) ResolveWindow(string? range, long? from, long? to)
    {
        if (from is { } start && to is { } stop && stop > start)
        {
            return (start, stop);
        }

        var span = ParseSpan(range) ?? TimeSpan.FromMinutes(15);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return (now - (long)span.TotalSeconds, now);
    }

    /// <summary>Renders a millisecond figure for JSON, turning "no reading" into null.</summary>
    private static double? Round(double value) =>
        double.IsNaN(value) ? null : Math.Round(value, 2);

    /// <summary>
    /// Parses a range in SmokePing's notation - "3h", "30h", "10d", "360d", "2w" - and
    /// returns the absolute window it describes, ending at the current step boundary.
    /// </summary>
    public static (long From, long To) ResolveRange(string? range)
    {
        var span = ParseSpan(range) ?? TimeSpan.FromHours(3);
        var to = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return (to - (long)span.TotalSeconds, to);
    }

    /// <summary>Parses "90m", "3h", "10d", "2w", "6mon", "1y". Returns null when unparseable.</summary>
    public static TimeSpan? ParseSpan(string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return null;
        }

        var match = RangePattern().Match(range.Trim());
        if (!match.Success)
        {
            return null;
        }

        var amount = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var span = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "s" => TimeSpan.FromSeconds(amount),
            "m" => TimeSpan.FromMinutes(amount),
            "h" => TimeSpan.FromHours(amount),
            "d" => TimeSpan.FromDays(amount),
            "w" => TimeSpan.FromDays(amount * 7),
            "mon" => TimeSpan.FromDays(amount * 30),
            "y" => TimeSpan.FromDays(amount * 365),
            _ => TimeSpan.Zero,
        };

        return span <= TimeSpan.Zero ? null : span;
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*(s|m|h|d|w|mon|y)$", RegexOptions.IgnoreCase)]
    private static partial Regex RangePattern();
}
