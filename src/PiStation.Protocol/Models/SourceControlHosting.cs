using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

public enum PullRequestState
{
    Open,
    Closed,
    Merged,
    Draft,
    Unknown,
}

public enum PullRequestCheckState
{
    Pending,
    Passed,
    Failed,
    Unknown,
}

public sealed record SourceControlRepository(
    SourceControlProvider Provider,
    string Host,
    string Owner,
    string Name,
    string WebUrl,
    string RemoteUrl,
    string DefaultBranch,
    bool CanWrite);

public sealed record PullRequestDescriptor(
    SourceControlProvider Provider,
    string Repository,
    string Number,
    string Title,
    string Url,
    PullRequestState State,
    string Author,
    string SourceBranch,
    string TargetBranch,
    bool IsDraft,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Reviewers,
    PullRequestCheckState Checks,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? ClosedOrMergedUtc = null)
{
    public bool CanComment => HostingCapabilities.CanMutate(Provider, PullRequestMutationKind.Comment);
    public bool CanLabel => HostingCapabilities.CanMutate(Provider, PullRequestMutationKind.AddLabel);
    public bool CanReview => HostingCapabilities.CanMutate(Provider, PullRequestMutationKind.AddReviewer);
    public bool CanApprove => HostingCapabilities.CanMutate(Provider, PullRequestMutationKind.Approve);
    public bool CanRequestChanges => HostingCapabilities.CanMutate(Provider, PullRequestMutationKind.RequestChanges);
    public bool CanMerge => HostingCapabilities.CanMutate(Provider, PullRequestMutationKind.Merge);
    public bool CanClose => HostingCapabilities.CanMutate(Provider, PullRequestMutationKind.Close);
}

public sealed record DetectSourceControlRequest(WorkspaceTarget Target);

public enum PullRequestInvolvement { All, Authored, ReviewRequested }
public enum PullRequestDraftFilter { Any, Only, Hide }
public enum PullRequestReviewFilter { Any, Approved, ChangesRequested, ReviewRequired, None }
public enum PullRequestChecksFilter { Any, Passing, Failing, Pending }
public sealed record PullRequestListFilters(string Query = "", PullRequestInvolvement Involvement = PullRequestInvolvement.All,
    PullRequestDraftFilter Draft = PullRequestDraftFilter.Any, PullRequestReviewFilter Review = PullRequestReviewFilter.Any,
    PullRequestChecksFilter Checks = PullRequestChecksFilter.Any, string? Author = null,
    IReadOnlyList<IReadOnlyList<string>>? LabelGroups = null, IReadOnlyList<string>? ExcludedLabels = null);

public sealed record ListPullRequestsRequest(WorkspaceTarget Target, PullRequestState? State = null, int Offset = 0, string? SourceBranch = null,
    PullRequestListFilters? Filters = null);

public sealed record ListPullRequestsResult(
    SourceControlRepository Repository,
    IReadOnlyList<PullRequestDescriptor> PullRequests,
    int? NextOffset = null, string? Notice = null);

public sealed record CloneHostedRepositoryRequest(
    string RemoteUrl,
    string DestinationPath,
    string? DisplayName = null,
    CommandId? OperationId = null);

public sealed record PublishHostedRepositoryRequest(
    ProjectId ProjectId,
    SourceControlProvider Provider,
    string Owner,
    string RepositoryName,
    bool IsPrivate = true,
    CommandId? OperationId = null);

public sealed record CreatePullRequestRequest(
    WorkspaceTarget Target,
    string Title,
    string Body,
    string? SourceBranch = null,
    string? TargetBranch = null,
    bool IsDraft = false,
    ThreadId? ThreadId = null,
    CommandId? OperationId = null);

public enum PullRequestMutationKind
{
    Comment,
    AddLabel,
    AddReviewer,
    Approve,
    RequestChanges,
    Merge,
    Close,
    Reopen,
}

public sealed record MutatePullRequestRequest(
    WorkspaceTarget Target,
    string Number,
    PullRequestMutationKind Mutation,
    string? Value = null,
    CommandId? OperationId = null);

public sealed record GenerateSourceControlTextRequest(
    WorkspaceTarget Target,
    bool ForPullRequest,
    string? Instructions = null,
    string? BaseBranch = null,
    IReadOnlyList<string>? FilePaths = null,
    PiModelSelection? Model = null);

public enum SourceControlWritingStyle
{
    Default,
    ConventionalCommits,
    RepositoryConventions,
    Custom,
}

public sealed record SourceControlWritingSettings(
    SourceControlWritingStyle Style = SourceControlWritingStyle.Default,
    string CustomInstructions = "",
    PiModelSelection? Model = null,
    long Revision = 0);

public sealed record GeneratedSourceControlText(string Title, string Body);

public sealed record SourceControlOperationResult(
    bool Succeeded,
    string Message,
    SourceControlRepository? Repository = null,
    PullRequestDescriptor? PullRequest = null,
    ProjectDescriptor? Project = null,
    CommandId? OperationId = null,
    PiStation.Protocol.Receipts.CommandReceiptState State = PiStation.Protocol.Receipts.CommandReceiptState.Completed);
