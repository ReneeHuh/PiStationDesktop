using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiStation.Protocol.Models;

namespace PiStation.Host.Diagnostics;

internal sealed partial class RuntimeHealthService
{
    private string _metricsExportStatus = "OTLP metrics export is disabled.";
    internal async Task FlushMetricsAsync(CancellationToken stopping)
    {
        OperationMetric[] metrics;
        string? endpoint;
        CancellationToken changed;
        lock (_gate)
        {
            endpoint = _settings.MetricsEnabled ? _settings.OtlpMetricsEndpoint : null;
            if (endpoint is null || _metrics.Count == 0) return;
            metrics = _metrics.Values.ToArray(); changed = _exportChanged.Token;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping, changed);
        cancellation.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var uri = new Uri(endpoint);
            if (uri.AbsolutePath is "" or "/") uri = new Uri(uri, "/v1/metrics");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(CreateOtlpMetrics(metrics).ToJsonString(), Encoding.UTF8, "application/json") };
            using var reply = await _exportClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).ConfigureAwait(false);
            var status = $"OTLP metrics returned HTTP {(int)reply.StatusCode}.";
            if (reply.IsSuccessStatusCode)
            {
                await using var stream = await reply.Content.ReadAsStreamAsync(cancellation.Token).ConfigureAwait(false);
                var bytes = new byte[16 * 1024 + 1];
                var length = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, cancellation.Token).ConfigureAwait(false);
                status = $"Exported metrics for {metrics.Length} operations at {DateTimeOffset.Now:T}.";
                if (length == bytes.Length) status = "OTLP metrics response exceeded 16 KiB; delivery could not be confirmed.";
                else if (length > 0)
                {
                    using var response = JsonDocument.Parse(bytes.AsMemory(0, length));
                    if (response.RootElement.TryGetProperty("partialSuccess", out var partial) && partial.TryGetProperty("rejectedDataPoints", out var rejected) && rejected.ToString() != "0")
                        status = "OTLP collector reported partially rejected metric points.";
                }
            }
            lock (_gate) if (!changed.IsCancellationRequested) _metricsExportStatus = status;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or JsonException or InvalidOperationException)
        { lock (_gate) if (!changed.IsCancellationRequested) _metricsExportStatus = "OTLP metrics delivery failed. " + error.GetType().Name; }
    }

    internal static JsonObject CreateOtlpMetrics(IReadOnlyList<OperationMetric> metrics)
    {
        var timestamp = ((DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100).ToString(CultureInfo.InvariantCulture);
        JsonObject Gauge(string name, string unit, Func<OperationMetric, double> value)
        {
            var points = new JsonArray();
            foreach (var metric in metrics) points.Add(new JsonObject { ["timeUnixNano"] = timestamp, ["asDouble"] = value(metric),
                ["attributes"] = new JsonArray(new JsonObject { ["key"] = "rpc.method", ["value"] = new JsonObject { ["stringValue"] = metric.Operation } }) });
            return new JsonObject { ["name"] = name, ["unit"] = unit, ["gauge"] = new JsonObject { ["dataPoints"] = points } };
        }
        // Gauges report the current bounded collection, which can be cleared by
        // the user. They deliberately make no monotonic-counter promise.
        return new JsonObject { ["resourceMetrics"] = new JsonArray(new JsonObject {
            ["resource"] = new JsonObject { ["attributes"] = new JsonArray(new JsonObject { ["key"] = "service.name", ["value"] = new JsonObject { ["stringValue"] = "PiStation.Host" } }) },
            ["scopeMetrics"] = new JsonArray(new JsonObject { ["scope"] = new JsonObject { ["name"] = "PiStation.Host" },
                ["metrics"] = new JsonArray(Gauge("pistation.rpc.calls", "{call}", item => item.Count), Gauge("pistation.rpc.failures", "{call}", item => item.Failures),
                    Gauge("pistation.rpc.duration.total", "ms", item => item.TotalMilliseconds), Gauge("pistation.rpc.duration.max", "ms", item => item.MaximumMilliseconds)) }) }) };
    }
}
