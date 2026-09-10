using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiStation.Protocol.Models;

namespace PiStation.Host.Diagnostics;

internal sealed partial class RuntimeHealthService
{
    private readonly Queue<OperationTrace> _exportQueue = new();
    private readonly HttpClient _exportClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(3) };
    private CancellationTokenSource _exportChanged = new();
    private string _exportStatus = "OTLP export is disabled.";

    private void EnqueueExport(OperationTrace trace)
    {
        if (_settings.OtlpEndpoint is null) return;
        _exportQueue.Enqueue(trace);
        while (_exportQueue.Count > 256) { _exportQueue.Dequeue(); _exportStatus = "OTLP queue reached its limit; oldest spans were dropped."; }
    }
    private void ResetExport()
    {
        _exportChanged.Cancel(); _exportChanged.Dispose(); _exportChanged = new();
        _exportQueue.Clear();
        _exportStatus = _settings.TracingEnabled && _settings.OtlpEndpoint is not null ? "OTLP export enabled; waiting for spans." : "OTLP export is disabled.";
        _metricsExportStatus = _settings.MetricsEnabled && _settings.OtlpMetricsEndpoint is not null ? "OTLP metrics enabled; waiting for measurements." : "OTLP metrics export is disabled.";
    }
    private void DisposeExport() { _exportChanged.Cancel(); _exportChanged.Dispose(); _exportClient.Dispose(); }

    internal async Task FlushExportAsync(CancellationToken stopping)
    {
        OperationTrace[] traces;
        string? endpoint;
        CancellationToken changed;
        lock (_gate)
        {
            endpoint = _settings.TracingEnabled ? _settings.OtlpEndpoint : null;
            if (endpoint is null || _exportQueue.Count == 0) return;
            traces = Enumerable.Range(0, Math.Min(64, _exportQueue.Count)).Select(_ => _exportQueue.Dequeue()).ToArray();
            changed = _exportChanged.Token;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping, changed);
        cancellation.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var uri = new Uri(endpoint);
            if (uri.AbsolutePath is "" or "/") uri = new Uri(uri, "/v1/traces");
            using var body = new StringContent(CreateOtlpTraces(traces).ToJsonString(), Encoding.UTF8);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = body };
            using var reply = await _exportClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).ConfigureAwait(false);
            var status = $"OTLP returned HTTP {(int)reply.StatusCode}; this batch was dropped.";
            if (reply.IsSuccessStatusCode)
            {
                await using var stream = await reply.Content.ReadAsStreamAsync(cancellation.Token).ConfigureAwait(false);
                var bytes = new byte[16 * 1024 + 1];
                var length = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellation.Token).ConfigureAwait(false);
                if (length == bytes.Length) status = "OTLP response exceeded 16 KiB; delivery could not be confirmed.";
                else if (length == 0) status = $"Exported {traces.Length} spans at {DateTimeOffset.Now:T}.";
                else
                {
                    using var response = JsonDocument.Parse(bytes.AsMemory(0, length));
                    status = response.RootElement.TryGetProperty("partialSuccess", out var partial) &&
                        partial.TryGetProperty("rejectedSpans", out var rejected) && rejected.ToString() != "0"
                        ? "OTLP collector reported partially rejected spans; this batch will not be retried."
                        : $"Exported {traces.Length} spans at {DateTimeOffset.Now:T}.";
                }
            }
            lock (_gate)
                if (!changed.IsCancellationRequested) _exportStatus = status;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or JsonException or InvalidOperationException)
        { lock (_gate) if (!changed.IsCancellationRequested) _exportStatus = "OTLP delivery failed; this batch was dropped. " + error.GetType().Name; }
    }

    internal static JsonObject CreateOtlpTraces(IEnumerable<OperationTrace> traces)
    {
        var spans = new JsonArray();
        foreach (var trace in traces)
        {
            var start = (trace.StartedUtc.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100;
            spans.Add(new JsonObject { ["traceId"] = trace.TraceId, ["spanId"] = trace.SpanId, ["name"] = trace.Operation,
                ["kind"] = 2, ["startTimeUnixNano"] = start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["endTimeUnixNano"] = (start + (long)(trace.DurationMilliseconds * 1_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["status"] = new JsonObject { ["code"] = trace.Outcome == "ok" ? 1 : 2 },
                ["attributes"] = new JsonArray(new JsonObject { ["key"] = "pistation.outcome", ["value"] = new JsonObject { ["stringValue"] = trace.Outcome } }) });
        }
        return new JsonObject { ["resourceSpans"] = new JsonArray(new JsonObject {
            ["resource"] = new JsonObject { ["attributes"] = new JsonArray(new JsonObject { ["key"] = "service.name", ["value"] = new JsonObject { ["stringValue"] = "PiStation.Host" } }) },
            ["scopeSpans"] = new JsonArray(new JsonObject { ["scope"] = new JsonObject { ["name"] = "PiStation.Host" }, ["spans"] = spans }) }) };
    }
}
