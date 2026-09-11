namespace PiStation.Protocol.Models;

public static class HostingCapabilities
{
    public static bool CanReadReview(SourceControlProvider provider) => provider is SourceControlProvider.GitHub or SourceControlProvider.GitLab or SourceControlProvider.AzureDevOps or SourceControlProvider.Bitbucket;
    public static bool CanWriteReview(SourceControlProvider provider) => provider is SourceControlProvider.GitHub or SourceControlProvider.GitLab or SourceControlProvider.Bitbucket;
    public static PullRequestReviewCapabilities Review(SourceControlProvider provider) => provider switch
    {
        SourceControlProvider.GitHub => new(true, true, Enum.GetValues<PullRequestReviewEvent>(), true, true),
        SourceControlProvider.GitLab => new(true, true, [PullRequestReviewEvent.Comment, PullRequestReviewEvent.Approve], true, true),
        SourceControlProvider.AzureDevOps => new(false, false, [], false, true),
        SourceControlProvider.Bitbucket => new(true, true, Enum.GetValues<PullRequestReviewEvent>(), false, true),
        _ => new(false, false, [], false, false),
    };
    public static bool CanList(SourceControlProvider provider) => CanReadReview(provider);
    public static bool CanCreate(SourceControlProvider provider) => CanList(provider);
    public static bool CanPublish(SourceControlProvider provider) => CanList(provider);
    public static bool CanMutate(SourceControlProvider provider, PullRequestMutationKind mutation) => provider switch
    {
        SourceControlProvider.GitHub => Enum.IsDefined(mutation),
        SourceControlProvider.GitLab => Enum.IsDefined(mutation) && mutation != PullRequestMutationKind.RequestChanges,
        SourceControlProvider.AzureDevOps => mutation is PullRequestMutationKind.AddReviewer or PullRequestMutationKind.Approve or
            PullRequestMutationKind.RequestChanges or PullRequestMutationKind.Merge or PullRequestMutationKind.Close or PullRequestMutationKind.Reopen,
        SourceControlProvider.Bitbucket => mutation is PullRequestMutationKind.Comment or PullRequestMutationKind.AddReviewer or PullRequestMutationKind.Approve or
            PullRequestMutationKind.RequestChanges or PullRequestMutationKind.Merge or PullRequestMutationKind.Close,
        _ => false,
    };
}
