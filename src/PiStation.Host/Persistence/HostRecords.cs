using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.Host.Persistence;

public sealed record HostEnvironmentRecord(
    EnvironmentId EnvironmentId,
    string Name,
    DateTimeOffset CreatedUtc);

public sealed record HostThreadRecord(
    ThreadId ThreadId,
    ProjectId ProjectId,
    string PiSessionId,
    string? PiSessionFile,
    string Title,
    long Revision,
    bool IsArchived,
    bool IsPinned,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    ThreadWorkspaceMode WorkspaceMode = ThreadWorkspaceMode.Local,
    string? BranchName = null,
    string? WorktreePath = null,
    long WorkspaceGeneration = 0,
    SetupScriptState SetupScriptState = SetupScriptState.None,
    string? SetupScriptMessage = null)
{
    public ThreadDescriptor ToDescriptor(EnvironmentId environmentId) => new(
        environmentId,
        ThreadId,
        ProjectId,
        Title,
        PiSessionId,
        PiSessionFile,
        CreatedUtc,
        UpdatedUtc,
        Revision,
        IsArchived,
        IsPinned,
        WorkspaceMode,
        BranchName,
        WorktreePath,
        WorkspaceGeneration,
        SetupScriptState,
        SetupScriptMessage);
}

public sealed record StoredCommandReceipt(
    CommandReceipt Receipt,
    string BodyHash);

public sealed record ReceiptAcquisition(StoredCommandReceipt StoredReceipt, bool WasCreated);

public sealed record StoredWorkspaceCommandReceipt(
    WorkspaceCommandReceipt Receipt,
    string BodyHash,
    WorkspaceGitOperationResult? Result);

public sealed record WorkspaceReceiptAcquisition(
    StoredWorkspaceCommandReceipt StoredReceipt,
    bool WasCreated);

public sealed record DraftUpdateResult(ThreadDraft? Draft, bool WasUpdated);

public sealed record PiConfigurationUpdateResult(
    ThreadPiConfiguration? Configuration,
    bool WasUpdated);

public sealed record ThreadMetadataUpdateResult(
    HostThreadRecord? Thread,
    bool WasUpdated);

public enum DraftAttachmentMutationState
{
    Updated,
    DraftNotFound,
    DraftConflict,
    AttachmentConflict,
    AttachmentLimitExceeded,
    AttachmentNotFound,
}

public sealed record DraftAttachmentMutationResult(
    ThreadDraft? Draft,
    DraftAttachmentMutationState State,
    DraftAttachment? RemovedAttachment = null);

public sealed record DraftClearResult(
    ThreadDraft? Draft,
    DraftAttachmentMutationState State,
    IReadOnlyList<DraftAttachment> RemovedAttachments);
