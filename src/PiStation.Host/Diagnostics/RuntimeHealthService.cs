using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using PiStation.Protocol.Models;
using PiStation.Protocol.Platform;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Diagnostics;

internal sealed partial class RuntimeHealthService : IAsyncDisposable
{
    internal static readonly ActivitySource Activities = new("PiStation.Host");
    private static readonly Meter Meter = new("PiStation.Host");
    private static readonly Histogram<double> Durations = Meter.CreateHistogram<double>("pistation.rpc.duration", "ms");
    private readonly object _gate = new();
    private readonly object _samplingGate = new();
    private readonly string _path;
    private readonly ProcessResourceMonitor _processes;
    private readonly Queue<ResourceHistorySample> _history = new();
    private readonly Queue<OperationTrace> _traces = new();
    private readonly Dictionary<string, OperationMetric> _metrics = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _monitor;
    private RuntimeHealthSettings _settings = new();
    private PowerState _power = new(null, null, null, DateTimeOffset.MinValue);
    private IReadOnlyList<ProcessResourceSample> _latest = [];
    private string _status = "Waiting for diagnostics demand.";
    internal BackgroundActivityPolicy Activity { get; } = new();

    public RuntimeHealthService(string dataRoot, Func<IEnumerable<OwnedProcessRoot>> roots)
    {
        _path = Path.Combine(dataRoot, "runtime-health.json");
        _processes = new(roots);
        try
        {
            if (File.Exists(_path))
            {
                if (new FileInfo(_path).Length > 16 * 1024) throw new InvalidDataException("Settings exceed their size limit.");
                _settings = Validate(JsonSerializer.Deserialize(File.ReadAllText(_path), ProtocolJsonContext.Default.RuntimeHealthSettings) ?? new());
            }
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException)
        { _status = "Runtime health settings could not be loaded; defaults are active."; }
        ResetExport();
        _monitor = MonitorAsync();
    }

    internal BackgroundPolicySnapshot Background { get { lock (_gate) return Activity.Evaluate(_settings, _power); } }

    internal RuntimeHealthSnapshot Snapshot(bool refresh = true)
    {
        if (refresh) Sample();
        lock (_gate)
        {
            return new(Activity.Evaluate(_settings, _power), _latest, _history.ToArray(), _traces.ToArray(),
                _metrics.Values.OrderByDescending(metric => metric.TotalMilliseconds).ToArray(), _status, _exportStatus + " " + _metricsExportStatus);
        }
    }

    internal RuntimeHealthSettings Save(RuntimeHealthSettings proposed)
    {
        proposed = Validate(proposed);
        lock (_gate)
        {
            if (proposed.Revision != _settings.Revision) throw new InvalidOperationException("Health settings changed. Refresh before saving.");
            var updated = proposed with { Revision = checked(_settings.Revision + 1) };
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(updated, ProtocolJsonContext.Default.RuntimeHealthSettings));
                File.Move(temporary, _path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            _settings = updated;
            ResetExport();
            return updated;
        }
    }

    internal void Clear()
    {
        lock (_gate) { _history.Clear(); _traces.Clear(); _metrics.Clear(); ResetExport(); _status = "Collected diagnostics cleared."; }
    }

    internal DiagnosticActionResult Terminate(TerminateDiagnosticProcessRequest request)
    { lock (_samplingGate) return _processes.Terminate(request); }

