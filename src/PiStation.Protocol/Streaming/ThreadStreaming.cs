using System.Text.Json.Serialization;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;

namespace PiStation.Protocol.Streaming;

public sealed record ThreadCursor(ProjectionEpoch ProjectionEpoch, Sequence Sequence);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(RuntimeStateChangedEvent), "runtimeStateChanged")]
[JsonDerivedType(typeof(TurnStartedEvent), "turnStarted")]
[JsonDerivedType(typeof(MessageStartedEvent), "messageStarted")]
[JsonDerivedType(typeof(ContentDeltaEvent), "contentDelta")]
[JsonDerivedType(typeof(MessageCompletedEvent), "messageCompleted")]
[JsonDerivedType(typeof(ToolStartedEvent), "toolStarted")]
[JsonDerivedType(typeof(ToolOutputReplacedEvent), "toolOutputReplaced")]
[JsonDerivedType(typeof(ToolCompletedEvent), "toolCompleted")]
[JsonDerivedType(typeof(TurnSettledEvent), "turnSettled")]
[JsonDerivedType(typeof(RuntimeFailedEvent), "runtimeFailed")]
[JsonDerivedType(typeof(UnknownRuntimeEvent), "unknownRuntimeEvent")]
[JsonDerivedType(typeof(ApprovalRequestedEvent), "approvalRequested")]
[JsonDerivedType(typeof(QuestionRequestedEvent), "questionRequested")]
[JsonDerivedType(typeof(InteractionResolvedEvent), "interactionResolved")]
[JsonDerivedType(typeof(CheckpointCapturedEvent), "checkpointCaptured")]
public abstract record ThreadEvent;

public sealed record RuntimeStateChangedEvent(ThreadRuntimeState State) : ThreadEvent;

public sealed record TurnStartedEvent(TurnId TurnId, string Prompt) : ThreadEvent;

public sealed record MessageStartedEvent(MessageProjection Message) : ThreadEvent;

public sealed record ContentDeltaEvent(
    string MessageId,
    int ContentIndex,
    ContentKind ContentKind,
    string Delta) : ThreadEvent;

public sealed record MessageCompletedEvent(MessageProjection Message) : ThreadEvent;

public sealed record ToolStartedEvent(ToolProjection Tool) : ThreadEvent;

public sealed record ToolOutputReplacedEvent(string ToolCallId, string OutputPreview) : ThreadEvent;

public sealed record ToolCompletedEvent(string ToolCallId, string OutputPreview, ToolExecutionState State) : ThreadEvent;

public sealed record TurnSettledEvent(TurnId TurnId, TurnMetrics? Metrics = null) : ThreadEvent;

public sealed record RuntimeFailedEvent(ProtocolError Error) : ThreadEvent;

public sealed record UnknownRuntimeEvent(string RuntimeType, string Preview) : ThreadEvent;

public sealed record ApprovalRequestedEvent(
    InteractionId InteractionId,
    string Title,
    string Message,
    int? TimeoutMilliseconds) : ThreadEvent;

public sealed record QuestionRequestedEvent(
    InteractionId InteractionId,
    QuestionInputKind InputKind,
    string Title,
    string? Prompt,
    IReadOnlyList<string> Options,
    string? InitialValue,
    int? TimeoutMilliseconds) : ThreadEvent;

public sealed record InteractionResolvedEvent(
    InteractionId InteractionId,
    InteractionState State,
    ApprovalDecision? ApprovalDecision = null,
    string? Answer = null) : ThreadEvent;

public sealed record CheckpointCapturedEvent(ThreadCheckpoint Checkpoint) : ThreadEvent;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ThreadSnapshotEnvelope), "snapshot")]
[JsonDerivedType(typeof(ThreadEventEnvelope), "event")]
[JsonDerivedType(typeof(ThreadResyncRequiredEnvelope), "resyncRequired")]
[JsonDerivedType(typeof(ThreadSynchronizedEnvelope), "synchronized")]
public abstract record ThreadEnvelope(
    EnvironmentId EnvironmentId,
    ThreadId ThreadId,
    ProjectionEpoch ProjectionEpoch,
    Sequence Sequence);

public sealed record ThreadSnapshotEnvelope(ThreadProjection Projection) : ThreadEnvelope(
    Projection.EnvironmentId,
    Projection.ThreadId,
    Projection.ProjectionEpoch,
    Projection.Sequence);

public sealed record ThreadEventEnvelope(
    EnvironmentId EnvironmentId,
    ThreadId ThreadId,
    ProjectionEpoch ProjectionEpoch,
    Sequence Sequence,
    ThreadEvent Event) : ThreadEnvelope(EnvironmentId, ThreadId, ProjectionEpoch, Sequence);

public sealed record ThreadResyncRequiredEnvelope(
    EnvironmentId EnvironmentId,
    ThreadId ThreadId,
    ProjectionEpoch ProjectionEpoch,
    Sequence Sequence,
    string Reason) : ThreadEnvelope(EnvironmentId, ThreadId, ProjectionEpoch, Sequence);

public sealed record ThreadSynchronizedEnvelope(EnvironmentId EnvironmentId, ThreadId ThreadId,
    ProjectionEpoch ProjectionEpoch, Sequence Sequence) : ThreadEnvelope(EnvironmentId, ThreadId, ProjectionEpoch, Sequence);
