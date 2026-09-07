using System.Reflection;
using PiStation.App.ViewModels;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class PullRequestReviewSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "PiStation.PullRequestReviewSafetyTests",
        Guid.NewGuid().ToString("N"));
    private readonly ProjectId _project = ProjectId.New();
    private readonly IEnvironmentClient _client;
    private readonly ReviewClientProxy _proxy;
    private readonly PullRequestReviewDraftStore _store;

    public PullRequestReviewSafetyTests()
    {
        _store = new(_root);
        _client = DispatchProxy.Create<IEnvironmentClient, ReviewClientProxy>();
        _proxy = (ReviewClientProxy)_client;
        _proxy.Read = request => Task.FromResult(Snapshot(request.Number));
    }

    [Fact]
    public async Task RestoredReplyDraftSelectsItsMatchingDiscussion()
    {
        await _store.SaveAsync(_project, new PullRequestReviewDraft(
            "github.com/owner/repo",
            "1",
            "head",
            string.Empty,
            PullRequestReviewEvent.Comment,
            [],
            ReplyThreadId: "thread-2",
            ReplyBody: "Reply to the second thread"));

        using var model = Model();
        await Load(model, "1");

        Assert.Equal("thread-2", model.SelectedDiscussion?.Id);
        Assert.Equal("thread-2", model.ReplyThreadId);
        Assert.Equal("Reply to the second thread", model.ReplyBody);
        Assert.True(model.CanReply);
    }

    [Fact]
    public async Task DiscussionCannotChangeWhileAnUnsentReplyTargetsAnotherThread()
    {
        await _store.SaveAsync(_project, new PullRequestReviewDraft(
            "github.com/owner/repo",
            "1",
            "head",
            string.Empty,
            PullRequestReviewEvent.Comment,
            [],
            ReplyThreadId: "thread-2",
            ReplyBody: "Keep this reply with thread two"));

        using var model = Model();
        await Load(model, "1");
        var first = model.Discussions.Single(discussion => discussion.Id == "thread-1");
        model.SelectedDiscussion = first;

        Assert.Equal("thread-2", model.SelectedDiscussion?.Id);
        Assert.Equal("thread-2", model.ReplyThreadId);
        Assert.Equal("Keep this reply with thread two", model.ReplyBody);
    }

    [Fact]
    public async Task ApproveMayBeSubmittedWithAnEmptyBody()
    {
        using var model = Model();
        await Load(model, "1");

        model.ReviewEvent = PullRequestReviewEvent.Approve;

        Assert.Empty(model.Body);
        Assert.True(model.CanSubmit);
    }

    [Theory]
    [InlineData(PullRequestReviewEvent.Comment)]
    [InlineData(PullRequestReviewEvent.RequestChanges)]
    public async Task CommentAndRequestChangesRequireNonWhitespaceBody(PullRequestReviewEvent reviewEvent)
    {
        using var model = Model();
        await Load(model, "1");

        model.ReviewEvent = reviewEvent;
        model.SetBody(" \t\r\n ");

        Assert.False(model.CanSubmit);
    }

    [Fact]
    public async Task AWorkspaceTargetForAnotherProjectIsRejected()
    {
        using var model = Model();
        var otherProject = ProjectId.New();

        await Assert.ThrowsAsync<ArgumentException>(() => model.LoadAsync(
            _project,
            new WorkspaceTarget(otherProject),
            Snapshot("1").PullRequest));
        Assert.Null(model.Snapshot);
    }

    [Fact]
    public async Task PendingOperationBlocksSettersWithoutMutatingDraftBody()
    {
        var operationId = CommandId.New();
        await _store.SaveAsync(_project, new PullRequestReviewDraft(
            "github.com/owner/repo",
            "1",
            "head",
            "Pending review body",
            PullRequestReviewEvent.Comment,
            [],
            operationId,
            ReplyThreadId: "thread-1",
            ReplyBody: "Pending reply",
            PendingAction: "SubmitReview"));

        using var model = Model();
        await Load(model, "1");
        model.SetBody("Should not replace pending body");
        model.SetReplyBody("Should not replace pending reply");
        model.ReviewEvent = PullRequestReviewEvent.Approve;

        Assert.Equal(operationId, model.PendingOperationId);
        Assert.Equal("Pending review body", model.Body);
        Assert.Equal("Pending reply", model.ReplyBody);
        Assert.Equal(PullRequestReviewEvent.Comment, model.ReviewEvent);
    }

    private PullRequestReviewViewModel Model() => new(() => _client, _store);

    private Task Load(PullRequestReviewViewModel model, string number) =>
        model.LoadAsync(_project, new WorkspaceTarget(_project), Snapshot(number).PullRequest);

    private static PullRequestReviewSnapshot Snapshot(string number) => new(
        new(SourceControlProvider.GitHub, "github.com", "owner", "repo", "https://github.com/owner/repo", "https://github.com/owner/repo.git", "main", true),
        new(SourceControlProvider.GitHub, "owner/repo", number, "Review", "https://github.com/owner/repo/pull/" + number,
            PullRequestState.Open, "author", "feature", "main", false, [], [], PullRequestCheckState.Passed, DateTimeOffset.UtcNow),
        "Description", "head", "base", "reviewer", [], [],
        [new("src/App.cs", null, "modified", 1, 0, [new(null, 3, "+added", PullRequestDiffLineKind.Addition)])],
        [
            new("thread-1", "src/App.cs", 3, PullRequestDiffSide.Right, false, false, true, true, []),
            new("thread-2", "src/App.cs", 3, PullRequestDiffSide.Right, false, false, true, true, []),
        ]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    public class ReviewClientProxy : DispatchProxy
    {
        public Func<GetPullRequestReviewRequest, Task<PullRequestReviewSnapshot>> Read { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IEnvironmentClient.GetPullRequestReviewAsync) => Read((GetPullRequestReviewRequest)args![0]!),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(targetMethod?.Name),
        };
    }
}
