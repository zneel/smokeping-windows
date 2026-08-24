using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmokePing.Net.Alerting;
using SmokePing.Net.Configuration;
using SmokePing.Net.Graphing;
using SmokePing.Net.Probes;
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

    /// <summary>
    /// Reads a period for a target from whichever store is configured, so the web
    /// interface does not care which format the measurements are kept in.
    /// </summary>
    private static (IReadOnlyList<Storage.Sample> Samples, int StepSeconds) ReadSamples(
        MeasuredTarget target,
        DataStore store,
        RrdTargetStore? rrdStore,
        long from,
        long to)
    {
        if (rrdStore is not null)
        {
            var file = rrdStore.GetOrOpen(target, to);
            var archive = RrdTargetStore.SelectArchive(file, to - from);
            return (rrdStore.Read(target, from, to), (int)file.RowStep(archive));
        }

        var database = store.GetOrOpen(target);
        var index = database.SelectArchive(to - from);
        return (database.Read(index, from, to), database.Archives[index].StepSeconds);
    }

    public static void MapSmokePingApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/config", (LoadedConfiguration config) => Results.Ok(new
        {
            siteName = config.Raw.General.SiteName,
            owner = config.Raw.General.Owner,
            contactEmail = config.Raw.General.ContactEmail,
            detailRanges = DetailRanges.Select(r => new { label = r.Label, range = r.Range }),
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
            DataStore store,
            ProbeRegistry probes,
            HostResolver resolver,
            RrdTargetStore? rrdStore = null) =>
        {
            if (!config.TryGetTarget(id, out var target))
            {
                return Results.NotFound(new { error = $"Unknown target '{id}'." });
            }

            var (from, to) = ResolveRange(range);
            var (samples, stepSeconds) = ReadSamples(target, store, rrdStore, from, to);
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
            DataStore store,
            ProbeRegistry probes,
            HostResolver resolver,
            RrdTargetStore? rrdStore = null) =>
        {
            // The graph id carries a .svg suffix so it can be used directly in an <img>.
            id = id.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? id[..^4] : id;

            if (!config.TryGetTarget(id, out var target))
            {
                return Results.NotFound(new { error = $"Unknown target '{id}'." });
            }

            var (from, to) = ResolveRange(range);
            var (samples, stepSeconds) = ReadSamples(target, store, rrdStore, from, to);

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
            DataStore store,
            RrdTargetStore? rrdStore = null) =>
        {
            var (from, to) = ResolveRange(range ?? "10h");
            var limit = Math.Clamp(entries ?? 5, 1, 50);

            // Upstream's sorters rank on the single most recent round, so these do
            // too: "Top Max" is the slowest probe of that round, not the highest
            // median over the window.
            var rows = config.Targets.Select(target =>
            {
                var (samples, _) = ReadSamples(target, store, rrdStore, from, to);
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

        app.MapGet("/api/health", (LoadedConfiguration config, DataStore store) => Results.Ok(new
        {
            status = "ok",
            targets = config.Targets.Count,
            dataDirectory = store.DataDirectory,
            utcNow = DateTimeOffset.UtcNow,
        }));
    }

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
