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
    int? ContextWindow = null);

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
