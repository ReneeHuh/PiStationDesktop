using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

public enum GitFileStatus
{
    None,
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Unmerged,
    Untracked,
}

public sealed record GetProjectChangesRequest(
    ProjectId ProjectId,
    int MaximumResults = GitChangesDefaults.DefaultMaximumResults,
    ThreadId? ThreadId = null);

public sealed record ProjectChange(
    string RelativePath,
    string FileName,
    string? OriginalRelativePath,
    GitFileStatus StagedStatus,
    GitFileStatus WorkingTreeStatus,
    int Additions = 0,
    int Deletions = 0);

public enum GitRepositoryOperationState
{
    None,
    Merge,
    Rebase,
    CherryPick,
    Revert,
    Bisect,
}

public sealed record GetProjectChangesResult(
    ProjectId ProjectId,
    bool IsRepository,
    string BranchName,
    string? UpstreamName,
    int AheadCount,
    int BehindCount,
    IReadOnlyList<ProjectChange> Changes,
    bool IsTruncated,
    bool HasConflicts = false,
    GitRepositoryOperationState OperationState = GitRepositoryOperationState.None,
    string StatusToken = "",
    string WorkspacePath = "",
    bool IsWorktree = false,
    string? HeadSha = null);

public sealed record GetProjectChangeDiffRequest(
    ProjectId ProjectId,
    string RelativePath,
    int MaximumCharacters = GitChangesDefaults.DefaultMaximumDiffCharacters,
    ThreadId? ThreadId = null);

public sealed record GetProjectChangeDiffResult(
    ProjectId ProjectId,
    string RelativePath,
    string DiffContent,
    bool HasStagedChanges,
    bool HasWorkingTreeChanges,
    bool IsUntracked,
    bool IsTruncated);

public enum ThreadCheckpointStatus
{
    Ready,
    Missing,
    Error,
}

public enum CheckpointDiffScope
{
    Turn,
    FullThread,
}

public sealed record ThreadCheckpointFile(
    string RelativePath,
    int Additions,
    int Deletions);

public sealed record ThreadCheckpoint(
    TurnId TurnId,
    int TurnCount,
    string CheckpointRef,
    ThreadCheckpointStatus Status,
    IReadOnlyList<ThreadCheckpointFile> Files,
    string? PiEntryIdBeforeTurn,
    string? PiEntryIdAfterTurn,
    DateTimeOffset CompletedUtc,
    string? BeforeCheckpointRef = null,
    long WorkspaceGeneration = 0,
    string? BranchName = null,
    string? HeadShaBefore = null,
    string? HeadShaAfter = null);

public sealed record GetThreadCheckpointDiffRequest(
    ThreadId ThreadId,
    int TurnCount,
    CheckpointDiffScope Scope,
    string? RelativePath = null,
    bool IgnoreWhitespace = true,
    int MaximumCharacters = GitChangesDefaults.DefaultMaximumDiffCharacters);

public sealed record GetThreadCheckpointDiffResult(
    ThreadId ThreadId,
    int FromTurnCount,
    int ToTurnCount,
    CheckpointDiffScope Scope,
    string? RelativePath,
    string DiffContent,
    bool IsTruncated);

public static class GitChangesDefaults
{
    public const int DefaultMaximumResults = 200;
    public const int MaximumResults = 1000;
    public const int DefaultMaximumDiffCharacters = 256 * 1024;
    public const int MaximumDiffCharacters = 512 * 1024;
    public const int MaximumRelativePathLength = 1024;
    public const int MaximumCheckpointFiles = 1000;
}
