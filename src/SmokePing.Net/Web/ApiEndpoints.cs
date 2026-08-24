using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmokePing.Net.Alerting;
using SmokePing.Net.Configuration;
using SmokePing.Net.Graphing;
using SmokePing.Net.Probes;
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
            menu = config.Menu,
            targetCount = config.Targets.Count,
        }));

        app.MapGet("/api/targets", (LoadedConfiguration config, ProbeRegistry probes) => Results.Ok(
            config.Targets.Select(target => new
            {
                target.Id,
                target.Title,
                target.Host,
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
            ProbeRegistry probes) =>
        {
            if (!config.TryGetTarget(id, out var target))
            {
                return Results.NotFound(new { error = $"Unknown target '{id}'." });
            }

            var (from, to) = ResolveRange(range);
            var database = store.GetOrOpen(target);
            var archive = database.SelectArchive(to - from);
            var samples = database.Read(archive, from, to);
            var statistics = GraphStatistics.Compute(samples);

            return Results.Ok(new
            {
                target.Id,
                target.Title,
                target.Host,
                target.ProbeType,
                target.Description,
                target.Pings,
                probeDescription = probes.Get(target.ProbeType).Describe(target),
                stepSeconds = database.Archives[archive].StepSeconds,
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
            ProbeRegistry probes) =>
        {
            // The graph id carries a .svg suffix so it can be used directly in an <img>.
            id = id.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? id[..^4] : id;

            if (!config.TryGetTarget(id, out var target))
            {
                return Results.NotFound(new { error = $"Unknown target '{id}'." });
            }

            var (from, to) = ResolveRange(range);
            var database = store.GetOrOpen(target);
            var archive = database.SelectArchive(to - from);

            var request = new GraphRequest
            {
                Samples = database.Read(archive, from, to),
                Pings = target.Pings,
                FromTimestamp = from,
                ToTimestamp = to,
                StepSeconds = database.Archives[archive].StepSeconds,
                // The heading is part of the picture so an embedded graph explains
                // itself; a caller that already labels it can pass an empty override.
                Title = title ?? $"{target.Title} - {target.Host}",
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
            DataStore store) =>
        {
            var (from, to) = ResolveRange(range ?? "10h");
            var limit = Math.Clamp(entries ?? 5, 1, 50);

            var rows = config.Targets.Select(target =>
            {
                var database = store.GetOrOpen(target);
                var archive = database.SelectArchive(to - from);
                var statistics = GraphStatistics.Compute(database.Read(archive, from, to));
                return new { target, statistics };
            })
            .Where(row => row.statistics.HasData)
            .ToList();

            object Chart(string title, IEnumerable<object> items) => new { title, items };

            IEnumerable<object> Top(Func<GraphStatistics, double> value) => rows
                .OrderByDescending(row => value(row.statistics))
                .Take(limit)
                .Select(row => new
                {
                    row.target.Id,
                    row.target.Title,
                    row.target.Host,
                    value = value(row.statistics),
                });

            return Results.Ok(new
            {
                from,
                to,
                charts = new[]
                {
                    Chart("Top Standard Deviation", Top(s => s.StandardDeviation)),
                    Chart("Top Jitter", Top(s => s.Jitter)),
                    Chart("Top Max Roundtrip Time", Top(s => s.MedianMaximum)),
                    Chart("Top Packet Loss", Top(s => s.LossAverage)),
                    Chart("Top Median Roundtrip Time", Top(s => s.MedianAverage)),
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
