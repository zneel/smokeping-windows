using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmokePing.Net.Storage;

namespace SmokePing.Net.Desktop;

/// <summary>A node of the target menu.</summary>
public sealed class MenuNodeDto
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsTarget { get; set; }

    public string? Host { get; set; }

    public string? ProbeType { get; set; }

    public List<MenuNodeDto> Children { get; set; } = [];
}

public sealed class RangeDto
{
    public string Label { get; set; } = string.Empty;

    public string Range { get; set; } = string.Empty;
}

public sealed class SiteConfigDto
{
    public string SiteName { get; set; } = "SmokePing.NET";

    public string Owner { get; set; } = string.Empty;

    public int TargetCount { get; set; }

    public List<RangeDto> DetailRanges { get; set; } = [];

    public List<MenuNodeDto> Menu { get; set; } = [];
}

public sealed class StatisticsDto
{
    public bool HasData { get; set; }

    public double Median { get; set; }

    public double Minimum { get; set; }

    public double Maximum { get; set; }

    public double StandardDeviation { get; set; }

    public double LossPercent { get; set; }

    public int RoundsWithData { get; set; }
}

public sealed class SampleDto
{
    [JsonPropertyName("t")]
    public long Timestamp { get; set; }

    public int Sent { get; set; }

    public int Lost { get; set; }

    public float?[] Quantiles { get; set; } = [];

    /// <summary>Converts to the storage type the graph layout consumes.</summary>
    public Sample ToSample()
    {
        var quantiles = Sample.CreateNaNQuantiles();
        for (var i = 0; i < quantiles.Length && i < Quantiles.Length; i++)
        {
            quantiles[i] = Quantiles[i] ?? float.NaN;
        }

        return new Sample
        {
            Timestamp = Timestamp,
            Sent = Sent,
            Lost = Lost,
            Quantiles = quantiles,
        };
    }
}

public sealed class TargetDataDto
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string ProbeDescription { get; set; } = string.Empty;

    public int Pings { get; set; }

    public int StepSeconds { get; set; }

    public long From { get; set; }

    public long To { get; set; }

    public StatisticsDto Statistics { get; set; } = new();

    public List<SampleDto> Samples { get; set; } = [];
}

public sealed class AlertEventDto
{
    public DateTimeOffset Timestamp { get; set; }

    public string AlertName { get; set; } = string.Empty;

    public string TargetId { get; set; } = string.Empty;

    public string TargetTitle { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;
}

public sealed class AlertRuleDto
{
    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Pattern { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    public bool EdgeTrigger { get; set; }
}

public sealed class AlertsDto
{
    public List<AlertRuleDto> Rules { get; set; } = [];

    public List<AlertEventDto> Active { get; set; } = [];

    public List<AlertEventDto> Recent { get; set; } = [];
}

public sealed class ChartItemDto
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public double Value { get; set; }
}

public sealed class ChartDto
{
    public string Title { get; set; } = string.Empty;

    public List<ChartItemDto> Items { get; set; } = [];
}

public sealed class ChartsDto
{
    public List<ChartDto> Charts { get; set; } = [];
}

/// <summary>
/// Reads from a running SmokePing.NET daemon over its HTTP API.
///
/// The desktop client deliberately does not open the measurement files itself: the
/// daemon owns them, and two processes writing the same round-robin file would
/// corrupt it. This also means the client can watch a daemon on another machine.
/// </summary>
public sealed class SmokePingClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;

    public SmokePingClient(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);

        BaseAddress = baseAddress;
        _httpClient = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    public Uri BaseAddress { get; }

    public Task<SiteConfigDto?> GetConfigAsync(CancellationToken cancellationToken) =>
        GetAsync<SiteConfigDto>("api/config", cancellationToken);

    public Task<TargetDataDto?> GetTargetAsync(string id, string range, CancellationToken cancellationToken) =>
        GetAsync<TargetDataDto>($"api/targets/{id}?range={Uri.EscapeDataString(range)}", cancellationToken);

    public Task<AlertsDto?> GetAlertsAsync(CancellationToken cancellationToken) =>
        GetAsync<AlertsDto>("api/alerts", cancellationToken);

    public Task<ChartsDto?> GetChartsAsync(string range, CancellationToken cancellationToken) =>
        GetAsync<ChartsDto>($"api/charts?range={Uri.EscapeDataString(range)}&entries=5", cancellationToken);

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content
            .ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose() => _httpClient.Dispose();
}
