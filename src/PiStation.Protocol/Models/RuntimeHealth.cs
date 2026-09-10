namespace PiStation.Protocol.Models;

public sealed record RuntimeHealthSettings(long Revision = 0, string BackgroundProfile = "balanced",
    bool PauseWhenHostLocked = true, bool PauseWhenHostLowPower = true, bool PauseWhenClientLowPower = true,
    bool PauseWhenOnBattery = false, bool TracingEnabled = false, bool MetricsEnabled = true,
    double TraceSampleRate = 1, string? OtlpEndpoint = null, string? OtlpMetricsEndpoint = null);

public sealed record PowerState(bool? Locked, bool? OnBattery, bool? LowPower, DateTimeOffset CapturedUtc);
public sealed record ClientActivityReport(bool Visible, bool Focused, bool DiagnosticsVisible, bool? OnBattery, bool? LowPower);
public static class BackgroundActivityRules
{
    public static bool IsClientEligible(RuntimeHealthSettings settings, ClientActivityReport report) =>
        report.Visible && (settings.BackgroundProfile == "performance" || report.Focused) &&
        !(settings.PauseWhenClientLowPower && report.LowPower == true) &&
        !(settings.PauseWhenOnBattery && report.OnBattery == true);
}
public sealed record BackgroundPolicySnapshot(RuntimeHealthSettings Settings, PowerState HostPower,
    int ActiveClients, bool RunBackgroundRefresh, bool RunDiagnostics, string Reason, DateTimeOffset CapturedUtc);

public sealed record ProcessResourceSample(int ProcessId, long StartedUtcTicks, int? ParentProcessId,
    string Name, string Kind, string OwnerId, int Depth, double? CpuPercent, long WorkingSetBytes,
    long PrivateMemoryBytes, bool CanTerminate);
public sealed record ResourceHistorySample(DateTimeOffset CapturedUtc, IReadOnlyList<ProcessResourceSample> Processes);
public sealed record TerminateDiagnosticProcessRequest(int ProcessId, long StartedUtcTicks);
public sealed record DiagnosticActionResult(bool Succeeded, string Message);
public sealed record OperationTrace(string TraceId, string SpanId, string Operation, DateTimeOffset StartedUtc,
    double DurationMilliseconds, string Outcome);
public sealed record OperationMetric(string Operation, long Count, long Failures, double TotalMilliseconds, double MaximumMilliseconds);
public sealed record RuntimeHealthSnapshot(BackgroundPolicySnapshot Background, IReadOnlyList<ProcessResourceSample> Processes,
    IReadOnlyList<ResourceHistorySample> History, IReadOnlyList<OperationTrace> Traces,
    IReadOnlyList<OperationMetric> Metrics, string Status, string ExportStatus);
