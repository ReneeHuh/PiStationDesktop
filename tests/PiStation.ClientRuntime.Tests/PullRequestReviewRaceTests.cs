using System.Reflection;
using PiStation.App.ViewModels;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.ClientRuntime.Tests;

public sealed class PullRequestReviewRaceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "PiStation.PullRequestReviewRaceTests",
        Guid.NewGuid().ToString("N"));
    private readonly ProjectId _project = ProjectId.New();
    private readonly IEnvironmentClient _client;
    private readonly ReviewClientProxy _proxy;
    private readonly PullRequestReviewDraftStore _store;

    public PullRequestReviewRaceTests()
    {
        _store = new(_root);
        _client = DispatchProxy.Create<IEnvironmentClient, ReviewClientProxy>();
        _proxy = (ReviewClientProxy)_client;
        _proxy.Read = request => Task.FromResult(Snapshot(request.Number));
    }

    [Fact]
    public async Task AStaleCompletedReadCannotOverwriteTheNewerSelection()
    {
        var firstRequested = NewSignal();
        var secondRequested = NewSignal();
        var first = NewSignal<PullRequestReviewSnapshot>();
        var second = NewSignal<PullRequestReviewSnapshot>();
        _proxy.Read = request => request.Number switch
        {
            "1" => Signal(firstRequested, first.Task),
            "2" => Signal(secondRequested, second.Task),
            _ => throw new InvalidOperationException("Unexpected pull request."),
        };

        using var model = Model();
        var firstLoad = model.LoadAsync(_project, new(_project), Snapshot("1").PullRequest);
        await firstRequested.Task;
        var secondLoad = model.LoadAsync(_project, new(_project), Snapshot("2").PullRequest);
        await secondRequested.Task;
        second.TrySetResult(Snapshot("2"));
        await secondLoad;
        first.TrySetResult(Snapshot("1"));
        await firstLoad;

        Assert.Equal("2", model.PullRequest?.Number);
        Assert.Equal("2", model.Snapshot?.PullRequest.Number);
        Assert.Equal("Review loaded.", model.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReadResponseForAnotherPullRequestOrRepositoryIsRejected(bool repositoryMismatch)
    {
        var response = repositoryMismatch ? Snapshot("1") : Snapshot("2");
        _proxy.Read = _ => Task.FromResult(response with
        {
            Repository = new SourceControlRepository(
                SourceControlProvider.GitHub,
                "github.com",
                "someone-else",
                "different-repo",
                "https://github.com/someone-else/different-repo",
                "https://github.com/someone-else/different-repo.git",
                "main",
                true),
        });

        using var model = Model();
        await model.LoadAsync(_project, new(_project), Snapshot("1").PullRequest);

        Assert.Equal("1", model.PullRequest?.Number);
        Assert.Null(model.Snapshot);
        Assert.NotEqual("Review loaded.", model.Status);
    }

    [Fact]
    public async Task AnInFlightWriteCannotClearOrDeleteTheNewPullRequestDraft()
    {
        var newDraft = new PullRequestReviewDraft(
            "github.com/owner/repo",
            "2",
            "head",
            "Draft for PR two",
            PullRequestReviewEvent.Comment,
            []);
        await _store.SaveAsync(_project, newDraft);
        var submitted = NewSignal<SubmitPullRequestReviewRequest>();
        var result = NewSignal<SourceControlOperationResult>();
        _proxy.Submit = request =>
        {
            submitted.TrySetResult(request);
            return result.Task;
        };

        using var model = Model();
        await Load(model, "1");
        model.SetBody("Review for PR one");
        var submit = model.SubmitAsync();
        var submittedRequest = await submitted.Task;

        await Load(model, "2");
        result.TrySetResult(new SourceControlOperationResult(
            true,
            "Submitted",
            OperationId: submittedRequest.OperationId,
            State: CommandReceiptState.Completed));
        await submit;

        var retained = await _store.LoadAsync(_project, newDraft.Repository, newDraft.Number);
        Assert.NotNull(retained);
        Assert.Equal(newDraft.Body, retained.Body);
        if (model.PullRequest?.Number == "2")
        {
            Assert.Equal(newDraft.Body, model.Body);
        }
    }

    [Fact]
    public async Task SubmitPayloadDoesNotChangeWhenSettersRunDuringProviderCall()
    {
        SubmitPullRequestReviewRequest? captured = null;
        var completed = NewSignal<SourceControlOperationResult>();
        using var model = Model();
        _proxy.Submit = request =>
        {
            captured = request;
            model.SetBody("changed during network call");
            return completed.Task;
        };
        await Load(model, "1");
        model.SetBody("original body");
        model.SelectedLine = model.Lines[0];
        Assert.True(model.AddInlineComment("original inline comment"));

        var submit = model.SubmitAsync();
        while (captured is null) await Task.Yield();
        completed.TrySetResult(new SourceControlOperationResult(
            false,
            "Rejected",
            OperationId: captured.OperationId,
            State: CommandReceiptState.Failed));
        await submit;

        Assert.NotNull(captured);
        Assert.Equal("original body", captured.Body);
        var comment = Assert.Single(captured.Comments);
        Assert.Equal("original inline comment", comment.Body);
    }

    [Fact]
    public async Task RecoveryKeepsPendingUntilMatchingOperationIsTerminal()
    {
        var operationId = CommandId.New();
        var pending = new PullRequestReviewDraft(
            "github.com/owner/repo",
            "1",
            "head",
            "Pending review",
            PullRequestReviewEvent.Comment,
            [],
            operationId,
            PendingAction: "SubmitReview");
        await _store.SaveAsync(_project, pending);
        using var model = Model();
        await Load(model, "1");
        var unrelatedId = CommandId.New();
        _proxy.Operations = () => Task.FromResult<IReadOnlyList<HostingOperation>>([
            new HostingOperation(
                unrelatedId,
                "SubmitReview",
                CommandReceiptState.Completed,
                new SourceControlOperationResult(true, "Other operation", OperationId: unrelatedId,
                    State: CommandReceiptState.Completed),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow),
        ]);
        await model.RecoverPendingAsync();
        Assert.Equal(operationId, model.PendingOperationId);

        _proxy.Operations = () =>
        {
            var result = new SourceControlOperationResult(
                true,
                "Accepted",
                OperationId: operationId,
                State: CommandReceiptState.Accepted);
            return Task.FromResult<IReadOnlyList<HostingOperation>>([
                new HostingOperation(operationId, "SubmitReview", CommandReceiptState.Accepted, result,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ]);
        };
        await model.RecoverPendingAsync();
        Assert.Equal(operationId, model.PendingOperationId);
        Assert.NotNull(await _store.LoadAsync(_project, pending.Repository, pending.Number));

        _proxy.Operations = () =>
        {
            var result = new SourceControlOperationResult(
                true,
                "Completed",
                OperationId: operationId,
                State: CommandReceiptState.Completed);
            return Task.FromResult<IReadOnlyList<HostingOperation>>([
                new HostingOperation(operationId, "SubmitReview", CommandReceiptState.Completed, result,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ]);
        };
        await model.RecoverPendingAsync();

        Assert.Null(model.PendingOperationId);
        Assert.Null(await _store.LoadAsync(_project, pending.Repository, pending.Number));
    }

    private PullRequestReviewViewModel Model() => new(() => _client, _store);

    private Task Load(PullRequestReviewViewModel model, string number) =>
        model.LoadAsync(_project, new(_project), Snapshot(number).PullRequest);

    private static PullRequestReviewSnapshot Snapshot(string number) => new(
        new(SourceControlProvider.GitHub, "github.com", "owner", "repo", "https://github.com/owner/repo", "https://github.com/owner/repo.git", "main", true),
        new(SourceControlProvider.GitHub, "owner/repo", number, "Review", "https://github.com/owner/repo/pull/" + number,
            PullRequestState.Open, "author", "feature", "main", false, [], [], PullRequestCheckState.Passed, DateTimeOffset.UtcNow),
        "Description", "head", "base", "reviewer", [], [],
        [new("src/App.cs", null, "modified", 1, 0, [new(null, 3, "+added", PullRequestDiffLineKind.Addition)])],
        [new("thread-1", "src/App.cs", 3, PullRequestDiffSide.Right, false, false, true, true, [])]);

    private static TaskCompletionSource<T> NewSignal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<bool> NewSignal() => NewSignal<bool>();

    private static Task<T> Signal<T>(TaskCompletionSource<bool> requested, Task<T> response)
    {
        requested.TrySetResult(true);
        return response;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    public class ReviewClientProxy : DispatchProxy
    {
        public Func<GetPullRequestReviewRequest, Task<PullRequestReviewSnapshot>> Read { get; set; } = null!;
        public Func<SubmitPullRequestReviewRequest, Task<SourceControlOperationResult>> Submit { get; set; } = _ => throw new InvalidOperationException("Unexpected write");
        public Func<Task<IReadOnlyList<HostingOperation>>> Operations { get; set; } = () => Task.FromResult<IReadOnlyList<HostingOperation>>([]);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IEnvironmentClient.GetPullRequestReviewAsync) => Read((GetPullRequestReviewRequest)args![0]!),
            nameof(IEnvironmentClient.SubmitPullRequestReviewAsync) => Submit((SubmitPullRequestReviewRequest)args![0]!),
            nameof(IEnvironmentClient.ListHostingOperationsAsync) => Operations(),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(targetMethod?.Name),
        };
    }
}
