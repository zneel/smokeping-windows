using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SmokePing.Net.Alerting;

/// <summary>
/// Delivers alert notifications: always to the log, plus an optional webhook POST
/// and an optional external command, matching the two mechanisms upstream offers
/// (a mail template and a "|command" recipient).
/// </summary>
public sealed class AlertNotifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Name of the configured client webhooks are posted with.</summary>
    public const string HttpClientName = "alerts";

    private readonly ILogger<AlertNotifier> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly List<AlertEvent> _recent = [];
    private readonly object _recentGate = new();

    public AlertNotifier(ILogger<AlertNotifier> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Number of past notifications kept for the web interface.</summary>
    public const int RecentCapacity = 200;

    /// <summary>Most recent notifications, newest first.</summary>
    public IReadOnlyList<AlertEvent> Recent
    {
        get
        {
            lock (_recentGate)
            {
                return _recent.AsEnumerable().Reverse().ToList();
            }
        }
    }

    public async Task NotifyAsync(
        AlertEvent alertEvent,
        Configuration.AlertRuleConfig rule,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alertEvent);
        ArgumentNullException.ThrowIfNull(rule);

        Record(alertEvent);

        _logger.LogWarning(
            "Alert {Alert} {State} for {Target} ({Host}) - loss: {Loss} rtt: {Rtt} - {Comment}",
            alertEvent.AlertName,
            alertEvent.State,
            alertEvent.TargetId,
            alertEvent.Host,
            string.Join(", ", alertEvent.LossHistory.TakeLast(10)),
            string.Join(", ", alertEvent.RttHistory.TakeLast(10)),
            alertEvent.Comment);

        if (!string.IsNullOrWhiteSpace(rule.WebhookUrl))
        {
            await PostWebhookAsync(alertEvent, rule.WebhookUrl, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(rule.Command))
        {
            RunCommand(alertEvent, rule.Command);
        }
    }

    private void Record(AlertEvent alertEvent)
    {
        lock (_recentGate)
        {
            _recent.Add(alertEvent);
            if (_recent.Count > RecentCapacity)
            {
                _recent.RemoveRange(0, _recent.Count - RecentCapacity);
            }
        }
    }

    private async Task PostWebhookAsync(AlertEvent alertEvent, string url, CancellationToken cancellationToken)
    {
        try
        {
            var payload = JsonSerializer.Serialize(alertEvent, JsonOptions);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _httpClientFactory
                .CreateClient(HttpClientName)
                .PostAsync(url, content, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Alert webhook {Url} returned {StatusCode} for alert {Alert}.",
                    url,
                    (int)response.StatusCode,
                    alertEvent.AlertName);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogError(ex, "Alert webhook {Url} failed for alert {Alert}.", url, alertEvent.AlertName);
        }
    }

    /// <summary>
    /// Runs the configured command with the same argument list upstream passes:
    /// name, target, loss history, rtt history, host and the raise/clear flag.
    /// </summary>
    private void RunCommand(AlertEvent alertEvent, string command)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add(alertEvent.AlertName);
            startInfo.ArgumentList.Add(alertEvent.TargetId);
            startInfo.ArgumentList.Add("loss: " + string.Join(", ", alertEvent.LossHistory));
            startInfo.ArgumentList.Add("rtt: " + string.Join(", ", alertEvent.RttHistory));
            startInfo.ArgumentList.Add(alertEvent.Host);
            startInfo.ArgumentList.Add(alertEvent.IsRaised ? "1" : "0");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                _logger.LogError("Alert command '{Command}' could not be started.", command);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogError(ex, "Alert command '{Command}' failed to start.", command);
        }
    }
}
