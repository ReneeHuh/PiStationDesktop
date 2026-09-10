namespace PiStation.Protocol.Models;

public static class HostingCapabilities
{
    public static bool CanReadReview(SourceControlProvider provider) => provider is SourceControlProvider.GitHub or SourceControlProvider.GitLab or SourceControlProvider.AzureDevOps;
    public static bool CanWriteReview(SourceControlProvider provider) => provider is SourceControlProvider.GitHub or SourceControlProvider.GitLab;
    public static PullRequestReviewCapabilities Review(SourceControlProvider provider) => provider switch
    {
        SourceControlProvider.GitHub => new(true, true, Enum.GetValues<PullRequestReviewEvent>(), true, true),
        SourceControlProvider.GitLab => new(true, true, [PullRequestReviewEvent.Comment, PullRequestReviewEvent.Approve], true, true),
        SourceControlProvider.AzureDevOps => new(false, false, [], false, true),
        _ => new(false, false, [], false, false),
    };
    public static bool CanList(SourceControlProvider provider) => provider is SourceControlProvider.GitHub or SourceControlProvider.GitLab or SourceControlProvider.AzureDevOps;
    public static bool CanCreate(SourceControlProvider provider) => CanList(provider);
    public static bool CanPublish(SourceControlProvider provider) => provider is SourceControlProvider.GitHub or SourceControlProvider.GitLab or SourceControlProvider.AzureDevOps;
    public static bool CanMutate(SourceControlProvider provider, PullRequestMutationKind mutation) => provider switch
    {
        SourceControlProvider.GitHub => Enum.IsDefined(mutation),
        SourceControlProvider.GitLab => Enum.IsDefined(mutation) && mutation != PullRequestMutationKind.RequestChanges,
        SourceControlProvider.AzureDevOps => mutation is PullRequestMutationKind.AddReviewer or PullRequestMutationKind.Approve or
            PullRequestMutationKind.RequestChanges or PullRequestMutationKind.Merge or PullRequestMutationKind.Close or PullRequestMutationKind.Reopen,
        _ => false,
    };
}
