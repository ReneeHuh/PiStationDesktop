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

    [Fact]
    public async Task CheckoutCapturesLoadedRevisionAndSavesReviewDraftBeforeDispatch()
    {
        using var model = Model();
        var source = ThreadId.New();
        await model.LoadAsync(_project, new(_project, source), Snapshot("1").PullRequest);
        model.SetBody("Keep my unpublished review");
        CreatePullRequestReviewThreadRequest? captured = null;
        var thread = ReviewThread();
        _proxy.Checkout = async request =>
        {
            captured = request;
            Assert.Equal("Keep my unpublished review", (await _store.LoadAsync(_project, "github.com/owner/repo", "1"))?.Body);
            Assert.False(model.CanCreateReviewThread);
            return thread;
        };
        var selection = new PiModelSelection("provider", "model");
        Assert.Equal(thread, await model.CreateReviewThreadAsync(selection));
        Assert.Equal(new(_project, source), captured?.Target.Workspace);
        Assert.Equal("1", captured?.Target.Number);
        Assert.Equal("head", captured?.Target.HeadCommitId);
        Assert.Equal("github.com/owner/repo", captured?.Target.Repository);
        Assert.Equal(selection, captured?.InheritedModel);
        Assert.True(model.CanCreateReviewThread);
    }

    [Fact]
    public async Task CheckoutFailureAllowsRetryWithoutPublishingOrLosingDraft()
    {
        using var model = Model();
        await Load(model, "1");
        model.SetBody("Preserve this review");
        _proxy.Checkout = _ => throw new IOException("fetch failed");
        Assert.Null(await model.CreateReviewThreadAsync());
        Assert.Contains("fetch failed", model.Status);
        Assert.Equal("Preserve this review", model.Body);
        Assert.True(model.CanCreateReviewThread);
        _proxy.Checkout = _ => Task.FromResult(ReviewThread());
        Assert.NotNull(await model.CreateReviewThreadAsync());
    }

    [Fact]
    public async Task LateCheckoutCannotNavigateAfterSelectionChangesOrDispatchTwice()
    {
        using var model = Model();
        await Load(model, "1");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<ThreadDescriptor>(TaskCreationOptions.RunContinuationsAsynchronously);
        _proxy.Checkout = _ => { started.SetResult(); return pending.Task; };
        var checkout = model.CreateReviewThreadAsync();
        await started.Task;
        Assert.Null(await model.CreateReviewThreadAsync());
        await Load(model, "2");
        Assert.False(model.CanCreateReviewThread);
        pending.SetResult(ReviewThread());
        Assert.Null(await checkout);
        Assert.Equal("2", model.Snapshot?.PullRequest.Number);
        Assert.True(model.CanCreateReviewThread);
    }

    [Fact]
    public async Task CheckoutRequiresLoadedSupportedRevisionAndOperateAccessButNotProviderWriteAccess()
    {
        var canOperate = false;
        using var model = new PullRequestReviewViewModel(() => _client, _store, () => canOperate);
        Assert.False(model.CanCreateReviewThread);
        _proxy.Read = _ => Task.FromResult(Snapshot("1") with { Repository = Snapshot("1").Repository with { CanWrite = false } });
        await Load(model, "1");
        Assert.False(model.CanCreateReviewThread);
        Assert.Null(await model.CreateReviewThreadAsync());
        canOperate = true;
        Assert.True(model.CanCreateReviewThread);
        Assert.False(model.CanWriteReview);
        await model.LoadAsync(_project, new(_project), Snapshot("1").PullRequest with { Provider = SourceControlProvider.GitLab });
        Assert.False(model.CanCreateReviewThread);
    }

    [Fact]
    public async Task DetailsAndCommentEditsSurviveSelectionChangesAndReopen()
    {
        _proxy.Read = request => Task.FromResult(ManagementSnapshot(request.Number));
        using (var model = Model())
        {
            await Load(model, "1");
            model.SetEditedTitle("My PR title");
            model.SetEditedDescription("My PR description");
            model.SelectComment(Assert.Single(model.EditableComments));
            model.SetEditedCommentBody("My comment edit");
            await Load(model, "2");
            Assert.Equal("Review", model.EditedTitle);
            Assert.Empty(model.EditedCommentBody);
        }
        using var reopened = Model();
        await Load(reopened, "1");
        Assert.Equal("My PR title", reopened.EditedTitle);
        Assert.Equal("My PR description", reopened.EditedDescription);
        Assert.Equal("My comment edit", reopened.EditedCommentBody);
        Assert.True(reopened.CanSaveDetails);
        Assert.True(reopened.CanSaveComment);
    }

    [Fact]
    public async Task LiveRefreshPreservesEditsAndDetectsConflictsBeforeWriting()
    {
        var current = ManagementSnapshot("1");
        _proxy.Read = _ => Task.FromResult(current);
        using var model = Model();
        await Load(model, "1");
        model.SetEditedTitle("My title");
        model.SelectComment(Assert.Single(model.EditableComments));
        model.SetEditedCommentBody("My comment");
        model.SetBody("My unsent review");
        model.SetReplyBody("My reply");
        current = current with { PullRequest = current.PullRequest with { Title = "Changed elsewhere" },
            Discussions = [current.Discussions[0] with { Comments = [current.Discussions[0].Comments[0] with { Body = "Changed comment" }] }],
            Checks = [new("Build", "COMPLETED", "SUCCESS")] };
        await model.RefreshLiveAsync();
        Assert.Equal("My title", model.EditedTitle);
        Assert.Equal("My comment", model.EditedCommentBody);
        Assert.Equal("My unsent review", model.Body);
        Assert.Equal("My reply", model.ReplyBody);
        Assert.Contains("SUCCESS", model.ChecksSummary);
        Assert.True(model.DetailsConflict);
        Assert.True(model.CommentConflict);
        Assert.Null(await model.ManageAsync(PullRequestManagementAction.EditDetails));
        model.UseLatestDetails(keepEdits: true);
        ManagePullRequestRequest? captured = null;
        _proxy.Manage = request => { captured = request; return Task.FromResult(new SourceControlOperationResult(true, "Saved", OperationId: request.OperationId)); };
        Assert.True((await model.ManageAsync(PullRequestManagementAction.EditDetails))?.Succeeded);
        Assert.Equal("Changed elsewhere", captured?.ExpectedTitle);
        Assert.Equal("My title", captured?.Title);
        Assert.Equal("My unsent review", model.Body);
        Assert.Equal("My comment", model.EditedCommentBody);
    }

    [Fact]
    public async Task LongPullRequestDescriptionsDoNotPreventSavingOtherDrafts()
    {
        var description = new string('x', 50_000);
        _proxy.Read = request => Task.FromResult(ManagementSnapshot(request.Number) with { Body = description });
        using var model = Model();
        await Load(model, "1");
        model.SetBody("Keep this review");
        model.SetEditedTitle("Updated title");
        await model.SaveNowAsync();
        var saved = await _store.LoadAsync(_project, "github.com/owner/repo", "1");
        Assert.Equal("Keep this review", saved?.Body);
        Assert.Equal(description, saved?.Management?.Body);
        Assert.Equal(description, saved?.Management?.ExpectedBody);
        Assert.Equal("Updated title", saved?.Management?.Title);
    }

    [Fact]
    public async Task UncertainManagementOperationPersistsPayloadAndLocksOnReopen()
    {
        _proxy.Read = request => Task.FromResult(ManagementSnapshot(request.Number));
        _proxy.Manage = _ => throw new IOException("lost response");
        using (var model = Model())
        {
            await Load(model, "1");
            model.SetEditedDescription("Retain description");
            Assert.Null(await model.ManageAsync(PullRequestManagementAction.EditDetails));
            Assert.True(model.HasPendingOperation);
        }
        using var reopened = Model();
        await Load(reopened, "1");
        Assert.False(reopened.CanManage);
        Assert.Equal("Retain description", reopened.EditedDescription);
        var saved = await _store.LoadAsync(_project, "github.com/owner/repo", "1");
        Assert.Equal("Manage", saved?.PendingAction);
        Assert.Equal("Retain description", saved?.Management?.PendingRequest?.Body);
        Assert.Equal("Description", saved?.Management?.PendingRequest?.ExpectedBody);
        await reopened.DiscardDraftAsync();
        Assert.True(reopened.HasPendingOperation);
    }

    [Fact]
    public async Task ConfirmedManagementRejectionUnlocksRetryAndKeepsText()
    {
        _proxy.Read = request => Task.FromResult(ManagementSnapshot(request.Number));
        _proxy.Manage = request => Task.FromResult(new SourceControlOperationResult(false, "Permission changed", OperationId: request.OperationId, State: CommandReceiptState.Rejected));
        using var model = Model();
        await Load(model, "1");
        model.SetEditedTitle("Preserve title");
        Assert.False((await model.ManageAsync(PullRequestManagementAction.EditDetails))?.Succeeded);
        Assert.True(model.CanSaveDetails);
        Assert.False(model.HasPendingOperation);
        Assert.Equal("Preserve title", model.EditedTitle);
        Assert.Equal("Preserve title", (await _store.LoadAsync(_project, "github.com/owner/repo", "1"))?.Management?.Title);
    }

    [Fact]
    public async Task RecoveringConfirmedManagementSuccessKeepsOtherUnsentDrafts()
    {
        _proxy.Read = request => Task.FromResult(ManagementSnapshot(request.Number));
        _proxy.Manage = _ => throw new IOException("lost response");
        using (var model = Model())
        {
            await Load(model, "1");
            model.SetEditedTitle("Saved title");
            model.SelectComment(Assert.Single(model.EditableComments));
            model.SetEditedCommentBody("Unsent comment edit");
            model.SetBody("Unsent review");
            await model.ManageAsync(PullRequestManagementAction.EditDetails);
        }
        using var reopened = Model();
        await Load(reopened, "1");
        var operation = reopened.PendingOperationId!.Value;
        _proxy.Operations = () => Task.FromResult<IReadOnlyList<HostingOperation>>([new(operation, "Manage pull request",
            CommandReceiptState.Completed, new(true, "Saved", OperationId: operation), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
        await reopened.RecoverPendingAsync();
        Assert.False(reopened.HasPendingOperation);
        Assert.Equal("Saved title", reopened.EditedTitle);
        Assert.Equal("Unsent comment edit", reopened.EditedCommentBody);
        Assert.Equal("Unsent review", reopened.Body);
        var saved = await _store.LoadAsync(_project, "github.com/owner/repo", "1");
        Assert.Null(saved?.Management?.PendingRequest);
        Assert.Null(saved?.PendingOperationId);
        Assert.Equal("Saved title", saved?.Management?.ExpectedTitle);
    }

    [Fact]
    public async Task LiveRefreshIgnoresSelectionEventsWhileUpdatingCollections()
    {
        _proxy.Read = request => Task.FromResult(ManagementSnapshot(request.Number));
        using var model = Model();
        await Load(model, "1");
        model.SelectComment(Assert.Single(model.EditableComments));
        model.Discussions.CollectionChanged += (_, _) => model.SelectedDiscussion = null;
        model.EditableComments.CollectionChanged += (_, _) => model.SelectComment(null);
        await model.RefreshLiveAsync();
        Assert.Equal("thread-1", model.ReplyThreadId);
        Assert.Equal("thread-1", model.SelectedDiscussion?.Id);
        Assert.Equal("comment", model.SelectedComment?.Id);
    }

    [Fact]
    public async Task LiveRefreshCannotOverwriteNewSelectionOrACompletedMutation()
    {
        var pending = new TaskCompletionSource<PullRequestReviewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _proxy.Read = request => Task.FromResult(ManagementSnapshot(request.Number));
        using var model = Model();
        await Load(model, "1");
        _proxy.Read = _ => pending.Task;
        var refresh = model.RefreshLiveAsync();
        model.SetEditedTitle("Newest title");
        _proxy.Manage = request => Task.FromResult(new SourceControlOperationResult(true, "Saved", OperationId: request.OperationId));
        Assert.True((await model.ManageAsync(PullRequestManagementAction.EditDetails))?.Succeeded);
        pending.SetResult(ManagementSnapshot("1") with { PullRequest = ManagementSnapshot("1").PullRequest with { Title = "Stale response" } });
        await refresh;
        Assert.Equal("Newest title", model.EditedTitle);
        Assert.NotEqual("Stale response", model.Snapshot?.PullRequest.Title);

        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        refresh = model.RefreshLiveAsync();
        _proxy.Read = request => Task.FromResult(ManagementSnapshot(request.Number));
        await Load(model, "2");
        pending.SetResult(ManagementSnapshot("1"));
        await refresh;
        Assert.Equal("2", model.Snapshot?.PullRequest.Number);
        Assert.Equal("Review", model.EditedTitle);
    }

    [Fact]
    public async Task ChangedHeadKeepsDiffSelectionAndDraftsButBlocksWrites()
    {
        _proxy.Read = request => Task.FromResult(ManagementSnapshot(request.Number));
        using var model = Model();
        await Load(model, "1");
        model.SelectedLine = Assert.Single(model.Lines);
        Assert.True(model.AddInlineComment("Keep line comment"));
        model.SetEditedTitle("Keep title");
        var line = model.SelectedLine;
        _proxy.Read = _ => Task.FromResult(ManagementSnapshot("1") with { HeadCommitId = "new-head" });
        await model.RefreshLiveAsync();
        Assert.True(model.IsStaleHead);
        Assert.False(model.CanManage);
        Assert.False(model.CanSubmit);
        Assert.Same(line, model.SelectedLine);
        Assert.Single(model.InlineComments);
        Assert.Equal("Keep title", model.EditedTitle);
        await model.SaveNowAsync();
        Assert.Equal("head", (await _store.LoadAsync(_project, "github.com/owner/repo", "1"))?.HeadCommitId);
        await model.DiscardDraftAsync();
        Assert.True(model.IsStaleHead);
        Assert.False(model.CanManage);
        await model.ReloadAsync();
        Assert.False(model.IsStaleHead);
        Assert.True(model.CanManage);
    }

    [Fact]
    public async Task MetadataRemovalAndCommentPermissionsFollowCurrentSelection()
    {
        _proxy.Read = _ => Task.FromResult(ManagementSnapshot("1") with { PullRequest = ManagementSnapshot("1").PullRequest with { Labels = ["bug"], Reviewers = ["owner/team"] } });
        using var model = Model();
        await Load(model, "1");
        Assert.False(model.CanRemoveLabel);
        model.SelectedLabel = "bug";
        model.SelectedReviewer = "owner/team";
        Assert.True(model.CanRemoveLabel);
        Assert.True(model.CanRemoveReviewer);
        var captured = new List<ManagePullRequestRequest>();
        _proxy.Manage = request => { captured.Add(request); return Task.FromResult(new SourceControlOperationResult(true, "Removed", OperationId: request.OperationId)); };
        await model.ManageAsync(PullRequestManagementAction.RemoveLabel, "bug");
        await model.ManageAsync(PullRequestManagementAction.RemoveReviewer, "owner/team");
        Assert.Collection(captured, request => Assert.Equal("bug", request.ItemId), request => Assert.Equal("owner/team", request.ItemId));
        model.SelectComment(Assert.Single(model.EditableComments) with { CanDelete = false });
        // Selection is resolved from the loaded collection, never trusted from a caller-supplied flag.
        Assert.True(model.CanDeleteComment);
        model.Suspend();
        Assert.False(model.CanManage);
    }

    [Fact]
    public async Task AdvancedMutationPreservesDraftsAndCapturesMergeMethodForRecovery()
    {
        _proxy.Read = request => Task.FromResult(AdvancedSnapshot(request.Number));
        _proxy.Manage = _ => throw new IOException("response lost");
        using (var model = Model())
        {
            await Load(model, "1");
            model.SetEditedTitle("Keep local title");
            model.SetBody("Keep local review");
            model.SelectedMergeMethod = PullRequestMergeMethod.Rebase;
            Assert.True(model.CanMerge);
            await model.ManageAsync(PullRequestManagementAction.Merge);
            Assert.False(model.CanMerge);
        }
        var saved = await _store.LoadAsync(_project, "github.com/owner/repo", "1");
        Assert.Equal(PullRequestMergeMethod.Rebase, saved?.Management?.PendingRequest?.MergeMethod);
        Assert.Equal("Keep local title", saved?.Management?.Title);
        Assert.Equal("Keep local review", saved?.Body);
        using var reopened = Model();
        await Load(reopened, "1");
        Assert.True(reopened.HasPendingOperation);
        Assert.False(reopened.CanUpdateBranch);
        Assert.False(reopened.CanReactToPullRequest);
    }

    [Fact]
    public async Task ReactionsToggleFromHostedStateAndAllowCommentsByOtherAuthors()
    {
        var snapshot = AdvancedSnapshot("1");
        _proxy.Read = _ => Task.FromResult(snapshot);
        using var model = Model();
        await Load(model, "1");
        model.SelectComment(Assert.Single(model.EditableComments));
        Assert.False(model.CanEditComment);
        Assert.True(model.CanReactToComment);
        var captured = new List<ManagePullRequestRequest>();
        _proxy.Manage = request => { captured.Add(request); return Task.FromResult(new SourceControlOperationResult(true, "Updated", OperationId: request.OperationId)); };
        await model.ToggleReactionAsync(PullRequestReactionContent.Heart, true);
        await model.ToggleReactionAsync(PullRequestReactionContent.Eyes);
        Assert.Collection(captured,
            request => { Assert.Equal("comment", request.ItemId); Assert.False(request.Reacted); Assert.Equal(PullRequestReactionContent.Heart, request.Reaction); },
            request => { Assert.Null(request.ItemId); Assert.True(request.Reacted); });
        snapshot = snapshot with { HeadCommitId = "changed" };
        await model.RefreshLiveAsync();
        Assert.Null(await model.ToggleReactionAsync(PullRequestReactionContent.Eyes));
        Assert.Equal(2, captured.Count);
    }

    [Fact]
    public async Task WorkflowPagesKeepSelectionScopedAndDoNotApplyAfterSwitchingPullRequests()
    {
        _proxy.Read = request => Task.FromResult(AdvancedSnapshot(request.Number));
        _proxy.Workflows = request => Task.FromResult(new PullRequestWorkflowsResult(request.Target, [new("91", "CI", "Awaiting approval", "https://github.com/owner/repo/actions/runs/91")], request.Page == 1 ? 2 : null));
        using var model = Model();
        await Load(model, "1");
        await model.LoadWorkflowsAsync();
        model.SelectedWorkflow = Assert.Single(model.Workflows);
        Assert.True(model.CanApproveWorkflow);
        await model.LoadWorkflowsAsync(true);
        Assert.Single(model.Workflows);
        Assert.False(model.CanLoadMoreWorkflows);
        Assert.Null(await model.ManageAsync(PullRequestManagementAction.ApproveWorkflow, "wrong-id"));
        model.SelectedWorkflow = new("92", "Injected", "Awaiting approval", "");
        Assert.False(model.CanApproveWorkflow);
        var pending = new TaskCompletionSource<PullRequestWorkflowsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        GetPullRequestWorkflowsRequest? captured = null;
        _proxy.Workflows = request => { captured = request; return pending.Task; };
        var load = model.LoadWorkflowsAsync();
        await Load(model, "2");
        pending.SetResult(new(captured!.Target, [new("91", "Stale CI", "Awaiting approval", "")], null));
        await load;
        Assert.Empty(model.Workflows);
        Assert.Null(model.SelectedWorkflow);
        Assert.False(model.CanApproveWorkflow);
        Assert.True(model.CanLoadWorkflows);
    }

    [Fact]
    public async Task WorkflowLoadCannotEnableApprovalAfterHeadChangesOrMutationStarts()
    {
        var snapshot = AdvancedSnapshot("1");
        _proxy.Read = _ => Task.FromResult(snapshot);
        using var model = Model();
        await Load(model, "1");
        var pending = new TaskCompletionSource<PullRequestWorkflowsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        GetPullRequestWorkflowsRequest? captured = null;
        _proxy.Workflows = request => { captured = request; return pending.Task; };
        var load = model.LoadWorkflowsAsync();
        snapshot = snapshot with { HeadCommitId = "new-head" };
        await model.RefreshLiveAsync();
        pending.SetResult(new(captured!.Target, [new("91", "Old CI", "Awaiting approval", "")], null));
        await load;
        Assert.Empty(model.Workflows);
        Assert.False(model.CanApproveWorkflow);
        Assert.False(model.CanMerge);
    }

    private static PullRequestReviewSnapshot AdvancedSnapshot(string number)
    {
        var snapshot = ManagementSnapshot(number);
        return snapshot with { Advanced = new(Enum.GetValues<PullRequestMergeMethod>(), true, true, false, true, false, true, false, "MERGEABLE", "CLEAN"), CanReact = true,
            Discussions = [snapshot.Discussions[0] with { Comments = [snapshot.Discussions[0].Comments[0] with { CanEdit = false, CanDelete = false, CanReact = true,
                Reactions = [new(PullRequestReactionContent.Heart, 2, true)] }] }] };
    }

    private static PullRequestReviewSnapshot ManagementSnapshot(string number)
    {
        var snapshot = Snapshot(number);
        return snapshot with { CanEditDetails = true, CanManageMetadata = true,
            Discussions = [snapshot.Discussions[0] with { Comments = [new("comment", "reviewer", "Original comment", DateTimeOffset.UtcNow, CanEdit: true, CanDelete: true)] }] };
    }

    private ThreadDescriptor ReviewThread() => new(EnvironmentId.New(), ThreadId.New(), _project,
        "Review PR #1", "session", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

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
        public Func<CreatePullRequestReviewThreadRequest, Task<ThreadDescriptor>> Checkout { get; set; } = _ => throw new InvalidOperationException("Unexpected checkout");
        public Func<ManagePullRequestRequest, Task<SourceControlOperationResult>> Manage { get; set; } = _ => throw new InvalidOperationException("Unexpected management write");
        public Func<GetPullRequestWorkflowsRequest, Task<PullRequestWorkflowsResult>> Workflows { get; set; } = _ => throw new InvalidOperationException("Unexpected workflow read");
        public Func<Task<IReadOnlyList<HostingOperation>>> Operations { get; set; } = () => Task.FromResult<IReadOnlyList<HostingOperation>>([]);
        public Func<SubmitPullRequestReviewRequest, Task<SourceControlOperationResult>> Submit { get; set; } = _ => throw new InvalidOperationException("Unexpected write");
        public Func<SetPullRequestThreadResolvedRequest, Task<SourceControlOperationResult>> Resolve { get; set; } = _ => throw new InvalidOperationException("Unexpected write");
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IEnvironmentClient.GetPullRequestReviewAsync) => Read((GetPullRequestReviewRequest)args![0]!),
            nameof(IEnvironmentClient.CreatePullRequestReviewThreadAsync) => Checkout((CreatePullRequestReviewThreadRequest)args![0]!),
            nameof(IEnvironmentClient.ManagePullRequestAsync) => Manage((ManagePullRequestRequest)args![0]!),
            nameof(IEnvironmentClient.GetPullRequestWorkflowsAsync) => Workflows((GetPullRequestWorkflowsRequest)args![0]!),
            nameof(IEnvironmentClient.ListHostingOperationsAsync) => Operations(),
            nameof(IEnvironmentClient.SubmitPullRequestReviewAsync) => Submit((SubmitPullRequestReviewRequest)args![0]!),
            nameof(IEnvironmentClient.SetPullRequestThreadResolvedAsync) => Resolve((SetPullRequestThreadResolvedRequest)args![0]!),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(targetMethod?.Name),
        };
    }
}
