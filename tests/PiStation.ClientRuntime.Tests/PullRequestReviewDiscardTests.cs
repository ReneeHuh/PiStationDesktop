using System.Reflection;
using PiStation.App.ViewModels;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.ClientRuntime.Tests;

public sealed class PullRequestReviewDiscardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PiStation.ReviewDiscardTests", Guid.NewGuid().ToString("N"));
    private readonly ProjectId _project = ProjectId.New();
    private readonly PullRequestReviewDraftStore _store;
    private readonly ReviewClientProxy _proxy;
    private readonly IEnvironmentClient _client;

    public PullRequestReviewDiscardTests()
    {
        _store = new(_root);
        _client = DispatchProxy.Create<IEnvironmentClient, ReviewClientProxy>();
        _proxy = (ReviewClientProxy)_client;
        _proxy.Read = request => Task.FromResult(Snapshot(request.Number));
    }

    [Fact]
    public async Task DiscardSerializesWithWritesAndDisablesSubmissionWhileDeleting()
    {
        using var model = new PullRequestReviewViewModel(() => _client, _store);
        await LoadAsync(model, "1");
        model.SetBody("Keep this draft until discard completes.");
        await model.SaveNowAsync();

        var fileGate = GetDraftStoreGate();
        await fileGate.WaitAsync();
        Task discard;
        try
        {
            discard = model.DiscardDraftAsync();
            await WaitUntilAsync(() => model.IsBusy);

            Assert.False(model.CanSubmit);
            model.SetBody("This must not be written during discard.");
            Assert.Equal("Keep this draft until discard completes.", model.Body);
            Assert.Null(await model.SubmitAsync());
            Assert.Empty(_proxy.Submissions);
        }
        finally
        {
            fileGate.Release();
        }

        await discard;
        Assert.False(model.IsBusy);
        Assert.Null(await _store.LoadAsync(_project, "github.com/owner/repo", "1"));
    }

    [Fact]
    public async Task CancelledDiscardRetainsDraftAndRestoresEditing()
    {
        using var model = new PullRequestReviewViewModel(() => _client, _store);
        await LoadAsync(model, "1");
        model.SetBody("Retain this when deletion is cancelled.");
        await model.SaveNowAsync();

        var fileGate = GetDraftStoreGate();
        await fileGate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        Task discard;
        try
        {
            discard = model.DiscardDraftAsync(cancellation.Token);
            await WaitUntilAsync(() => model.IsBusy);
            cancellation.Cancel();
        }
        finally
        {
            fileGate.Release();
        }

        await discard;
        Assert.False(model.IsBusy);
        Assert.True(model.CanEditDraft);
        Assert.Equal("Retain this when deletion is cancelled.", model.Body);
        Assert.Equal("Retain this when deletion is cancelled.",
            (await _store.LoadAsync(_project, "github.com/owner/repo", "1"))?.Body);
    }

    [Fact]
    public async Task ExistingPendingOperationCannotBeClearedByDiscard()
    {
        var pendingId = CommandId.New();
        await _store.SaveAsync(_project, new PullRequestReviewDraft(
            "github.com/owner/repo", "1", "head", "Pending", PullRequestReviewEvent.Comment, [],
            pendingId, PendingAction: "SubmitReview"));

        using var model = new PullRequestReviewViewModel(() => _client, _store);
        await LoadAsync(model, "1");
        await model.DiscardDraftAsync();

        Assert.Equal(pendingId, model.PendingOperationId);
        Assert.False(model.CanSubmit);
        Assert.Equal(pendingId,
            (await _store.LoadAsync(_project, "github.com/owner/repo", "1"))?.PendingOperationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwitchingDuringDiscardDoesNotRecreateDeletedDraftOrLoseCancelledDraft(bool cancel)
    {
        using var model = new PullRequestReviewViewModel(() => _client, _store);
        await LoadAsync(model, "1");
        model.SetBody("Saved text");
        await model.SaveNowAsync();
        model.SetBody("Most recent unsaved text");
        var fileGate = GetDraftStoreGate();
        await fileGate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        Task discard;
        Task load;
        try
        {
            discard = model.DiscardDraftAsync(cancellation.Token);
            Assert.True(model.IsBusy);
            load = LoadAsync(model, "2");
            if (cancel) cancellation.Cancel();
        }
        finally { fileGate.Release(); }
        await discard;
        await load;
        Assert.Equal("2", model.PullRequest?.Number);
        Assert.Empty(model.Body);
        var oldDraft = await _store.LoadAsync(_project, "github.com/owner/repo", "1");
        if (cancel) Assert.Equal("Most recent unsaved text", oldDraft?.Body);
        else Assert.Null(oldDraft);
    }

    [Fact]
    public async Task SubmissionQueuedBehindDiscardCannotSendItsCapturedOldDraft()
    {
        using var model = new PullRequestReviewViewModel(() => _client, _store);
        await LoadAsync(model, "1");
        model.SetBody("Must not submit after discard");
        await model.SaveNowAsync();
        var operationGate = (SemaphoreSlim)typeof(PullRequestReviewViewModel)
            .GetField("_writeGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(model)!;
        await operationGate.WaitAsync();
        Task discard;
        Task<SourceControlOperationResult?> submit;
        try
        {
            discard = model.DiscardDraftAsync();
            submit = model.SubmitAsync();
        }
        finally { operationGate.Release(); }
        await discard;
        Assert.Null(await submit);
        Assert.Empty(_proxy.Submissions);
        Assert.Empty(model.Body);
        Assert.Null(model.PendingOperationId);
    }

    private async Task LoadAsync(PullRequestReviewViewModel model, string number) =>
        await model.LoadAsync(_project, new WorkspaceTarget(_project), Snapshot(number).PullRequest);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++) await Task.Delay(1);
        Assert.True(condition(), "The expected asynchronous state transition did not occur.");
    }

    private static SemaphoreSlim GetDraftStoreGate() =>
        (SemaphoreSlim)(typeof(PullRequestReviewDraftStore).GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            ?? throw new InvalidOperationException("Draft store gate was not found."));

    private static PullRequestReviewSnapshot Snapshot(string number) => new(
        new(SourceControlProvider.GitHub, "github.com", "owner", "repo", "https://github.com/owner/repo", "https://github.com/owner/repo.git", "main", true),
        new(SourceControlProvider.GitHub, "owner/repo", number, "Review", "https://github.com/owner/repo/pull/" + number,
            PullRequestState.Open, "author", "feature", "main", false, [], [], PullRequestCheckState.Passed, DateTimeOffset.UtcNow),
        "Description", "head", "base", "reviewer", [], [],
        [new("src/App.cs", null, "modified", 1, 0, [new(null, 3, "+added", PullRequestDiffLineKind.Addition)])],
        []);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    public class ReviewClientProxy : DispatchProxy
    {
        public Func<GetPullRequestReviewRequest, Task<PullRequestReviewSnapshot>> Read { get; set; } = null!;
        public List<SubmitPullRequestReviewRequest> Submissions { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IEnvironmentClient.GetPullRequestReviewAsync) => Read((GetPullRequestReviewRequest)args![0]! ),
            nameof(IEnvironmentClient.SubmitPullRequestReviewAsync) => RecordSubmission((SubmitPullRequestReviewRequest)args![0]! ),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(targetMethod?.Name),
        };

        private Task<SourceControlOperationResult> RecordSubmission(SubmitPullRequestReviewRequest request)
        {
            Submissions.Add(request);
            return Task.FromResult(new SourceControlOperationResult(
                true, "Submitted", OperationId: request.OperationId, State: CommandReceiptState.Completed));
        }
    }
}
