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
    int PendingMessageCount,
    string SteeringMode,
    string FollowUpMode,
    string? SessionName = null,
    bool AutoCompactionEnabled = true,
    bool? ReportedAutoCompactionEnabled = null);

public sealed record PiModelInfo(
    string ProviderId,
    string ModelId,
    string DisplayName,
    bool SupportsReasoning,
    int? ContextWindow);

public sealed record PiSessionEntries(IReadOnlyList<JsonElement> Entries, string? LeafId);

public sealed record PiClearedMessages(IReadOnlyList<string> Steering, IReadOnlyList<string> FollowUp);

public sealed record PiSessionMutation(bool Cancelled);

public sealed record PiCommandInfo(
    string Name,
    string? Description,
    string Source,
    string? Location,
    string? Path,
    PiCommandSourceInfo? SourceInfo = null);

public sealed record PiCommandSourceInfo(string? Path, string? Source, string? Scope, string? Origin, string? BaseDir);

public sealed record PiCompactionUsage(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long TotalTokens,
    decimal? TotalCost);

public sealed record PiCompactionResult(
    string Summary,
    string? FirstKeptEntryId,
    long TokensBefore,
    long? EstimatedTokensAfter,
    PiCompactionUsage? Usage);
