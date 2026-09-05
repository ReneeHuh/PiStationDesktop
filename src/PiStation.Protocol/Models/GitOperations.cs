using System.Text.Json.Serialization;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Receipts;

namespace PiStation.Protocol.Models;

public sealed record WorkspaceTarget(ProjectId ProjectId, ThreadId? ThreadId = null);

public enum GitRefKind
{
    All,
    Local,
    Remote,
}

public sealed record GitRefDescriptor(
    string Name,
    bool IsRemote,
    string? RemoteName,
    bool IsCurrent,
    bool IsDefault,
    string? WorktreePath);

public sealed record ListGitRefsRequest(
    WorkspaceTarget Target,
    string? Query = null,
    int Cursor = 0,
    int Limit = 100,
    GitRefKind RefKind = GitRefKind.All,
    bool IncludeMatchingRemoteRefs = true,
    bool Refresh = false);

public sealed record ListGitRefsResult(
    IReadOnlyList<GitRefDescriptor> Refs,
    bool IsRepository,
    bool HasOriginRemote,
    int? NextCursor,
    int TotalCount);

public sealed record GitWorktreeDescriptor(
    string Path,
    string? BranchName,
    string HeadSha,
    bool IsCurrent,
    bool IsLocked,
    bool IsPrunable,
    bool HasChanges);

public sealed record ListGitWorktreesRequest(ProjectId ProjectId);

public sealed record ListGitWorktreesResult(
    ProjectId ProjectId,
    IReadOnlyList<GitWorktreeDescriptor> Worktrees);

public enum GitActionKind
{
    Commit,
    Push,
    CommitPush,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(GitInitCommand), "gitInit")]
[JsonDerivedType(typeof(GitPullCommand), "gitPull")]
[JsonDerivedType(typeof(GitCreateBranchCommand), "gitCreateBranch")]
[JsonDerivedType(typeof(GitSwitchBranchCommand), "gitSwitchBranch")]
[JsonDerivedType(typeof(GitCreateWorktreeCommand), "gitCreateWorktree")]
[JsonDerivedType(typeof(GitRemoveWorktreeCommand), "gitRemoveWorktree")]
[JsonDerivedType(typeof(GitRunActionCommand), "gitRunAction")]
public abstract record WorkspaceGitCommand;

public sealed record GitInitCommand(string InitialBranch = "main") : WorkspaceGitCommand;

public sealed record GitPullCommand : WorkspaceGitCommand;

public sealed record GitCreateBranchCommand(string BranchName, bool SwitchToBranch = false)
    : WorkspaceGitCommand;

public sealed record GitSwitchBranchCommand(string BranchName) : WorkspaceGitCommand;

public sealed record GitCreateWorktreeCommand(
    string BaseRef,
    string? NewBranchName = null,
    ThreadId? AssignToThreadId = null)
    : WorkspaceGitCommand;

public sealed record GitRemoveWorktreeCommand(
    string WorktreePath,
    bool Force = false,
    string? ConfirmationToken = null)
    : WorkspaceGitCommand;

public sealed record GitRunActionCommand(
    GitActionKind Action,
    string? CommitMessage = null,
    IReadOnlyList<string>? FilePaths = null,
    string? RemoteName = null)
    : WorkspaceGitCommand;

public sealed record ExecuteWorkspaceGitCommandRequest(
    int ProtocolVersion,
    EnvironmentId EnvironmentId,
    ClientId ClientId,
    CommandId CommandId,
    WorkspaceTarget Target,
    WorkspaceGitCommand Command,
    string? ExpectedHeadSha = null,
    string? ExpectedBranchName = null,
    string? ExpectedStatusToken = null);

public sealed record WorkspaceCommandReceipt(
    EnvironmentId EnvironmentId,
    ClientId ClientId,
    CommandId CommandId,
    ProjectId ProjectId,
    ThreadId? ThreadId,
    CommandReceiptState State,
    string? ErrorCode,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record GitProgressEntry(
    string Phase,
    string Message,
    bool IsError = false);

public sealed record WorkspaceGitOperationResult(
    string Operation,
    string Status,
    string? BranchName = null,
    string? UpstreamName = null,
    string? CommitSha = null,
    string? WorktreePath = null,
    string? Message = null,
    IReadOnlyList<GitProgressEntry>? Progress = null);

public sealed record ExecuteWorkspaceGitCommandResult(
    WorkspaceCommandReceipt Receipt,
    WorkspaceGitOperationResult? Result);

public static class GitOperationsDefaults
{
    public const int MaximumRefQueryLength = 256;
    public const int MaximumRefs = 200;
    public const int MaximumCommitMessageLength = 10_000;
    public const int MaximumSelectedPaths = 1_000;
}
