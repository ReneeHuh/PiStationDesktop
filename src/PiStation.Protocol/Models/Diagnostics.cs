namespace PiStation.Protocol.Models;

public enum SettingsSection
{
    Projects,
    PiRuntime,
    SourceControl,
    Appearance,
    Integrations,
    Diagnostics,
    Updates,
    Usage,
}

public sealed record RuntimeDiagnostic(
    string Name,
    string State,
    string Detail,
    bool IsSensitive = false);

public sealed record ResourceTelemetry(
    DateTimeOffset CapturedUtc,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    double TotalProcessorTimeSeconds,
    int ProcessThreadCount,
    long DatabaseBytes,
    int ActiveThreadRuntimes,
    int ActiveTerminalSessions);

public sealed record UsageBreakdown(
    string Provider,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheTokens,
    long TotalTokens,
    decimal? EstimatedCost);

public sealed record UsageSummary(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    long TotalTokens,
    decimal? EstimatedCost,
    IReadOnlyList<UsageBreakdown> Breakdown,
    string QuotaState,
    string QuotaDetail);

public sealed record DiagnosticsSnapshot(
    IReadOnlyList<RuntimeDiagnostic> Diagnostics,
    ResourceTelemetry Resources,
    UsageSummary Usage,
    IReadOnlyList<string> RecentLogs,
    string ApplicationVersion,
    string ProtocolVersion,
    string UpdateState);

public sealed record ExportDiagnosticsRequest(string DestinationPath);

public sealed record ExportDiagnosticsResult(string Path, long ByteLength, DateTimeOffset CreatedUtc);

public sealed record DiagnosticsDownload(byte[] Content, DateTimeOffset CreatedUtc);
