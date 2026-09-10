using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed record DiagnosticProcessRow(ProcessResourceSample Sample)
{
    public string Heading => new string(' ', Math.Min(Sample.Depth, 12) * 2) + $"{Sample.Name} · PID {Sample.ProcessId} · {Sample.Kind}";
    public string Resources => $"CPU {(Sample.CpuPercent is { } cpu ? cpu.ToString("F1", System.Globalization.CultureInfo.CurrentCulture) + "%" : "unknown")} · memory {Sample.WorkingSetBytes / 1048576d:F1} MiB";
}

public sealed class RuntimeHealthViewModel : ObservableObject
{
    private RuntimeHealthSettings _saved = new();
    private bool _dirty;
    private bool _loading;
    public RuntimeHealthSettings Saved => _saved;
    public bool IsDirty => _dirty;
    public IReadOnlyList<string> Profiles { get; } = ["balanced", "performance", "battery-saver", "custom"];
    public ObservableCollection<DiagnosticProcessRow> Processes { get; } = [];
    public ObservableCollection<string> Metrics { get; } = [];
    public ObservableCollection<string> Traces { get; } = [];
    private DiagnosticProcessRow? _selected;
    public DiagnosticProcessRow? SelectedProcess { get => _selected; set { if (SetProperty(ref _selected, value)) OnPropertyChanged(nameof(CanTerminate)); } }
    public bool CanTerminate => SelectedProcess?.Sample.CanTerminate == true;
    private string _status = "Refresh diagnostics to inspect this host.";
    public string Status { get => _status; internal set => SetProperty(ref _status, value); }
    private string _policy = "Background state is unknown.";
    public string Policy { get => _policy; private set => SetProperty(ref _policy, value); }
    private string _history = "No resource samples yet.";
    public string HistorySummary { get => _history; private set => SetProperty(ref _history, value); }
    private PointCollection _cpu = [], _memory = [];
    public PointCollection CpuPoints { get => _cpu; private set => SetProperty(ref _cpu, value); }
    public PointCollection MemoryPoints { get => _memory; private set => SetProperty(ref _memory, value); }
    private string _profile = "balanced", _endpoint = "", _metricsEndpoint = "";
    private bool _locked = true, _hostLow = true, _clientLow = true, _battery, _tracing, _metrics = true;
    private double _sampleRate = 1;
    private void Edit() { if (!_loading) { _dirty = true; OnPropertyChanged(nameof(IsDirty)); } }
    public string Profile { get => _profile; set { if (SetProperty(ref _profile, value)) Edit(); } }
    public bool PauseLocked { get => _locked; set { if (SetProperty(ref _locked, value)) Edit(); } }
    public bool PauseHostLowPower { get => _hostLow; set { if (SetProperty(ref _hostLow, value)) Edit(); } }
    public bool PauseClientLowPower { get => _clientLow; set { if (SetProperty(ref _clientLow, value)) Edit(); } }
    public bool PauseBattery { get => _battery; set { if (SetProperty(ref _battery, value)) Edit(); } }
    public bool TracingEnabled { get => _tracing; set { if (SetProperty(ref _tracing, value)) Edit(); } }
    public bool MetricsEnabled { get => _metrics; set { if (SetProperty(ref _metrics, value)) Edit(); } }
    public double SampleRate { get => _sampleRate; set { if (SetProperty(ref _sampleRate, value)) Edit(); } }
    public string OtlpEndpoint { get => _endpoint; set { if (SetProperty(ref _endpoint, value)) Edit(); } }
    public string OtlpMetricsEndpoint { get => _metricsEndpoint; set { if (SetProperty(ref _metricsEndpoint, value)) Edit(); } }

