using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Receipts;

namespace PiStation.Protocol.Models;

public sealed record EnvironmentDescriptor(
    EnvironmentId EnvironmentId,
    string EnvironmentName,
    string ServerVersion,
    int MinimumProtocolVersion,
    int MaximumProtocolVersion,
    bool PiAvailable,
    string? PiVersion,
    IReadOnlyList<string> Capabilities);

public sealed record ProjectDescriptor(
    EnvironmentId EnvironmentId,
    ProjectId ProjectId,
    string CanonicalPath,
    string DisplayName,
    DateTimeOffset CreatedUtc,
    ThreadWorkspaceMode DefaultWorkspaceMode = ThreadWorkspaceMode.Local,
    IReadOnlyList<ProjectScript>? Scripts = null,
    bool AreRepositoryScriptsTrusted = false,
    string? Icon = null,
    PiModelSelection? DefaultModel = null,
    PiThinkingLevel? DefaultThinkingLevel = null,
    string? DefaultRuntimeModeId = null,
    bool AutoPullDefaultBranch = false);

public enum ThreadWorkspaceMode
{
    Local,
    Worktree,
}

public enum ProjectScriptIcon
{
    Play,
    Test,
    Lint,
    Configure,
    Build,
    Debug,
}

public sealed record ProjectScript(
    string Id,
    string Name,
    string Command,
    ProjectScriptIcon Icon = ProjectScriptIcon.Play,
    bool RunOnWorktreeCreate = false);

public enum SetupScriptState
{
    None,
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record ThreadDescriptor(
    EnvironmentId EnvironmentId,
    ThreadId ThreadId,
    ProjectId ProjectId,
    string Title,
    string PiSessionId,
    string? PiSessionFile,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    long Revision = 0,
    bool IsArchived = false,
    bool IsPinned = false,
    ThreadWorkspaceMode WorkspaceMode = ThreadWorkspaceMode.Local,
    string? BranchName = null,
    string? WorktreePath = null,
    long WorkspaceGeneration = 0,
    SetupScriptState SetupScriptState = SetupScriptState.None,
    string? SetupScriptMessage = null,
    bool IsSettled = false,
    DateTimeOffset? SnoozedUntilUtc = null,
    long? PinnedOrder = null,
    bool HasUnsentDraft = false,
    ThreadTitleKind TitleKind = ThreadTitleKind.Placeholder,
    PullRequestLink? PullRequest = null,
    PiStation.Protocol.Projections.ThreadRuntimeState? RuntimeState = null,
    bool NeedsAttention = false)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string ActivityStatus => NeedsAttention ? "Waiting for you" : RuntimeState switch
    {
        PiStation.Protocol.Projections.ThreadRuntimeState.Running => "Working",
        PiStation.Protocol.Projections.ThreadRuntimeState.Starting or PiStation.Protocol.Projections.ThreadRuntimeState.Hydrating => "Starting",
        PiStation.Protocol.Projections.ThreadRuntimeState.Crashed => "Needs recovery",
        _ => IsSettled ? "Settled" : HasUnsentDraft ? "Draft" : "Ready",
    };
}

public enum ThreadTitleKind
{
    Placeholder,
    Generated,
    Manual,
}

public enum SourceControlProvider
{
    Unknown,
    GitHub,
    GitLab,
    Bitbucket,
    AzureDevOps,
}

public sealed record PullRequestLink(
    SourceControlProvider Provider,
    string Repository,
    string Number,
    string Url,
    string State,
    string Title,
    DateTimeOffset UpdatedUtc);

public sealed record AddProjectRequest(string Path, string? DisplayName = null);

public sealed record SetProjectScriptsTrustRequest(ProjectId ProjectId, bool IsTrusted);

public sealed record RunProjectSetupScriptRequest(ProjectId ProjectId, ThreadId ThreadId);

