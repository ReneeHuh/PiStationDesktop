using System.Text.Json.Serialization;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Protocol.Projections;

public enum ThreadRuntimeState
{
    Stopped,
    Starting,
    Hydrating,
    Ready,
    Running,
    Stopping,
    Crashed,
}

public enum MessageRole
{
    User,
    Assistant,
    System,
    Tool,
}

public enum ContentKind
{
    Text,
    Thinking,
}

public enum ToolExecutionState
{
    Running,
    Completed,
    Failed,
}

public enum TimelineActivityState
{
    Informational,
    Running,
    Completed,
    Failed,
}

public enum TurnBoundaryKind
{
    Started,
    Settled,
}

public enum InteractionState
{
    Pending,
    Approved,
    Answered,
    Rejected,
    Canceled,
    TimedOut,
}

public enum ApprovalDecision
{
    Approve,
    Reject,
}

public enum QuestionInputKind
{
    Select,
    Input,
    Editor,
}

public enum QueuedMessageKind
{
    Steering,
    FollowUp,
}

public enum QueueDeliveryMode
{
    OneAtATime,
    All,
}

public enum QueueDeliveryState
{
    Empty,
    Queued,
    Delivering,
    Cleared,
}

public enum AgentActivityKind
{
    Agent,
    Workflow,
}

public enum AgentActivityState
{
    Pending,
    Running,
    Waiting,
    Completed,
    Failed,
    Interrupted,
}

public enum ContextCompactionState
{
    Idle,
    Running,
    Completed,
    Failed,
    Interrupted,
}

public sealed record ContextCompactionProjection(
    ContextCompactionState State,
    string Reason,
    long? TokensBefore,
    long? EstimatedTokensAfter,
    decimal? Cost,
    string? Summary,
    string? Error,
    DateTimeOffset UpdatedUtc);

public sealed record QueuedMessageProjection(
    QueuedMessageKind Kind,
    int Position,
    string Text);

public sealed record ThreadQueueProjection(
    IReadOnlyList<QueuedMessageProjection> Messages,
    QueueDeliveryMode SteeringMode,
    QueueDeliveryMode FollowUpMode,
    QueueDeliveryState DeliveryState,
    int PendingMessageCount,
    DateTimeOffset UpdatedUtc);

public sealed record AgentActivityProjection(
    string ActivityId,
    TurnId? TurnId,
    string? ParentActivityId,
    AgentActivityKind Kind,
    AgentActivityState State,
    string Title,
    string Task,
    string CurrentActivity,
    DateTimeOffset StartedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? CompletedUtc,
    int ToolCount,
    TokenUsage? Usage,
    string? Model,
    string? ReasoningLevel,
    string? ResultSummary,
    string? FailureSummary,
    int? Step,
    int? AgentIndex,
    bool CanInterrupt,
    string? ControlId = null,
    string? Transcript = null,
    bool CanResume = false);

public sealed record MessageProjection(
    string MessageId,
    MessageRole Role,
    string Text,
    string Thinking,
    bool IsComplete,
    SentMessageContent? Content = null);

public sealed record ToolProjection(
    string ToolCallId,
    string ToolName,
    string ArgumentsPreview,
    string OutputPreview,
    ToolExecutionState State);

public sealed record TokenUsage(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long? ReasoningTokens,
    long TotalTokens);

public sealed record TurnMetrics(
    long? ElapsedMilliseconds,
    TokenUsage? Usage,
    long? ContextTokens,
    int? ContextWindow);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(MessageTimelineItem), "message")]
[JsonDerivedType(typeof(ThinkingTimelineItem), "thinking")]
[JsonDerivedType(typeof(ToolTimelineItem), "tool")]
[JsonDerivedType(typeof(StatusTimelineItem), "status")]
[JsonDerivedType(typeof(ErrorTimelineItem), "error")]
[JsonDerivedType(typeof(TurnBoundaryTimelineItem), "turnBoundary")]
[JsonDerivedType(typeof(ApprovalTimelineItem), "approval")]
[JsonDerivedType(typeof(QuestionTimelineItem), "question")]
public abstract record TimelineItem(string ItemId, TurnId? TurnId);