    private static RuntimeHealthSettings Validate(RuntimeHealthSettings settings)
    {
        if (settings.Revision < 0 || settings.BackgroundProfile is not ("balanced" or "performance" or "battery-saver" or "custom") ||
            !double.IsFinite(settings.TraceSampleRate) || settings.TraceSampleRate is < 0 or > 1)
            throw new ArgumentException("Choose a valid background profile and a trace sampling rate between zero and one.");
        static string? Endpoint(string? value)
        {
            var endpoint = value?.Trim();
            if (!string.IsNullOrEmpty(endpoint) && (endpoint.Length > 2048 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
                !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback)))
                throw new ArgumentException("Use an HTTPS OTLP endpoint, or HTTP on localhost, without credentials, query or fragment.");
            return string.IsNullOrEmpty(endpoint) ? null : endpoint;
        }
        return settings with { OtlpEndpoint = Endpoint(settings.OtlpEndpoint), OtlpMetricsEndpoint = Endpoint(settings.OtlpMetricsEndpoint) };
    }

    private void Sample()
    {
        // Native process enumeration must not hold the telemetry/policy lock:
        // browser heartbeat RPCs also record operation diagnostics.
        if (!System.Threading.Monitor.TryEnter(_samplingGate)) return;
        try
        {
            var power = WindowsPowerState.Read();
            var processes = _processes.Capture();
            lock (_gate)
            {
                _power = power; _latest = processes;
                _history.Enqueue(new(DateTimeOffset.UtcNow, _latest));
                while (_history.Count > 120) _history.Dequeue();
                _status = _latest.Count >= 256 ? "Process snapshot limited to 256 owned processes." : "Up to 120 resource samples; first CPU samples are unknown.";
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        { lock (_gate) _status = "Resource sampling is temporarily unavailable: " + error.GetType().Name; }
        finally { System.Threading.Monitor.Exit(_samplingGate); }
    }

    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                var power = WindowsPowerState.Read();
                bool sample;
                lock (_gate) { _power = power; sample = Activity.Evaluate(_settings, _power).RunDiagnostics; }
                if (sample) Sample();
                await FlushExportAsync(_shutdown.Token).ConfigureAwait(false);
                await FlushMetricsAsync(_shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    internal OperationScope Begin(string operation)
    {
        lock (_gate) return new(this, operation, _settings.MetricsEnabled, _settings.TracingEnabled && Random.Shared.NextDouble() < _settings.TraceSampleRate);
    }

    internal sealed class OperationScope : IDisposable
    {
        private readonly RuntimeHealthService _owner;
        private readonly string _operation;
        private readonly bool _metric;
        private readonly Activity? _activity;
        private readonly long _start = Stopwatch.GetTimestamp();
        private readonly DateTimeOffset _utc = DateTimeOffset.UtcNow;
        public string Outcome { get; set; } = "ok";
        internal OperationScope(RuntimeHealthService owner, string operation, bool metric, bool trace)
        {
            _owner = owner; _operation = operation; _metric = metric;
            if (trace) _activity = Activities.StartActivity(operation, ActivityKind.Server) ?? new Activity(operation).SetIdFormat(ActivityIdFormat.W3C).Start();
        }
        public void Dispose()
        {
            var duration = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            _activity?.SetStatus(Outcome == "ok" ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
            _activity?.Dispose();
            if (_metric) Durations.Record(duration, new KeyValuePair<string, object?>("rpc.method", _operation), new("outcome", Outcome));
            lock (_owner._gate)
            {
                if (_metric && _owner._settings.MetricsEnabled && (_owner._metrics.ContainsKey(_operation) || _owner._metrics.Count < 256))
                {
                    var old = _owner._metrics.GetValueOrDefault(_operation) ?? new(_operation, 0, 0, 0, 0);
                    _owner._metrics[_operation] = old with { Count = old.Count + 1, Failures = old.Failures + (Outcome == "ok" ? 0 : 1),
                        TotalMilliseconds = old.TotalMilliseconds + duration, MaximumMilliseconds = Math.Max(old.MaximumMilliseconds, duration) };
                }
                if (_activity is not null && _owner._settings.TracingEnabled)
                {
                    var trace = new OperationTrace(_activity.TraceId.ToString(), _activity.SpanId.ToString(), _operation, _utc, duration, Outcome);
                    _owner._traces.Enqueue(trace);
                    while (_owner._traces.Count > 512) _owner._traces.Dequeue();
                    _owner.EnqueueExport(trace);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _monitor.ConfigureAwait(false);
        DisposeExport();
        _shutdown.Dispose();
    }
}