public sealed record RunProjectScriptRequest(
    ProjectId ProjectId,
    string ScriptId,
    ThreadId? ThreadId = null);

public sealed record RemoveProjectRequest(ProjectId ProjectId);

public sealed record UpdateProjectDefaultsRequest(
    ProjectId ProjectId,
    ThreadWorkspaceMode DefaultWorkspaceMode,
    PiModelSelection? DefaultModel,
    PiThinkingLevel? DefaultThinkingLevel,
    string? DefaultRuntimeModeId,
    bool AutoPullDefaultBranch);

public sealed record ProjectSetupScriptResult(
    SetupScriptState State,
    string? ScriptId,
    string? ScriptName,
    TerminalSessionId? TerminalSessionId,
    string? Message);

public sealed record CreateThreadRequest(
    ProjectId ProjectId,
    string? Title = null,
    ThreadWorkspaceMode? WorkspaceMode = null,
    string? BaseBranch = null,
    bool StartFromOrigin = false,
    string? BranchName = null,
    bool RunSetupScript = true);

public sealed record SearchThreadsRequest(
    ProjectId ProjectId,
    string Query,
    bool IncludeArchived = false,
    int Limit = ThreadLifecycleDefaults.DefaultSearchLimit);

public sealed record SearchThreadsResult(
    IReadOnlyList<ThreadDescriptor> Threads,
    bool IsTruncated);

public enum ThreadBulkOperation
{
    Archive,
    Restore,
    Settle,
    Unsettle,
    Snooze,
    Unsnooze,
    Pin,
    Unpin,
    Delete,
}

public sealed record ApplyThreadBulkOperationRequest(
    ProjectId ProjectId,
    IReadOnlyList<ThreadId> ThreadIds,
    ThreadBulkOperation Operation,
    DateTimeOffset? SnoozedUntilUtc = null);

public sealed record ApplyThreadBulkOperationResult(
    int AffectedCount,
    IReadOnlyList<ThreadDescriptor> Threads);

public sealed record SetThreadPinnedOrderRequest(
    ProjectId ProjectId,
    IReadOnlyList<ThreadId> ThreadIdsInOrder);

public sealed record DeleteThreadRequest(ThreadId ThreadId);

public sealed record LinkThreadPullRequestRequest(
    ThreadId ThreadId,
    PullRequestLink? PullRequest);

public static class ThreadLifecycleDefaults
{
    public const int MaximumTitleLength = 200;
    public const int MaximumSearchQueryLength = 200;
    public const int DefaultSearchLimit = 50;
    public const int MaximumSearchLimit = 200;
}

public sealed record ThreadDraft(
    EnvironmentId EnvironmentId,
    ThreadId ThreadId,
    DraftId DraftId,
    string Text,
    long Revision,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<DraftAttachment> Attachments,
    IReadOnlyList<ComposerContext>? Context = null);

public sealed record DraftAttachment(
    EnvironmentId EnvironmentId,
    ThreadId ThreadId,
    DraftId DraftId,
    AttachmentId AttachmentId,
    string FileName,
    string MediaType,
    long ByteLength,
    string Sha256,
    string ServerPath,
    DateTimeOffset CreatedUtc);

public sealed record UploadDraftAttachmentRequest(
    int ProtocolVersion,
    EnvironmentId EnvironmentId,
    ClientId ClientId,
    CommandId CommandId,
    ThreadId ThreadId,
    DraftId DraftId,
    AttachmentId AttachmentId,
    long ExpectedDraftRevision,
    string FileName,
    string? MediaType,
    long ByteLength);

public sealed record DraftAttachmentUploadResult(
    CommandReceipt Receipt,
    ThreadDraft? Draft,
    DraftAttachment? Attachment);

public static class AttachmentDefaults
{
    public const int MaximumPerDraft = 8;
    public const long MaximumImageBytes = 10L * 1024 * 1024;
    public const long MaximumFileBytes = 50L * 1024 * 1024;
}
