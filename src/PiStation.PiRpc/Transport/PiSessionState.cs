using System.Text.Json;

namespace PiStation.PiRpc.Transport;

public sealed record PiSessionState(
    string SessionId,
    string? SessionFile,
    PiModelInfo? Model,
    string ThinkingLevel,
    bool IsStreaming,
    bool IsCompacting,
    int MessageCount,
    int PendingMessageCount);

public sealed record PiModelInfo(
    string ProviderId,
    string ModelId,
    string DisplayName,
    bool SupportsReasoning,
    int? ContextWindow);

public sealed record PiSessionEntries(IReadOnlyList<JsonElement> Entries, string? LeafId);

public sealed record PiClearedMessages(IReadOnlyList<string> Steering, IReadOnlyList<string> FollowUp);

public sealed record PiSessionMutation(bool Cancelled);
