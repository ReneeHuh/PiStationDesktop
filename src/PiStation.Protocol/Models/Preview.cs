using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

public static class PreviewDiscoveryDefaults
{
    public const int MaximumUrlLength = 2048;
    public const int MaximumHostLength = 255;
    public const int MaximumProcessNameLength = 260;
    public const int MaximumCandidatePorts = 128;
    public const int MaximumResults = 40;
    public const int MaximumProbeConcurrency = 16;
    public const int ProbeTimeoutMilliseconds = 750;
}

public sealed record DiscoverProjectPreviewServersRequest(ProjectId ProjectId, ThreadId? ThreadId = null);

public sealed record DiscoveredPreviewServer(
    string Url,
    string Host,
    int Port,
    string Scheme,
    string? ProcessName = null,
    int? ProcessId = null);

public sealed record DiscoverProjectPreviewServersResult(
    ProjectId ProjectId,
    DateTimeOffset ScannedAtUtc,
    IReadOnlyList<DiscoveredPreviewServer> Servers,
    bool IsTruncated);
