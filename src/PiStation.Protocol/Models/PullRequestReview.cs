using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

public enum PullRequestDiffLineKind { Header, Context, Addition, Deletion, Metadata }
public enum PullRequestDiffSide { Left, Right }
public enum PullRequestReviewEvent { Comment, Approve, RequestChanges }

public sealed record PullRequestDiffLine(int? OldLine, int? NewLine, string Text, PullRequestDiffLineKind Kind);
public sealed record PullRequestChangedFile(string Path, string? PreviousPath, string Status, int Additions, int Deletions,
    IReadOnlyList<PullRequestDiffLine> Lines, bool PatchUnavailable = false, int PatchLineOffset = 0);
public sealed record PullRequestCommit(string Sha, string Title, string Author, DateTimeOffset CreatedUtc, string? Url = null);
public sealed record PullRequestCheck(string Name, string Status, string? Conclusion, string? Url = null, string? Id = null);
public sealed record PullRequestReviewComment(string Id, string Author, string Body, DateTimeOffset CreatedUtc, string? Url = null,
    bool CanEdit = false, bool CanDelete = false, PullRequestCommentKind Kind = PullRequestCommentKind.General,
    bool CanReact = false, IReadOnlyList<PullRequestReaction>? Reactions = null);
public sealed record PullRequestDiscussion(string Id, string? Path, int? Line, PullRequestDiffSide? Side,
    bool IsResolved, bool IsOutdated, bool CanReply, bool CanResolve, IReadOnlyList<PullRequestReviewComment> Comments);
public enum PullRequestReviewPageKind { Files, Commits, Checks, Threads, ThreadComments, Comments, Reviews, Labels, Reviewers }
public sealed record PullRequestReviewContinuation(PullRequestReviewPageKind Kind, string Cursor,
    string Repository, string Number, string HeadCommitId, string? ParentId = null, string? BaseCommitId = null);
public sealed record GetPullRequestReviewRequest(WorkspaceTarget Target, string Number, PullRequestReviewContinuation? Page = null);
public sealed record PullRequestReviewSnapshot(SourceControlRepository Repository, PullRequestDescriptor PullRequest,
    string Body, string HeadCommitId, string BaseCommitId, string ViewerLogin,
    IReadOnlyList<PullRequestCommit> Commits, IReadOnlyList<PullRequestCheck> Checks,
    IReadOnlyList<PullRequestChangedFile> Files, IReadOnlyList<PullRequestDiscussion> Discussions,
    bool IsTruncated = false, string? Notice = null, IReadOnlyList<PullRequestReviewContinuation>? NextPages = null,
    bool CanEditDetails = false, bool CanManageMetadata = false,
    PullRequestAdvancedState? Advanced = null, bool CanReact = false, IReadOnlyList<PullRequestReaction>? Reactions = null);

// Writes bind to the repository and revision the user actually inspected.
public sealed record PullRequestReviewTarget(WorkspaceTarget Workspace, string Repository, string Number, string HeadCommitId);
public sealed record PullRequestInlineComment(string Path, int Line, PullRequestDiffSide Side, string Body);
public sealed record SubmitPullRequestReviewRequest(PullRequestReviewTarget Target, PullRequestReviewEvent Event,
    string Body, IReadOnlyList<PullRequestInlineComment> Comments, CommandId? OperationId = null);
public sealed record ReplyPullRequestThreadRequest(PullRequestReviewTarget Target, string ThreadId, string Body, CommandId? OperationId = null);
public sealed record SetPullRequestThreadResolvedRequest(PullRequestReviewTarget Target, string ThreadId, bool IsResolved, CommandId? OperationId = null);

public sealed record PullRequestReviewDraft(string Repository, string Number, string HeadCommitId, string Body,
    PullRequestReviewEvent Event, IReadOnlyList<PullRequestInlineComment> Comments, CommandId? PendingOperationId = null,
    string? ReplyThreadId = null, string ReplyBody = "", string? PendingAction = null,
    PullRequestManagementDraft? Management = null);

public static class PullRequestReviewDefaults
{
    public const int MaximumFiles = 300;
    public const int MaximumItems = 100;
    public const int MaximumInlineComments = 50;
    public const int MaximumBodyCharacters = 32_768;
    public const int MaximumDescriptionCharacters = 65_536;
    public const int MaximumDiffLines = 20_000;
    public static string RepositoryKey(SourceControlRepository repository) => $"{repository.Host}/{repository.Owner}/{repository.Name}";
    public static PullRequestCheckState GetCheckState(IReadOnlyList<PullRequestCheck> checks, bool hasMore = false)
    {
        if (checks.Count == 0) return PullRequestCheckState.Unknown;
        if (checks.Any(check => (check.Conclusion ?? check.Status).Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
                                (check.Conclusion ?? check.Status).Equals("TIMED_OUT", StringComparison.OrdinalIgnoreCase) ||
                                (check.Conclusion ?? check.Status).Equals("ACTION_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
                                (check.Conclusion ?? check.Status).Equals("STALE", StringComparison.OrdinalIgnoreCase) ||
                                (check.Conclusion ?? check.Status).Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                                (check.Conclusion ?? check.Status).Contains("CANCEL", StringComparison.OrdinalIgnoreCase)))
            return PullRequestCheckState.Failed;
        if (checks.Any(check => (check.Conclusion ?? check.Status).Contains("PENDING", StringComparison.OrdinalIgnoreCase) ||
                                (check.Status.Contains("QUEU", StringComparison.OrdinalIgnoreCase)) ||
                                check.Status.Contains("PROGRESS", StringComparison.OrdinalIgnoreCase) ||
                                check.Conclusion is null && !check.Status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase) && !check.Status.Equals("PASS", StringComparison.OrdinalIgnoreCase)))
            return PullRequestCheckState.Pending;
        return hasMore ? PullRequestCheckState.Unknown : PullRequestCheckState.Passed;
    }

}
