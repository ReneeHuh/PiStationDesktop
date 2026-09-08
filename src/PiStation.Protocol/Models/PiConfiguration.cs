using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

public enum PiThinkingLevel
{
    Off,
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
    Max,
}

public sealed record PiModelSelection(
    string ProviderId,
    string ModelId);

public sealed record PiModelCapability(
    string ProviderId,
    string ModelId,
    string DisplayName,
    bool SupportsReasoning,
    int? ContextWindow = null,
    IReadOnlyList<PiThinkingLevel>? SupportedThinkingLevels = null);

public static class PiPermissionModes
{
    public static bool IsSupported(string? mode) => mode is null or "supervised" or "auto-accept-edits" or "auto" or "full-access";
    public static IReadOnlyList<PiRuntimeModeCapability> Capabilities { get; } =
    [new("supervised", "Supervised"), new("auto-accept-edits", "Auto-accept edits"),
     new("auto", "Auto (ask when review unavailable)"), new("full-access", "Full access")];
}

public sealed record PiRuntimeModeCapability(
    string RuntimeModeId,
    string DisplayName);

public sealed record PiConfigurationCapabilities(
    IReadOnlyList<PiModelCapability> Models,
    IReadOnlyList<PiThinkingLevel> ThinkingLevels,
    IReadOnlyList<PiRuntimeModeCapability> RuntimeModes);

public sealed record ThreadPiConfiguration(
    EnvironmentId EnvironmentId,
    ThreadId ThreadId,
    PiModelSelection? Model,
    PiThinkingLevel? ThinkingLevel,
    string? RuntimeModeId,
    long Revision,
    DateTimeOffset UpdatedUtc);

public sealed record ThreadPiConfigurationSnapshot(
    ThreadPiConfiguration Configuration,
    PiConfigurationCapabilities Capabilities,
    PiModelSelection? ActiveModel,
    PiThinkingLevel ActiveThinkingLevel,
    string? ActiveRuntimeModeId);