public sealed record MessageTimelineItem(
    string ItemId,
    TurnId? TurnId,
    string MessageId,
    MessageRole Role,
    string Text,
    bool IsComplete,
    SentMessageContent? Content = null) : TimelineItem(ItemId, TurnId);

public sealed record ThinkingTimelineItem(
    string ItemId,
    TurnId? TurnId,
    string MessageId,
    string Text,
    bool IsComplete) : TimelineItem(ItemId, TurnId);

public sealed record ToolTimelineItem(
    string ItemId,
    TurnId? TurnId,
    string ToolCallId,
    string ToolName,
    string ArgumentsPreview,
    string OutputPreview,
    ToolExecutionState State) : TimelineItem(ItemId, TurnId);

public sealed record StatusTimelineItem(
    string ItemId,
    TurnId? TurnId,
    string Text,
    TimelineActivityState State) : TimelineItem(ItemId, TurnId);

public sealed record ErrorTimelineItem(
    string ItemId,
    TurnId? TurnId,
    ProtocolError Error) : TimelineItem(ItemId, TurnId);

public sealed record TurnBoundaryTimelineItem(
    string ItemId,
    TurnId BoundaryTurnId,
    TurnBoundaryKind Boundary,
    TurnMetrics? Metrics = null) : TimelineItem(ItemId, BoundaryTurnId);

public sealed record ApprovalTimelineItem(
    string ItemId,
    TurnId? TurnId,
    InteractionId InteractionId,
    string Title,
    string Message,
    int? TimeoutMilliseconds,
    InteractionState State,
    ApprovalDecision? Decision) : TimelineItem(ItemId, TurnId);

public sealed record QuestionTimelineItem(
    string ItemId,
    TurnId? TurnId,
    InteractionId InteractionId,
    QuestionInputKind InputKind,
    string Title,
    string? Prompt,
    IReadOnlyList<string> Options,
    string? InitialValue,
    int? TimeoutMilliseconds,
    InteractionState State,
    string? Answer) : TimelineItem(ItemId, TurnId);

public sealed record ThreadProjection(
    EnvironmentId EnvironmentId,
    ThreadId ThreadId,
    ProjectionEpoch ProjectionEpoch,
    Sequence Sequence,
    ThreadRuntimeState RuntimeState,
    TurnId? CurrentTurnId,
    IReadOnlyList<TimelineItem> Timeline,
    IReadOnlyList<ThreadCheckpoint> Checkpoints,
    string PiSessionId,
    string? PiSessionFile,
    string? LastEntryId,
    ProtocolError? LastError,
    ThreadQueueProjection? Queue = null,
    IReadOnlyList<AgentActivityProjection>? AgentActivities = null,
    ContextCompactionProjection? Compaction = null,
    PiExtensionUiState? ExtensionUi = null,
    PiPlanState? Plan = null,
    PiAgentSetup? AgentSetup = null,
    long CompletionSequence = 0,
    PiShellExecution? ShellExecution = null)
{
    [JsonIgnore]
    public IReadOnlyList<MessageProjection> Messages
    {
        get
        {
            var thinkingByMessage = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var thinking in Timeline.OfType<ThinkingTimelineItem>())
            {
                thinkingByMessage[thinking.MessageId] = thinking.Text;
            }

            return Timeline
                .OfType<MessageTimelineItem>()
                .Select(message => new MessageProjection(
                    message.MessageId,
                    message.Role,
                    message.Text,
                    thinkingByMessage.GetValueOrDefault(message.MessageId, string.Empty),
                    message.IsComplete, message.Content))
                .ToArray();
        }
    }

    [JsonIgnore]
    public IReadOnlyList<ToolProjection> Tools => Timeline
        .OfType<ToolTimelineItem>()
        .Select(tool => new ToolProjection(
            tool.ToolCallId,
            tool.ToolName,
            tool.ArgumentsPreview,
            tool.OutputPreview,
            tool.State))
        .ToArray();
}
