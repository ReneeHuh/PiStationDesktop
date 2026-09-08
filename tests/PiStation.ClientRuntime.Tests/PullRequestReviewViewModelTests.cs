using System.Reflection;
using PiStation.App.ViewModels;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.ClientRuntime.Tests;

public sealed class PullRequestReviewViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PiStation.ReviewPresentationTests", Guid.NewGuid().ToString("N"));
    private readonly ProjectId _project = ProjectId.New();
    private readonly IEnvironmentClient _client;
    private readonly ReviewClientProxy _proxy;
    private readonly PullRequestReviewDraftStore _store;

    public PullRequestReviewViewModelTests()
    {
        _store = new(_root);
        _client = DispatchProxy.Create<IEnvironmentClient, ReviewClientProxy>();
        _proxy = (ReviewClientProxy)_client;
        _proxy.Read = request => Task.FromResult(Snapshot(request.Number));
    }

    [Fact]
    public async Task SwitchingPullRequestsFlushesOldDraftAndDoesNotCopyTextToNewOne()
    {
        using var model = Model();
        await Load(model, "1");
        model.SetBody("Only for PR one");
        model.SetReplyBody("Unsent reply for one");
        await Load(model, "2");
        Assert.Empty(model.Body);
        Assert.Empty(model.ReplyBody);
        var saved = await _store.LoadAsync(_project, "github.com/owner/repo", "1");
        Assert.Equal("Only for PR one", saved?.Body);
        await model.SaveNowAsync();
        Assert.DoesNotContain("Only for PR one", (await _store.LoadAsync(_project, "github.com/owner/repo", "2"))?.Body ?? "");
    }

    [Fact]
    public async Task SavingStaleDraftPreservesItsOriginalHeadAndBlocksAllWrites()
    {
        await _store.SaveAsync(_project, new("github.com/owner/repo", "1", "old-head", "Existing review", PullRequestReviewEvent.Comment, []));
        using var model = Model();
        await Load(model, "1");
        model.SetReplyBody("Reply");
        await model.SaveNowAsync();
        Assert.True(model.IsStaleHead);
        Assert.False(model.CanSubmit);
        Assert.False(model.CanReply);
        Assert.False(model.CanResolve);
        Assert.Equal("old-head", (await _store.LoadAsync(_project, "github.com/owner/repo", "1"))?.HeadCommitId);
    }

    [Fact]
    public async Task PendingDraftCannotBeDiscardedOrResubmittedAndSurvivesReopen()
    {
        var id = CommandId.New();
        await _store.SaveAsync(_project, new("github.com/owner/repo", "1", "head", "Saved", PullRequestReviewEvent.Comment, [], id, PendingAction: "SubmitReview"));
        using var model = Model();
        await Load(model, "1");
        await model.DiscardDraftAsync();
        Assert.Equal(id, model.PendingOperationId);
        Assert.False(model.CanSubmit);
        Assert.Equal(id, (await _store.LoadAsync(_project, "github.com/owner/repo", "1"))?.PendingOperationId);
    }

    [Fact]
    public async Task CorruptDraftBlocksWritesAndCannotBeOverwrittenByAutosave()
    {
        await _store.SaveAsync(_project, new("github.com/owner/repo", "1", "head", "Saved", PullRequestReviewEvent.Comment, []));
        var file = Assert.Single(Directory.EnumerateFiles(_root, "*.json"));
        await File.WriteAllTextAsync(file, "{broken draft with potentially pending operation");
        using var model = Model();
        await Load(model, "1");
        model.SetBody("New review");
        await model.SaveNowAsync();
        Assert.False(model.CanSubmit);
        Assert.Contains("broken draft", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task NonterminalWriteReceiptDoesNotUnlockTheDraft()
    {
        _proxy.Submit = request => Task.FromResult(new SourceControlOperationResult(false, "Accepted", OperationId: request.OperationId, State: CommandReceiptState.Accepted));
        using var model = Model();
        await Load(model, "1");
        model.SetBody("Review");
        await model.SubmitAsync();
        Assert.NotNull(model.PendingOperationId);
        Assert.False(model.CanSubmit);
        Assert.Equal(model.PendingOperationId, (await _store.LoadAsync(_project, "github.com/owner/repo", "1"))?.PendingOperationId);
    }

    [Fact]
    public async Task ResolveDoesNotEraseAnUnsentReplyOrReview()
    {
        _proxy.Resolve = request => Task.FromResult(new SourceControlOperationResult(true, "Resolved", OperationId: request.OperationId));
        using var model = Model();
        await Load(model, "1");
        model.SetBody("My review");
        model.SetReplyBody("My unsent reply");
        await model.SetResolvedAsync(true);
        Assert.Equal("My review", model.Body);
        Assert.Equal("My unsent reply", model.ReplyBody);
    }

    [Fact]
    public async Task ConfirmedRejectionUnlocksTheDraftWithoutLosingTextOrInlineComments()
    {
        _proxy.Submit = request => Task.FromResult(new SourceControlOperationResult(false, "Validation rejected", OperationId: request.OperationId, State: CommandReceiptState.Rejected));
        using var model = Model();
        await Load(model, "1");
        model.SetBody("Keep this review");
        model.SelectedLine = model.Lines[0];
        Assert.True(model.AddInlineComment("Keep this inline comment"));
        await model.SubmitAsync();
        Assert.Null(model.PendingOperationId);
        Assert.True(model.CanSubmit);
        Assert.Equal("Keep this review", model.Body);
        Assert.Single(model.InlineComments);
        var draft = await _store.LoadAsync(_project, "github.com/owner/repo", "1");
        Assert.NotNull(draft);
        Assert.Null(draft.PendingOperationId);
        Assert.Equal(model.Body, draft.Body);
        Assert.Single(draft.Comments);
    }

    [Fact]
    public async Task ChangingLineAndSideRaisesAvailabilityAndReviewChoicePersists()
    {
        using var model = Model();
        await Load(model, "1");
        var changed = new List<string?>();
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        model.SelectedLine = model.Lines[0];
        model.SelectedSide = PullRequestDiffSide.Left;
        Assert.Contains(nameof(model.CanAddInlineComment), changed);
        Assert.False(model.CanAddInlineComment);
        model.SelectedSide = PullRequestDiffSide.Right;
        Assert.True(model.CanAddInlineComment);
        model.ReviewEvent = PullRequestReviewEvent.Approve;
        await model.SaveNowAsync();
        Assert.Equal(PullRequestReviewEvent.Approve, (await _store.LoadAsync(_project, "github.com/owner/repo", "1"))?.Event);
    }

    [Fact]
    public async Task LoadingMorePreservesReviewReplyInlineDraftAndSelectedLine()
    {
        var first = Snapshot("1");
        var cursor = new PullRequestReviewContinuation(PullRequestReviewPageKind.Files, "page", "github.com/owner/repo", "1", "head");
        first = first with { NextPages = [cursor] };
        _proxy.Read = request => Task.FromResult(request.Page is null ? first : first with
        {
            Files = [first.Files[0] with { Lines = [new(null, 4, "+later", PullRequestDiffLineKind.Addition)], PatchLineOffset = 1 }],
            Discussions = [first.Discussions[0] with { Comments = [new("later", "author", "Later comment", DateTimeOffset.UtcNow)] }],
            NextPages = []
        });
        using var model = Model();
        await Load(model, "1");
        model.SetBody("Keep review");
        model.SetReplyBody("Keep reply");
        model.SelectedLine = Assert.Single(model.Lines);
        Assert.True(model.AddInlineComment("Keep line draft"));
        var selectedLine = model.SelectedLine!.Line;
        Assert.True(model.CanLoadMore);
        await model.LoadMoreAsync();
        Assert.False(model.CanLoadMore);
        Assert.Equal("Keep review", model.Body);
        Assert.Equal("Keep reply", model.ReplyBody);
        Assert.Equal("thread-1", model.ReplyThreadId);
        Assert.Equal("Keep line draft", Assert.Single(model.InlineComments).Body);
        Assert.Equal(selectedLine, model.SelectedLine?.Line);
        Assert.Equal(2, model.Lines.Count);
        Assert.Contains("Later comment", model.DiscussionSummary);
        await model.SaveNowAsync();
        Assert.Equal("Keep reply", (await _store.LoadAsync(_project, "github.com/owner/repo", "1"))?.ReplyBody);
    }

    [Fact]
    public async Task LatePageCannotOverwriteAnotherPullRequest()
    {
        var cursor = new PullRequestReviewContinuation(PullRequestReviewPageKind.Commits, "page", "github.com/owner/repo", "1", "head");
        var pending = new TaskCompletionSource<PullRequestReviewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _proxy.Read = request => request.Page is not null ? pending.Task : Task.FromResult(Snapshot(request.Number) with { NextPages = request.Number == "1" ? [cursor] : [] });
        using var model = Model();
        await Load(model, "1");
        model.SetBody("First draft");
        var loadMore = model.LoadMoreAsync();
        await Load(model, "2");
        pending.SetResult(Snapshot("1"));
        await loadMore;
        Assert.Equal("2", model.Snapshot?.PullRequest.Number);
        Assert.Empty(model.Body);
        Assert.False(model.IsBusy);
        Assert.Equal("First draft", (await _store.LoadAsync(_project, "github.com/owner/repo", "1"))?.Body);
    }

    [Fact]
    public async Task StalePageKeepsDraftAndDisablesReviewWrites()
    {
        var cursor = new PullRequestReviewContinuation(PullRequestReviewPageKind.Commits, "page", "github.com/owner/repo", "1", "head");
        _proxy.Read = request => Task.FromResult(request.Page is null ? Snapshot("1") with { NextPages = [cursor] } : Snapshot("1") with { HeadCommitId = "new-head" });
        using var model = Model();
        await Load(model, "1");
        model.SetBody("Preserve stale draft");
        await model.LoadMoreAsync();
        Assert.True(model.IsStaleHead);
        Assert.False(model.CanSubmit);
        Assert.False(model.CanLoadMore);
        Assert.Equal("head", model.Snapshot?.HeadCommitId);
        Assert.Equal("Preserve stale draft", model.Body);
    }

    private PullRequestReviewViewModel Model() => new(() => _client, _store);
    private Task Load(PullRequestReviewViewModel model, string number) => model.LoadAsync(_project, new(_project), Snapshot(number).PullRequest);
    private static PullRequestReviewSnapshot Snapshot(string number) => new(
        new(SourceControlProvider.GitHub, "github.com", "owner", "repo", "https://github.com/owner/repo", "https://github.com/owner/repo.git", "main", true),
        new(SourceControlProvider.GitHub, "owner/repo", number, "Review", "https://github.com/owner/repo/pull/" + number,
            PullRequestState.Open, "author", "feature", "main", false, [], [], PullRequestCheckState.Passed, DateTimeOffset.UtcNow),
        "Description", "head", "base", "reviewer", [], [],
        [new("src/App.cs", null, "modified", 1, 0, [new(null, 3, "+added", PullRequestDiffLineKind.Addition)])],
        [new("thread-1", "src/App.cs", 3, PullRequestDiffSide.Right, false, false, true, true, [])]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    public class ReviewClientProxy : DispatchProxy
    {
        public Func<GetPullRequestReviewRequest, Task<PullRequestReviewSnapshot>> Read { get; set; } = null!;
        public Func<SubmitPullRequestReviewRequest, Task<SourceControlOperationResult>> Submit { get; set; } = _ => throw new InvalidOperationException("Unexpected write");
        public Func<SetPullRequestThreadResolvedRequest, Task<SourceControlOperationResult>> Resolve { get; set; } = _ => throw new InvalidOperationException("Unexpected write");
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IEnvironmentClient.GetPullRequestReviewAsync) => Read((GetPullRequestReviewRequest)args![0]!),
            nameof(IEnvironmentClient.SubmitPullRequestReviewAsync) => Submit((SubmitPullRequestReviewRequest)args![0]!),
            nameof(IEnvironmentClient.SetPullRequestThreadResolvedAsync) => Resolve((SetPullRequestThreadResolvedRequest)args![0]!),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(targetMethod?.Name),
        };
    }
}
