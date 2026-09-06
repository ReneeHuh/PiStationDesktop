using System.Text.Json;

namespace PiStation.PiRpc.Wire.Events;

public abstract record PiRpcEvent(string Type);

public sealed record PiAgentStartedEvent() : PiRpcEvent("agent_start");

public sealed record PiAgentEndedEvent(bool WillRetry) : PiRpcEvent("agent_end");

public sealed record PiAgentSettledEvent() : PiRpcEvent("agent_settled");

public sealed record PiTurnStartedEvent() : PiRpcEvent("turn_start");

public sealed record PiTurnEndedEvent(JsonElement Message, JsonElement ToolResults) : PiRpcEvent("turn_end");

public sealed record PiMessageStartedEvent(JsonElement Message) : PiRpcEvent("message_start");

public sealed record PiMessageCompletedEvent(
    JsonElement Message,
    PiTokenUsage? Usage = null) : PiRpcEvent("message_end");

public sealed record PiMessageUpdateEvent(PiAssistantDelta Delta) : PiRpcEvent("message_update");

public sealed record PiToolExecutionStartedEvent(
    string ToolCallId,
    string ToolName,
    JsonElement Arguments) : PiRpcEvent("tool_execution_start");

public sealed record PiToolExecutionUpdatedEvent(
    string ToolCallId,
    string ToolName,
    JsonElement Arguments,
    JsonElement PartialResult) : PiRpcEvent("tool_execution_update");

public sealed record PiToolExecutionCompletedEvent(
    string ToolCallId,
    string ToolName,
    JsonElement Result,
    bool IsError) : PiRpcEvent("tool_execution_end");

public sealed record PiAutoRetryStartedEvent(
    int Attempt,
    int MaximumAttempts,
    int DelayMilliseconds,
    string ErrorMessage) : PiRpcEvent("auto_retry_start");

public sealed record PiAutoRetryCompletedEvent(
    bool Success,
    int Attempt,
    string? FinalError) : PiRpcEvent("auto_retry_end");

public sealed record PiQueueUpdatedEvent(
    IReadOnlyList<string> Steering,
    IReadOnlyList<string> FollowUp) : PiRpcEvent("queue_update");

public sealed record PiCompactionStartedEvent(string Reason) : PiRpcEvent("compaction_start");

public sealed record PiCompactionCompletedEvent(
    string Reason,
    JsonElement? Result,
    bool Aborted,
    bool WillRetry,
    string? ErrorMessage) : PiRpcEvent("compaction_end");

public abstract record PiExtensionUiRequestEvent(
    string RequestId,
    string Method,
    string Title,
    int? TimeoutMilliseconds) : PiRpcEvent("extension_ui_request");

public sealed record PiConfirmRequestedEvent(
    string RequestId,
    string Title,
    string Message,
    int? TimeoutMilliseconds) : PiExtensionUiRequestEvent(RequestId, "confirm", Title, TimeoutMilliseconds);

public sealed record PiSelectRequestedEvent(
    string RequestId,
    string Title,
    IReadOnlyList<string> Options,
    int? TimeoutMilliseconds) : PiExtensionUiRequestEvent(RequestId, "select", Title, TimeoutMilliseconds);

public sealed record PiInputRequestedEvent(
    string RequestId,
    string Title,
    string? Placeholder,
    int? TimeoutMilliseconds) : PiExtensionUiRequestEvent(RequestId, "input", Title, TimeoutMilliseconds);

public sealed record PiEditorRequestedEvent(
    string RequestId,
    string Title,
    string? Prefill) : PiExtensionUiRequestEvent(RequestId, "editor", Title, null);

public sealed record PiUnknownEvent(string EventType, JsonElement Payload) : PiRpcEvent(EventType);

/// <summary>A host-requested marker queued after Pi confirms a command completed without a running agent turn.</summary>
public sealed record PiIdlePromptCompletedEvent(string Tag) : PiRpcEvent("pistation_idle_prompt_completed");

public sealed record PiExtensionUiUpdateEvent(string RequestId, string Method, string? Key = null,
    string? Text = null, IReadOnlyList<string>? Lines = null, string? Placement = null, string? Severity = null)
    : PiRpcEvent("extension_ui_request");

public abstract record PiAssistantDelta(string Type, int ContentIndex);

public sealed record PiTextStartedDelta(int ContentIndex) : PiAssistantDelta("text_start", ContentIndex);

public sealed record PiTextDelta(int ContentIndex, string Delta) : PiAssistantDelta("text_delta", ContentIndex);

public sealed record PiTextCompletedDelta(int ContentIndex, string Content) : PiAssistantDelta("text_end", ContentIndex);

public sealed record PiThinkingStartedDelta(int ContentIndex) : PiAssistantDelta("thinking_start", ContentIndex);

public sealed record PiThinkingDelta(int ContentIndex, string Delta) : PiAssistantDelta("thinking_delta", ContentIndex);

public sealed record PiThinkingCompletedDelta(int ContentIndex, string Content) : PiAssistantDelta("thinking_end", ContentIndex);

public sealed record PiToolCallStartedDelta(
    int ContentIndex,
    string ToolCallId,
    string ToolName) : PiAssistantDelta("toolcall_start", ContentIndex);

public sealed record PiToolCallArgumentsDelta(
    int ContentIndex,
    string Delta) : PiAssistantDelta("toolcall_delta", ContentIndex);

public sealed record PiToolCallCompletedDelta(
    int ContentIndex,
    JsonElement ToolCall) : PiAssistantDelta("toolcall_end", ContentIndex);

public sealed record PiUnknownAssistantDelta(
    string DeltaType,
    int ContentIndex,
    JsonElement Payload) : PiAssistantDelta(DeltaType, ContentIndex);

public sealed record PiTokenUsage(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long? ReasoningTokens,
    long TotalTokens,
    decimal? TotalCost = null)
{
    public long ContextTokens => TotalTokens > 0
        ? TotalTokens
        : AddSaturated(AddSaturated(InputTokens, OutputTokens), AddSaturated(CacheReadTokens, CacheWriteTokens));

    private static long AddSaturated(long left, long right) => left > long.MaxValue - right
        ? long.MaxValue
        : left + right;
}
