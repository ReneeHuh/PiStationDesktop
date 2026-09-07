using PiStation.ClientRuntime;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    private PullRequestReviewViewModel? _pullRequestReview;

    /// <summary>The review model is lazy so startup remains independent of hosting authentication.</summary>
    public PullRequestReviewViewModel PullRequestReview => _pullRequestReview ??= new PullRequestReviewViewModel(
        RequireClient,
        new PullRequestReviewDraftStore(
            Path.Combine(Path.GetDirectoryName(BrowserAutomationRoot)!, "pr-review-drafts")));

    public PullRequestReviewViewModel Review => PullRequestReview;
}
