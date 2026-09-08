using PiStation.ClientRuntime;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    private PullRequestReviewViewModel? _pullRequestReview;

    /// <summary>The review model is lazy so startup remains independent of hosting authentication.</summary>
    public PullRequestReviewViewModel PullRequestReview => _pullRequestReview ??= new PullRequestReviewViewModel(
        RequireClient,
        new PullRequestReviewDraftStore(
            Path.Combine(Path.GetDirectoryName(BrowserAutomationRoot)!, "pr-review-drafts")),
        () => CanOperate);

    public PullRequestReviewViewModel Review => PullRequestReview;

    public async Task<bool> CreatePullRequestReviewThreadAsync(CancellationToken cancellationToken = default)
    {
        if (!CanOperate || SelectedProject is not { } project || PullRequestReview.ProjectId != project.ProjectId) return false;
        var previousThreadId = SelectedThread?.ThreadId;
        var client = RequireClient();
        var thread = await PullRequestReview.CreateReviewThreadAsync(
            PiConfiguration.SelectedModel?.Selection ?? Layout.LastModel,
            PiConfiguration.SelectedThinkingLevel?.Value ?? Layout.LastThinkingLevel, cancellationToken);
        if (thread is null || !ReferenceEquals(client, RequireClient()) || SelectedProject?.ProjectId != project.ProjectId ||
            thread.ProjectId != project.ProjectId || SelectedThread?.ThreadId != previousThreadId) return false;
        CancelThreadSearch();
        ThreadSearchQuery = string.Empty;
        IsShowingArchivedThreads = thread.IsArchived;
        var now = DateTimeOffset.UtcNow;
        InboxShelf = ThreadInbox.Shelf(thread, now);
        Replace(Threads, ThreadInbox.Select(client.ThreadMetadata.GetProjectThreads(project.ProjectId, includeArchived: thread.IsArchived), InboxShelf, now));
        foreach (var group in ProjectGroups) group.Apply(group.AllThreads, InboxShelf, Layout.Sidebar);
        ThreadListStatus = string.Empty;
        ThreadLifecycleStatus = $"Opened {thread.Title}. Send the prepared prompt to start the review.";
        await SelectThreadAsync(thread, cancellationToken);
        return SelectedThread?.ThreadId == thread.ThreadId;
    }
}