    internal void ApplyPreset()
    {
        if (Profile == "custom") return;
        PauseLocked = true;
        PauseHostLowPower = PauseClientLowPower = Profile != "performance";
        PauseBattery = Profile == "battery-saver";
    }
    internal RuntimeHealthSettings CreateSettings() => new(_saved.Revision, Profile, PauseLocked, PauseHostLowPower, PauseClientLowPower,
        PauseBattery, TracingEnabled, MetricsEnabled, SampleRate, OtlpEndpoint, OtlpMetricsEndpoint);
    internal void ApplySettings(RuntimeHealthSettings settings)
    {
        _loading = true;
        _saved = settings;
        Profile = settings.BackgroundProfile; PauseLocked = settings.PauseWhenHostLocked;
        PauseHostLowPower = settings.PauseWhenHostLowPower; PauseClientLowPower = settings.PauseWhenClientLowPower;
        PauseBattery = settings.PauseWhenOnBattery; TracingEnabled = settings.TracingEnabled; MetricsEnabled = settings.MetricsEnabled;
        SampleRate = settings.TraceSampleRate; OtlpEndpoint = settings.OtlpEndpoint ?? "";
        OtlpMetricsEndpoint = settings.OtlpMetricsEndpoint ?? "";
        _loading = false; _dirty = false; OnPropertyChanged(nameof(IsDirty));
    }
    internal void ApplyPolicy(BackgroundPolicySnapshot snapshot)
    {
        static string State(bool? value) => value switch { true => "yes", false => "no", _ => "unknown" };
        Policy = $"{snapshot.Reason}. Host locked: {State(snapshot.HostPower.Locked)}; battery: {State(snapshot.HostPower.OnBattery)}; battery saver: {State(snapshot.HostPower.LowPower)}. Active clients: {snapshot.ActiveClients}.";
        if (!_dirty) ApplySettings(snapshot.Settings);
    }
    internal void Apply(RuntimeHealthSnapshot snapshot)
    {
        ApplyPolicy(snapshot.Background);
        Status = snapshot.Status + " " + snapshot.ExportStatus;
        var selected = SelectedProcess?.Sample;
        Processes.Clear(); foreach (var process in snapshot.Processes) Processes.Add(new(process));
        SelectedProcess = Processes.FirstOrDefault(row => selected is not null && row.Sample.ProcessId == selected.ProcessId && row.Sample.StartedUtcTicks == selected.StartedUtcTicks);
        Metrics.Clear(); foreach (var metric in snapshot.Metrics.Take(100)) Metrics.Add($"{metric.Operation}: {metric.Count} calls, {metric.Failures} failed/canceled · mean {metric.TotalMilliseconds / Math.Max(1, metric.Count):F1} ms · max {metric.MaximumMilliseconds:F1} ms");
        Traces.Clear(); foreach (var trace in snapshot.Traces.TakeLast(50).Reverse()) Traces.Add($"{trace.StartedUtc.LocalDateTime:T} · {trace.Operation} · {trace.Outcome} · {trace.DurationMilliseconds:F1} ms · {trace.TraceId}");
        var history = snapshot.History;
        var memory = history.Select(item => item.Processes.Sum(process => process.WorkingSetBytes) / 1048576d).ToArray();
        var cpu = history.Select(item => item.Processes.Sum(process => process.CpuPercent ?? 0)).ToArray();
        CpuPoints = Points(cpu, 100); MemoryPoints = Points(memory, Math.Max(1, memory.DefaultIfEmpty(1).Max()));
        HistorySummary = $"{history.Count} samples · combined host and owned-process CPU (0–100%) and memory · peak {(memory.Length > 0 ? memory.Max() : 0):F1} MiB. Samples are spaced by collection order; background pauses omit samples. Unknown initial CPU plots at zero.";
    }
    private static PointCollection Points(double[] values, double maximum)
    {
        var points = new PointCollection();
        for (var i = 0; i < values.Length; i++) points.Add(new(i * 400d / Math.Max(1, values.Length - 1), 76 - Math.Clamp(values[i] / maximum, 0, 1) * 72));
        return points;
    }
    internal void Clear()
    {
        ApplySettings(new()); Processes.Clear(); Metrics.Clear(); Traces.Clear(); SelectedProcess = null;
        CpuPoints = []; MemoryPoints = []; Policy = "Background state is unknown."; HistorySummary = "No resource samples yet.";
    }
}
