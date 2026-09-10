using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

public enum PullRequestManagementAction
{
    EditDetails, SetDraft, EditComment, DeleteComment, RemoveLabel, RemoveReviewer,
    Merge, EnableAutoMerge, DisableAutoMerge, UpdateBranch, Revert, ApproveWorkflow, SetReaction
}
public enum PullRequestCommentKind { General, Inline, Review }
public enum PullRequestMergeMethod { Merge, Squash, Rebase }
public enum PullRequestUpdateMethod { Merge, Rebase }
public enum PullRequestReactionContent { ThumbsUp, ThumbsDown, Laugh, Hooray, Confused, Heart, Rocket, Eyes }
public sealed record PullRequestReaction(PullRequestReactionContent Content, int Count, bool ViewerHasReacted);
public sealed record PullRequestAdvancedState(IReadOnlyList<PullRequestMergeMethod> MergeMethods,
    bool CanMerge, bool CanEnableAutoMerge, bool CanDisableAutoMerge, bool CanUpdateBranch, bool CanRevert,
    bool CanApproveWorkflows, bool AutoMergeEnabled, string Mergeability, string BaseStatus,
    IReadOnlyList<PullRequestUpdateMethod>? UpdateMethods = null);
public sealed record PullRequestWorkflow(string Id, string Name, string Status, string Url)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayLabel => $"{Name} · Run #{Id} · {Status}";
}
public sealed record GetPullRequestWorkflowsRequest(PullRequestReviewTarget Target, int Page = 1);
public sealed record PullRequestWorkflowsResult(PullRequestReviewTarget Target, IReadOnlyList<PullRequestWorkflow> Workflows, int? NextPage);

public sealed record ManagePullRequestRequest(PullRequestReviewTarget Target, PullRequestManagementAction Action,
    string? Title = null, string? Body = null, bool? IsDraft = null, string? ItemId = null,
    string? ExpectedTitle = null, string? ExpectedBody = null, bool? ExpectedIsDraft = null, CommandId? OperationId = null,
    PullRequestMergeMethod? MergeMethod = null, PullRequestUpdateMethod? UpdateMethod = null,
    PullRequestReactionContent? Reaction = null, bool? Reacted = null);

public sealed record PullRequestManagementDraft(string Title, string Body, string ExpectedTitle, string ExpectedBody,
    string? CommentId = null, string CommentBody = "", string? ExpectedCommentBody = null,
    ManagePullRequestRequest? PendingRequest = null);
