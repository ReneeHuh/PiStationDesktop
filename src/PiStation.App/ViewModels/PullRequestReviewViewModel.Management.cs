using System.Collections.ObjectModel;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class PullRequestReviewViewModel
{
    private PullRequestManagementDraft? _managementDraft;
    private bool _managementSubmitting;
    private bool _liveRefreshRunning;
    private bool _updatingManagement;
    private bool _updatingDiscussions;
    private bool _liveRevisionChanged;
    private long _mutationGeneration;
    private string _liveStatus = "Status refreshes every 30 seconds while this dialog is open.";
    private string? _selectedLabel;
    private string? _selectedReviewer;

    public string? SelectedLabel { get => _selectedLabel; set { if (SetProperty(ref _selectedLabel, value)) RaiseManagementState(); } }
    public string? SelectedReviewer { get => _selectedReviewer; set { if (SetProperty(ref _selectedReviewer, value)) RaiseManagementState(); } }
    public bool CanRemoveLabel => CanRemoveMetadata && ReviewCapabilities.RemoveLabels && Snapshot!.PullRequest.Labels.Contains(SelectedLabel);
    public bool CanRemoveReviewer => CanRemoveMetadata && ReviewCapabilities.RemoveReviewers && Snapshot!.PullRequest.Reviewers.Contains(SelectedReviewer);

    public void Suspend(string? status = null)
    {
        _hasLoaded = false;
        _loadCancellation?.Cancel();
        Interlocked.Increment(ref _loadGeneration);
        if (status is not null) SetStatus(status);
        RaiseState();
    }

    public ObservableCollection<PullRequestReviewComment> EditableComments { get; } = [];
    public string EditedTitle => _managementDraft?.Title ?? string.Empty;
    public string EditedDescription => _managementDraft?.Body ?? string.Empty;
    public string EditedCommentBody => _managementDraft?.CommentBody ?? string.Empty;
    public PullRequestReviewComment? SelectedComment => EditableComments.FirstOrDefault(comment => comment.Id == _managementDraft?.CommentId);
    public bool HasDetailsEdits => _managementDraft is { } draft && (draft.Title != draft.ExpectedTitle || draft.Body != draft.ExpectedBody);
    public bool HasCommentEdits => _managementDraft is { CommentId: not null } draft && draft.CommentBody != draft.ExpectedCommentBody;
    public bool HasManagementEdits => HasDetailsEdits || HasCommentEdits;
    public bool DetailsConflict => _managementDraft is { } draft && Snapshot is { } snapshot &&
        (snapshot.PullRequest.Title != draft.ExpectedTitle || snapshot.Body != draft.ExpectedBody);
    public bool CommentConflict => SelectedComment is { } comment && comment.Body != _managementDraft?.ExpectedCommentBody;
    public string EditConflictNotice => DetailsConflict || CommentConflict
        ? "The hosted text changed. Your edits are saved. Compare with the latest text, then keep your edits or discard them." : string.Empty;
    public string LatestCommentBody => SelectedComment?.Body ?? string.Empty;
    public string DraftActionLabel => Snapshot?.PullRequest.IsDraft == true ? "Mark ready for review" : "Convert to draft";
    public string LiveStatus { get => _liveStatus; private set => SetProperty(ref _liveStatus, value); }
    public bool CanManage => CanEditDraft && !IsStaleHead && !_managementSubmitting && _canOperate() && CanReadReview;
    public bool CanEditDetails => CanManage && Snapshot?.CanEditDetails == true;
    public bool CanSaveDetails => CanEditDetails && HasDetailsEdits && !DetailsConflict && !string.IsNullOrWhiteSpace(EditedTitle);
    public bool CanChangeDraft => CanEditDetails && Snapshot?.Repository.Provider != SourceControlProvider.Bitbucket && Snapshot?.PullRequest.State is PullRequestState.Open or PullRequestState.Draft;
    public bool CanRemoveMetadata => CanManage && Snapshot?.CanManageMetadata == true;
    public bool CanEditComment => CanManage && SelectedComment?.CanEdit == true;
    public bool CanSaveComment => CanEditComment && HasCommentEdits && !CommentConflict && !string.IsNullOrWhiteSpace(EditedCommentBody);
    public bool CanDeleteComment => CanManage && SelectedComment?.CanDelete == true && !CommentConflict;

    public void SetEditedTitle(string value)
    {
        if (_updatingManagement || !CanEditDetails || _managementDraft is null || value == EditedTitle) return;
        _managementDraft = _managementDraft with { Title = value }; ManagementEdited();
    }
    public void SetEditedDescription(string value)
    {
        if (_updatingManagement || !CanEditDetails || _managementDraft is null || value == EditedDescription) return;
        _managementDraft = _managementDraft with { Body = value }; ManagementEdited();
    }
    public void SetEditedCommentBody(string value)
    {
        if (_updatingManagement || !CanEditComment || _managementDraft is null || value == EditedCommentBody) return;
        _managementDraft = _managementDraft with { CommentBody = value }; ManagementEdited();
    }
    public void SelectComment(PullRequestReviewComment? comment)
    {
        if (_updatingManagement || !CanManage || _managementDraft is null || comment?.Id == _managementDraft.CommentId) return;
        if (HasCommentEdits && comment?.Id != _managementDraft.CommentId)
        { SetStatus("Save or discard the current comment edits before selecting another comment."); OnPropertyChanged(nameof(SelectedComment)); return; }
        _managementDraft = _managementDraft with { CommentId = comment?.Id, CommentBody = comment?.Body ?? "", ExpectedCommentBody = comment?.Body };
        ManagementEdited();
    }
    public void UseLatestDetails(bool keepEdits = false)
    {
        if (!CanManage || Snapshot is not { } snapshot || _managementDraft is null) return;
        _managementDraft = _managementDraft with { ExpectedTitle = snapshot.PullRequest.Title, ExpectedBody = snapshot.Body,
            Title = keepEdits ? EditedTitle : snapshot.PullRequest.Title, Body = keepEdits ? EditedDescription : snapshot.Body };
        ManagementEdited();
    }
    public void UseLatestComment(bool keepEdits = false)
    {
        if (!CanManage || _managementDraft is null) return;
        var comment = SelectedComment;
        _managementDraft = _managementDraft with { ExpectedCommentBody = comment?.Body,
            CommentId = comment?.Id, CommentBody = keepEdits && comment is not null ? EditedCommentBody : comment?.Body ?? "" };
        ManagementEdited();
    }

    private void ManagementEdited() { ScheduleSave(); RaiseManagementState(); }
    private void LoadManagementDraft(PullRequestReviewSnapshot snapshot, PullRequestManagementDraft? draft)
    {
        _managementDraft = draft ?? new(snapshot.PullRequest.Title, snapshot.Body, snapshot.PullRequest.Title, snapshot.Body);
        RefreshManagementComments();
        if (!HasDetailsEdits && draft?.PendingRequest is null)
            _managementDraft = _managementDraft with { Title = snapshot.PullRequest.Title, Body = snapshot.Body, ExpectedTitle = snapshot.PullRequest.Title, ExpectedBody = snapshot.Body };
        RaiseManagementState();
    }
    private void RefreshManagementComments()
    {
        _updatingManagement = true;
        try
        {
            EditableComments.Clear();
            foreach (var comment in Discussions.SelectMany(discussion => discussion.Comments).DistinctBy(comment => comment.Id)) EditableComments.Add(comment);
        }
        finally { _updatingManagement = false; }
        RaiseManagementState();
    }

    public async Task<SourceControlOperationResult?> ManageAsync(PullRequestManagementAction action, string? item = null,
        PullRequestReactionContent? reaction = null, bool? reacted = null, CancellationToken cancellationToken = default)
    {
        if (!CanManage || Snapshot is not { } snapshot || _managementDraft is not { } draft ||
            _capturedTarget is not { } workspace || _projectId is not { } projectId) return null;
        if (!(action switch
        {
            PullRequestManagementAction.EditDetails => CanSaveDetails,
            PullRequestManagementAction.SetDraft => CanChangeDraft,
            PullRequestManagementAction.EditComment => CanSaveComment,
            PullRequestManagementAction.DeleteComment => CanDeleteComment,
            PullRequestManagementAction.RemoveLabel => CanRemoveMetadata && ReviewCapabilities.RemoveLabels && snapshot.PullRequest.Labels.Contains(item),
            PullRequestManagementAction.RemoveReviewer => CanRemoveMetadata && ReviewCapabilities.RemoveReviewers && snapshot.PullRequest.Reviewers.Contains(item),
            _ => CanPerformAdvancedAction(action, item, reaction, reacted)
        })) return null;
        var request = new ManagePullRequestRequest(new(workspace, PullRequestReviewDefaults.RepositoryKey(snapshot.Repository), snapshot.PullRequest.Number, snapshot.HeadCommitId),
            action, action == PullRequestManagementAction.EditDetails ? draft.Title.Trim() : null,
            action == PullRequestManagementAction.EditDetails ? draft.Body : action == PullRequestManagementAction.EditComment ? draft.CommentBody : null,
            action == PullRequestManagementAction.SetDraft ? !snapshot.PullRequest.IsDraft : null,
            action is PullRequestManagementAction.EditComment or PullRequestManagementAction.DeleteComment ? draft.CommentId : item,
            action == PullRequestManagementAction.EditDetails ? draft.ExpectedTitle : null,
            action == PullRequestManagementAction.EditDetails ? draft.ExpectedBody : draft.ExpectedCommentBody,
            action == PullRequestManagementAction.SetDraft ? snapshot.PullRequest.IsDraft : null,
            MergeMethod: action is PullRequestManagementAction.Merge or PullRequestManagementAction.EnableAutoMerge ? SelectedMergeMethod : null,
            UpdateMethod: action == PullRequestManagementAction.UpdateBranch ? SelectedUpdateMethod : null,
            Reaction: reaction, Reacted: reacted);
        var generation = _loadGeneration;
        _managementSubmitting = true;
        _managementDraft = draft with { PendingRequest = request };
        RaiseManagementState();
        try
        {
            var client = _clientFactory();
            var operation = await BeginPendingAsync(projectId, snapshot, "Manage", null, CreateDraft(snapshot, ReplyThreadId), cancellationToken);
            if (operation is null) return null;
            var result = await client.ManagePullRequestAsync(request with { OperationId = operation }, cancellationToken);
            return await FinishOperationAsync(result, operation.Value, false, cancellationToken);
        }
        catch (Exception exception)
        {
            if (generation == _loadGeneration) SetStatus(PendingOperationId is null
                ? $"The change was not sent: {exception.Message}" : $"The change's outcome is unknown. Refresh operation history before retrying. {exception.Message}");
            return null;
        }
        finally
        {
            _managementSubmitting = false;
            if (generation == _loadGeneration && PendingOperationId is null && _managementDraft is not null)
                _managementDraft = _managementDraft with { PendingRequest = null };
            RaiseManagementState();
        }
    }

    private void CompleteManagementAction(SourceControlOperationResult result, string? action)
    {
        if (action != "Manage" || _managementDraft is not { PendingRequest: { } request } draft) return;
        if (result.Succeeded)
        {
            if (request.Action is PullRequestManagementAction.ApproveWorkflow or PullRequestManagementAction.UpdateBranch or PullRequestManagementAction.Merge)
                ResetWorkflows();
            if (request.Action == PullRequestManagementAction.EditDetails)
                draft = draft with { Title = request.Title!, Body = request.Body!, ExpectedTitle = request.Title!, ExpectedBody = request.Body! };
            if (request.Action is PullRequestManagementAction.EditComment or PullRequestManagementAction.DeleteComment)
                draft = draft with { CommentId = null, CommentBody = "", ExpectedCommentBody = null };
        }
        _managementDraft = draft with { PendingRequest = null };
    }

    public async Task RefreshLiveAsync(CancellationToken cancellationToken = default)
    {
        if (!_hasLoaded || _disposed || IsBusy || PendingOperationId is not null || _isDiscarding || _managementSubmitting ||
            _liveRefreshRunning || Snapshot is not { } original || _capturedTarget is not { } target) return;
        var generation = _loadGeneration;
        var mutation = _mutationGeneration;
        _liveRefreshRunning = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _loadCancellation?.Token ?? CancellationToken.None);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var fresh = await _clientFactory().GetPullRequestReviewAsync(new(target, original.PullRequest.Number), timeout.Token);
            if (_disposed || generation != _loadGeneration || mutation != _mutationGeneration || PendingOperationId is not null ||
                !ReferenceEquals(Snapshot, original) || cancellationToken.IsCancellationRequested) return;
            if (fresh.PullRequest.Number != original.PullRequest.Number ||
                PullRequestReviewDefaults.RepositoryKey(fresh.Repository) != PullRequestReviewDefaults.RepositoryKey(original.Repository))
                throw new InvalidDataException("The refresh returned another pull request.");
            var sameRevision = fresh.HeadCommitId == original.HeadCommitId && fresh.BaseCommitId == original.BaseCommitId;
            if (!sameRevision) { _liveRevisionChanged = true; IsStaleHead = true; }
            // Keep loaded diff pages and their selections. Dynamic connections restart at the current first page.
            var pages = sameRevision ? (original.NextPages ?? []).Where(page => page.Kind is PullRequestReviewPageKind.Files or PullRequestReviewPageKind.Commits)
                .Concat((fresh.NextPages ?? []).Where(page => page.Kind is not (PullRequestReviewPageKind.Files or PullRequestReviewPageKind.Commits))).ToArray() : [];
            Snapshot = fresh with { Files = original.Files, Commits = original.Commits, HeadCommitId = original.HeadCommitId,
                BaseCommitId = original.BaseCommitId, NextPages = pages };
            PullRequest = fresh.PullRequest;
            Repository = fresh.Repository;
            var discussionId = SelectedDiscussion?.Id;
            _updatingDiscussions = true;
            try
            {
                Discussions.Clear();
                foreach (var discussion in fresh.Discussions) Discussions.Add(discussion);
                _selectedDiscussion = Discussions.FirstOrDefault(discussion => discussion.Id == discussionId);
            }
            finally { _updatingDiscussions = false; }
            OnPropertyChanged(nameof(SelectedDiscussion)); OnPropertyChanged(nameof(DiscussionSummary));
            var commentWasEdited = HasCommentEdits;
            RefreshManagementComments();
            if (_managementDraft is { } management)
            {
                if (!HasDetailsEdits) _managementDraft = management with { Title = fresh.PullRequest.Title, Body = fresh.Body,
                    ExpectedTitle = fresh.PullRequest.Title, ExpectedBody = fresh.Body };
                if (!commentWasEdited && SelectedComment is { } comment)
                    _managementDraft = _managementDraft with { CommentBody = comment.Body, ExpectedCommentBody = comment.Body };
            }
            LiveStatus = sameRevision ? $"Status refreshed at {DateTime.Now:t}." : "The PR revision changed. Your drafts are preserved; reload the review before writing.";
            RaiseState();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (generation == _loadGeneration) LiveStatus = $"Automatic refresh paused: {exception.Message} The next refresh will retry.";
        }
        finally { _liveRefreshRunning = false; }
    }

    private void RaiseManagementState()
    {
        RaiseAdvancedState();
        foreach (var property in new[] { nameof(EditedTitle), nameof(EditedDescription), nameof(EditedCommentBody), nameof(SelectedComment),
            nameof(LatestCommentBody), nameof(HasManagementEdits), nameof(DetailsConflict), nameof(CommentConflict), nameof(EditConflictNotice),
            nameof(CanManage), nameof(CanEditDetails), nameof(CanSaveDetails), nameof(CanChangeDraft), nameof(CanRemoveMetadata),
            nameof(CanEditComment), nameof(CanSaveComment), nameof(CanDeleteComment), nameof(DraftActionLabel) }) OnPropertyChanged(property);
        OnPropertyChanged(nameof(CanRemoveLabel)); OnPropertyChanged(nameof(CanRemoveReviewer));
    }
}
