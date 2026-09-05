using System.Text.Json.Serialization;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;

namespace PiStation.Protocol.Commands;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ThreadStartTurnCommand), "threadStartTurn")]
[JsonDerivedType(typeof(ThreadStopTurnCommand), "threadStopTurn")]
[JsonDerivedType(typeof(ThreadRestartRuntimeCommand), "threadRestartRuntime")]
[JsonDerivedType(typeof(ThreadRespondToApprovalCommand), "threadRespondToApproval")]
[JsonDerivedType(typeof(ThreadAnswerQuestionCommand), "threadAnswerQuestion")]
[JsonDerivedType(typeof(ThreadCancelInteractionCommand), "threadCancelInteraction")]
[JsonDerivedType(typeof(ThreadSaveDraftCommand), "threadSaveDraft")]
[JsonDerivedType(typeof(ThreadAddDraftAttachmentCommand), "threadAddDraftAttachment")]
[JsonDerivedType(typeof(ThreadRemoveDraftAttachmentCommand), "threadRemoveDraftAttachment")]
[JsonDerivedType(typeof(ThreadClearDraftCommand), "threadClearDraft")]
[JsonDerivedType(typeof(ThreadUpdatePiConfigurationCommand), "threadUpdatePiConfiguration")]
[JsonDerivedType(typeof(ThreadRenameCommand), "threadRename")]
[JsonDerivedType(typeof(ThreadSetArchivedCommand), "threadSetArchived")]
[JsonDerivedType(typeof(ThreadSetPinnedCommand), "threadSetPinned")]
[JsonDerivedType(typeof(ThreadRevertCheckpointCommand), "threadRevertCheckpoint")]
public abstract record ThreadCommand;

public sealed record ThreadStartTurnCommand(
    string Prompt,
    DraftId? DraftId = null,
    long? DraftRevision = null,
    IReadOnlyList<AttachmentId>? AttachmentIds = null) : ThreadCommand;

public sealed record ThreadStopTurnCommand : ThreadCommand;

public sealed record ThreadRestartRuntimeCommand : ThreadCommand;

public sealed record ThreadRespondToApprovalCommand(
    InteractionId InteractionId,
    ApprovalDecision Decision) : ThreadCommand;

public sealed record ThreadAnswerQuestionCommand(
    InteractionId InteractionId,
    string Answer) : ThreadCommand;

public sealed record ThreadCancelInteractionCommand(InteractionId InteractionId) : ThreadCommand;

public sealed record ThreadSaveDraftCommand(
    DraftId DraftId,
    long ExpectedRevision,
    string Text) : ThreadCommand;

public sealed record ThreadAddDraftAttachmentCommand(
    DraftId DraftId,
    AttachmentId AttachmentId,
    long ExpectedRevision,
    string FileName,
    string MediaType,
    long ByteLength,
    string Sha256,
    string ServerPath) : ThreadCommand;

public sealed record ThreadRemoveDraftAttachmentCommand(
    DraftId DraftId,
    AttachmentId AttachmentId,
    long ExpectedRevision) : ThreadCommand;

public sealed record ThreadClearDraftCommand(
    DraftId DraftId,
    long ExpectedRevision,
    IReadOnlyList<AttachmentId> ExpectedAttachmentIds) : ThreadCommand;

public sealed record ThreadUpdatePiConfigurationCommand(
    long ExpectedRevision,
    PiModelSelection? Model,
    PiThinkingLevel? ThinkingLevel,
    string? RuntimeModeId) : ThreadCommand;

public sealed record ThreadRenameCommand(
    long ExpectedRevision,
    string Title) : ThreadCommand;

public sealed record ThreadSetArchivedCommand(
    long ExpectedRevision,
    bool IsArchived) : ThreadCommand;

public sealed record ThreadSetPinnedCommand(
    long ExpectedRevision,
    bool IsPinned) : ThreadCommand;

public sealed record ThreadRevertCheckpointCommand(int TurnCount) : ThreadCommand;

public sealed record ExecuteThreadCommandRequest(
    int ProtocolVersion,
    EnvironmentId EnvironmentId,
    ClientId ClientId,
    CommandId CommandId,
    ThreadId ThreadId,
    ProjectionEpoch? ExpectedProjectionEpoch,
    TurnId? ExpectedTurnId,
    ThreadCommand Command);
