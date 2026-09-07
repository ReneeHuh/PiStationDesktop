using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using PiStation.ClientRuntime;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.App.ViewModels;

/// <summary>Small presentation model for a hosted pull-request review.</summary>
public sealed class PullRequestReviewLineViewModel
{
    public PullRequestReviewLineViewModel(PullRequestChangedFile file, PullRequestDiffLine line) { File = file; Line = line; }
    public PullRequestChangedFile File { get; }
    public PullRequestDiffLine Line { get; }
    public string Location => $"{Line.OldLine?.ToString(CultureInfo.InvariantCulture) ?? "–"} → {Line.NewLine?.ToString(CultureInfo.InvariantCulture) ?? "–"}";
    public string Kind => Line.Kind.ToString();
    public string Text => Line.Text;
    public bool CanComment => Line.Kind is PullRequestDiffLineKind.Context or
        PullRequestDiffLineKind.Addition or PullRequestDiffLineKind.Deletion;
    public string DisplayText => $"{Location}  {Kind}: {Text}";
}

public sealed class PullRequestReviewViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Func<IEnvironmentClient> _clientFactory;
    private readonly PullRequestReviewDraftStore _draftStore;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _saveCancellation;
    private long _loadGeneration;
    private ProjectId? _projectId;
    private WorkspaceTarget? _capturedTarget;
    private PullRequestDescriptor? _pullRequest;
    private PullRequestReviewSnapshot? _snapshot;
    private SourceControlRepository? _repository;
    private PullRequestChangedFile? _selectedFile;
    private PullRequestReviewLineViewModel? _selectedLine;
    private PullRequestDiscussion? _selectedDiscussion;
    private PullRequestDiffSide _selectedSide = PullRequestDiffSide.Right;
    private PullRequestReviewEvent _reviewEvent = PullRequestReviewEvent.Comment;
    private string _body = string.Empty;
    private string _replyBody = string.Empty;
    private string _status = "Select a GitHub pull request to review.";
    private bool _isBusy;
    private bool _isStaleHead;
    private bool _hasLoaded;
    private CommandId? _pendingOperationId;
    private string? _pendingAction;
    private string? _replyThreadId;
    private string? _draftHeadCommitId;
    private long _pendingGeneration;
    // Discard shares the write gate with operation creation.  A discard that has
    // already started must not race BeginPendingAsync and remove its durable
    // pending receipt after the provider call begins.
    private bool _isDiscarding;
    private long _discardEpoch;
    private long _completedDiscardEpoch = -1;
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public PullRequestReviewViewModel(Func<IEnvironmentClient> clientFactory, PullRequestReviewDraftStore draftStore)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _draftStore = draftStore ?? throw new ArgumentNullException(nameof(draftStore));
    }

    public ObservableCollection<PullRequestChangedFile> Files { get; } = [];
    public ObservableCollection<PullRequestReviewLineViewModel> Lines { get; } = [];
    public ObservableCollection<PullRequestDiscussion> Discussions { get; } = [];
    public ObservableCollection<PullRequestInlineComment> InlineComments { get; } = [];

    public PullRequestReviewSnapshot? Snapshot { get => _snapshot; private set => SetProperty(ref _snapshot, value); }
    public PullRequestDescriptor? PullRequest { get => _pullRequest; private set => SetProperty(ref _pullRequest, value); }
    public SourceControlRepository? Repository { get => _repository; private set => SetProperty(ref _repository, value); }
    public PullRequestChangedFile? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (!SetProperty(ref _selectedFile, value)) return;
            Lines.Clear();
            if (value is not null)
            {
                foreach (var line in value.Lines.Take(PullRequestReviewDefaults.MaximumDiffLines))
                    Lines.Add(new PullRequestReviewLineViewModel(value, line));
            }
            SelectedLine = null;
            OnPropertyChanged(nameof(SelectedFileSummary));
        }
    }

    public PullRequestReviewLineViewModel? SelectedLine
    {
        get => _selectedLine;
        set { if (SetProperty(ref _selectedLine, value)) RaiseState(); }
    }
    public PullRequestDiscussion? SelectedDiscussion
    {
        get => _selectedDiscussion;
        set
        {
            if (_hasLoaded && (PendingOperationId is not null || IsBusy || _isDiscarding)) return;
            if (_hasLoaded && !string.IsNullOrEmpty(ReplyBody) && value?.Id != ReplyThreadId)
            {
                SetStatus("Send or clear the current reply before selecting another discussion.");
                OnPropertyChanged();
                return;
            }
            if (!SetProperty(ref _selectedDiscussion, value)) return;
            ReplyThreadId = value?.Id;
            ScheduleSave();
            OnPropertyChanged(nameof(DiscussionSummary));
            OnPropertyChanged(nameof(CanReply));
            OnPropertyChanged(nameof(CanResolve));
        }
    }

    public PullRequestDiffSide SelectedSide { get => _selectedSide; set { if (SetProperty(ref _selectedSide, value)) RaiseState(); } }
    public PullRequestReviewEvent ReviewEvent
    {
        get => _reviewEvent;
        set
        {
            // Loading applies the persisted event while _hasLoaded is false;
            // once a draft is live, busy/discarding state makes this UI-only
            // mutation unsafe because it could schedule a post-discard save.
            if (PendingOperationId is null && (!_hasLoaded || (!IsBusy && !_isDiscarding)) &&
                SetProperty(ref _reviewEvent, value))
            {
                ScheduleSave();
                RaiseState();
            }
        }
    }
    public string Body { get => _body; private set => SetProperty(ref _body, value); }
    public string ReplyBody { get => _replyBody; private set => SetProperty(ref _replyBody, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseState(); } }
    public bool IsStaleHead { get => _isStaleHead; private set { if (SetProperty(ref _isStaleHead, value)) RaiseState(); } }
    public CommandId? PendingOperationId { get => _pendingOperationId; private set { if (SetProperty(ref _pendingOperationId, value)) RaiseState(); } }
    public string? ReplyThreadId { get => _replyThreadId; private set => SetProperty(ref _replyThreadId, value); }

    public bool CanReadReview => PullRequest is { Provider: var provider } && HostingCapabilities.CanReadReview(provider);
    public bool CanWriteReview => CanReadReview && Repository?.CanWrite == true && PullRequest is { Provider: var provider } && HostingCapabilities.CanWriteReview(provider);
    public bool CanSubmit => _hasLoaded && CanWriteReview && !IsBusy && !IsStaleHead && PendingOperationId is null &&
        Snapshot is not null && Enum.IsDefined(ReviewEvent) &&
        (ReviewEvent == PullRequestReviewEvent.Approve || !string.IsNullOrWhiteSpace(Body)) &&
        Body.Length <= PullRequestReviewDefaults.MaximumBodyCharacters &&
        InlineComments.Count <= PullRequestReviewDefaults.MaximumInlineComments && PayloadWithinLimit();
    public bool CanAddInlineComment => _hasLoaded && CanWriteReview && !IsBusy && !IsStaleHead && PendingOperationId is null &&
        InlineComments.Count < PullRequestReviewDefaults.MaximumInlineComments && Enum.IsDefined(SelectedSide) &&
        SelectedLine is { CanComment: true } && (SelectedSide == PullRequestDiffSide.Left
            ? SelectedLine.Line.OldLine is not null
            : SelectedLine.Line.NewLine is not null);
    public bool CanReply => _hasLoaded && CanWriteReview && !IsBusy && !IsStaleHead && PendingOperationId is null &&
        SelectedDiscussion is { CanReply: true } && !string.IsNullOrWhiteSpace(ReplyBody) &&
        ReplyBody.Length <= PullRequestReviewDefaults.MaximumBodyCharacters;
    public bool CanResolve => _hasLoaded && CanWriteReview && !IsBusy && !IsStaleHead && PendingOperationId is null && SelectedDiscussion?.CanResolve == true;
    public bool HasPendingOperation => PendingOperationId is not null;
    public bool HasSnapshot => Snapshot is not null;
    public bool CanEditDraft => _hasLoaded && !IsBusy && !_isDiscarding && PendingOperationId is null && !_disposed;
    public string Description => Snapshot?.Body ?? string.Empty;
    public string SelectedFileSummary => SelectedFile is null
        ? "Select a file to inspect its hosted patch."
        : $"{SelectedFile.Status} • +{SelectedFile.Additions}/-{SelectedFile.Deletions}" +
          (SelectedFile.PatchUnavailable ? " • patch unavailable" : string.Empty);
    public string DiscussionSummary => SelectedDiscussion is null
        ? "Select a discussion to inspect comments."
        : string.Join("\n", SelectedDiscussion.Comments.Select(comment => $"{comment.Author}: {comment.Body}"));
    public string HeadSummary => Snapshot is null ? string.Empty : $"Head {Snapshot.HeadCommitId} • base {Snapshot.BaseCommitId}";
    public string DetailsSummary => Snapshot is null ? string.Empty :
        $"{Snapshot.PullRequest.Title} • #{Snapshot.PullRequest.Number} • {Snapshot.PullRequest.Author} • " +
        $"{Snapshot.PullRequest.SourceBranch} → {Snapshot.PullRequest.TargetBranch}";
    public string CommitsSummary => Snapshot is null ? string.Empty :
        Snapshot.Commits.Count == 0 ? "No commits reported." :
        string.Join("\n", Snapshot.Commits.Take(PullRequestReviewDefaults.MaximumItems).Select(commit =>
            $"{commit.Sha[..Math.Min(10, commit.Sha.Length)]}  {commit.Title}  ({commit.Author})"));
    public string ChecksSummary => Snapshot is null ? string.Empty :
        Snapshot.Checks.Count == 0 ? "No checks reported." :
        string.Join("\n", Snapshot.Checks.Take(PullRequestReviewDefaults.MaximumItems).Select(check =>
            $"{check.Name}: {check.Conclusion ?? check.Status}"));

    public async Task LoadAsync(ProjectId projectId, WorkspaceTarget target, PullRequestDescriptor pullRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        if (target.ProjectId != projectId) throw new ArgumentException("Review workspace must match the draft project.", nameof(target));
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Serialize the selection flush with discard.  If discard wins the
        // gate first, it clears the in-memory draft and this load must not
        // recreate the deleted file while flushing the previous selection.
        var discardWasInProgress = _isDiscarding;
        var observedDiscardEpoch = Volatile.Read(ref _discardEpoch);
        var skipPreviousDraftFlush = false;
        _saveCancellation?.Cancel();
        long generation;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            generation = Interlocked.Increment(ref _loadGeneration);
            skipPreviousDraftFlush = (discardWasInProgress || observedDiscardEpoch != Volatile.Read(ref _discardEpoch)) &&
                _completedDiscardEpoch == _discardEpoch;
            if (!skipPreviousDraftFlush) await PersistDraftCoreAsync(null, cancellationToken);
        }
        finally { _writeGate.Release(); }
        if (generation != _loadGeneration) return;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _loadCancellation.Token;
        _hasLoaded = false;
        _projectId = projectId;
        _capturedTarget = target;
        PullRequest = pullRequest;
        Snapshot = null;
        Repository = null;
        Files.Clear();
        Lines.Clear();
        Discussions.Clear();
        InlineComments.Clear();
        SelectedFile = null;
        SelectedDiscussion = null;
        // Keep the previous pending identity until the new draft has been read successfully. A corrupt
        // draft must never be replaced by an empty draft from a generic load failure.
        _hasLoaded = false;
        SetStatus("Loading pull-request review…");
        IsBusy = true;
        try
        {
            if (!CanReadReview)
            {
                SetStatus("Detailed review is available for GitHub pull requests only.");
                return;
            }

            var snapshot = await _clientFactory().GetPullRequestReviewAsync(
                new GetPullRequestReviewRequest(target, pullRequest.Number), token);
            if (generation != Volatile.Read(ref _loadGeneration) || token.IsCancellationRequested) return;
            var expectedRepository = pullRequest.Repository.Trim().Trim('/');
            var returnedRepository = $"{snapshot.Repository.Owner}/{snapshot.Repository.Name}";
            if (!string.Equals(snapshot.PullRequest.Number, pullRequest.Number, StringComparison.Ordinal) ||
                !string.Equals(snapshot.PullRequest.Repository.Trim().Trim('/'), expectedRepository, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(returnedRepository, expectedRepository, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The review response belongs to another pull request.");
            Snapshot = snapshot;
            Repository = snapshot.Repository;
            foreach (var file in snapshot.Files.Take(PullRequestReviewDefaults.MaximumFiles)) Files.Add(file);
            foreach (var discussion in snapshot.Discussions.Take(PullRequestReviewDefaults.MaximumItems * 3)) Discussions.Add(discussion);
            SelectedFile = Files.FirstOrDefault();
            SelectedDiscussion = Discussions.FirstOrDefault();
            var repositoryKey = PullRequestReviewDefaults.RepositoryKey(snapshot.Repository);
            var draft = await _draftStore.LoadAsync(projectId, repositoryKey, pullRequest.Number, token);
            if (generation != Volatile.Read(ref _loadGeneration) || token.IsCancellationRequested) return;
            PendingOperationId = null;
            _pendingAction = null;
            _draftHeadCommitId = snapshot.HeadCommitId;
            IsStaleHead = false;
            if (draft is not null && string.Equals(draft.Repository, repositoryKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(draft.Number, pullRequest.Number, StringComparison.Ordinal))
            {
                Body = draft.Body;
                ReviewEvent = draft.Event;
                foreach (var comment in draft.Comments.Take(PullRequestReviewDefaults.MaximumInlineComments)) InlineComments.Add(comment);
                PendingOperationId = draft.PendingOperationId;
                _pendingGeneration = generation;
                _pendingAction = draft.PendingAction;
                ReplyThreadId = draft.ReplyThreadId;
                SelectedDiscussion = Discussions.FirstOrDefault(thread => thread.Id == draft.ReplyThreadId);
                ReplyThreadId = draft.ReplyThreadId;
                ReplyBody = draft.ReplyBody ?? string.Empty;
                _draftHeadCommitId = draft.HeadCommitId;
                IsStaleHead = !string.Equals(draft.HeadCommitId, snapshot.HeadCommitId, StringComparison.Ordinal);
            }
            else
            {
                Body = string.Empty;
                ReviewEvent = PullRequestReviewEvent.Comment;
                ReplyThreadId = SelectedDiscussion?.Id;
                ReplyBody = string.Empty;
            }
            _hasLoaded = true;
            RaiseState();
            SetStatus(IsStaleHead
                ? "Saved review draft is for an older head. Review, re-anchor, or discard it before submitting."
                : PendingOperationId is not null
                    ? $"Operation {PendingOperationId} is pending. Refresh operation history before retrying."
                    : snapshot.Notice ?? "Review loaded.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _loadGeneration)) SetStatus($"Review unavailable: {exception.Message}");
        }
        finally
        {
            if (generation == Volatile.Read(ref _loadGeneration)) IsBusy = false;
        }
    }

    public void SetBody(string value)
    {
        if (PendingOperationId is not null || IsBusy || _isDiscarding || _disposed) return;
        Body = value ?? string.Empty;
        ScheduleSave();
        RaiseState();
    }

    public void SetReplyBody(string value)
    {
        if (PendingOperationId is not null || IsBusy || _isDiscarding || _disposed) return;
        ReplyBody = value ?? string.Empty;
        ScheduleSave();
        RaiseState();
    }

    public bool AddInlineComment(string body)
    {
        if (!CanAddInlineComment || SelectedLine is null || Snapshot is null || string.IsNullOrWhiteSpace(body) || body.Length > PullRequestReviewDefaults.MaximumBodyCharacters) return false;
        var line = SelectedSide == PullRequestDiffSide.Left ? SelectedLine.Line.OldLine : SelectedLine.Line.NewLine;
        if (line is null) return false;
        InlineComments.Add(new PullRequestInlineComment(SelectedLine.File.Path, line.Value, SelectedSide, body.Trim()));
        ScheduleSave();
        RaiseState();
        return true;
    }

    public void RemoveInlineComment(PullRequestInlineComment comment)
    {
        if (PendingOperationId is not null || IsBusy || _isDiscarding || _disposed) return;
        if (InlineComments.Remove(comment)) { ScheduleSave(); RaiseState(); }
    }

    public async Task<SourceControlOperationResult?> SubmitAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSubmit || _projectId is not { } projectId || _capturedTarget is not { } workspace || Snapshot is not { } snapshot)
            return null;
        var capturedEvent = ReviewEvent;
        var capturedBody = Body.Trim();
        var capturedComments = InlineComments.ToArray();
        var capturedReply = ReplyBody;
        var capturedTarget = new PullRequestReviewTarget(workspace, PullRequestReviewDefaults.RepositoryKey(snapshot.Repository), snapshot.PullRequest.Number, snapshot.HeadCommitId);
        CommandId? operationId;
        try { operationId = await BeginPendingAsync(projectId, snapshot, "SubmitReview", null,
            CreateDraft(snapshot, null, capturedBody, capturedEvent, capturedComments, capturedReply), cancellationToken); }
        catch (Exception exception) { SetStatus($"Review was not submitted because its pending draft could not be saved: {exception.Message}"); return null; }
        if (operationId is null) return null;
        try
        {
            var result = await _clientFactory().SubmitPullRequestReviewAsync(
                new SubmitPullRequestReviewRequest(capturedTarget, capturedEvent, capturedBody, capturedComments, operationId), cancellationToken);
            return await FinishOperationAsync(result, operationId.Value, clearAllOnSuccess: true, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { SetStatus("Review submission was cancelled; its outcome is unknown."); return null; }
        catch (Exception exception) { SetStatus($"Review submission outcome is unknown. {exception.Message}"); return null; }
    }

    public Task ReloadAsync(CancellationToken cancellationToken = default) =>
        _projectId is { } projectId && _capturedTarget is { } target && PullRequest is { } pullRequest
            ? LoadAsync(projectId, target, pullRequest, cancellationToken)
            : Task.CompletedTask;

    public async Task<SourceControlOperationResult?> ReplyAsync(CancellationToken cancellationToken = default)
    {
        if (!CanReply || _projectId is not { } projectId || _capturedTarget is not { } workspace || Snapshot is not { } snapshot || ReplyThreadId is null)
            return null;
        var capturedThreadId = ReplyThreadId;
        var capturedReplyBody = ReplyBody.Trim();
        var capturedReviewBody = Body;
        var capturedEvent = ReviewEvent;
        var capturedComments = InlineComments.ToArray();
        var capturedTarget = new PullRequestReviewTarget(workspace, PullRequestReviewDefaults.RepositoryKey(snapshot.Repository), snapshot.PullRequest.Number, snapshot.HeadCommitId);
        CommandId? operationId;
        try { operationId = await BeginPendingAsync(projectId, snapshot, "Reply", capturedThreadId,
            CreateDraft(snapshot, capturedThreadId, capturedReviewBody, capturedEvent, capturedComments, capturedReplyBody), cancellationToken); }
        catch (Exception exception) { SetStatus($"Reply was not sent because its pending draft could not be saved: {exception.Message}"); return null; }
        if (operationId is null) return null;
        try
        {
            var result = await _clientFactory().ReplyPullRequestThreadAsync(
                new ReplyPullRequestThreadRequest(capturedTarget, capturedThreadId!, capturedReplyBody, operationId), cancellationToken);
            return await FinishOperationAsync(result, operationId.Value, clearAllOnSuccess: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { SetStatus("Reply was cancelled; its outcome is unknown."); return null; }
        catch (Exception exception) { SetStatus($"Reply outcome is unknown. {exception.Message}"); return null; }
    }

    public async Task<SourceControlOperationResult?> SetResolvedAsync(bool resolved, CancellationToken cancellationToken = default)
    {
        if (!CanResolve || _projectId is not { } projectId || _capturedTarget is not { } workspace || Snapshot is not { } snapshot || ReplyThreadId is null)
            return null;
        var capturedThreadId = ReplyThreadId;
        var capturedReviewBody = Body;
        var capturedEvent = ReviewEvent;
        var capturedComments = InlineComments.ToArray();
        var capturedUnsentReply = ReplyBody;
        var capturedTarget = new PullRequestReviewTarget(workspace, PullRequestReviewDefaults.RepositoryKey(snapshot.Repository), snapshot.PullRequest.Number, snapshot.HeadCommitId);
        CommandId? operationId;
        try { operationId = await BeginPendingAsync(projectId, snapshot, resolved ? "Resolve" : "Unresolve", capturedThreadId,
            CreateDraft(snapshot, capturedThreadId, capturedReviewBody, capturedEvent, capturedComments, capturedUnsentReply), cancellationToken); }
        catch (Exception exception) { SetStatus($"Thread update was not sent because its pending draft could not be saved: {exception.Message}"); return null; }
        if (operationId is null) return null;
        try
        {
            var result = await _clientFactory().SetPullRequestThreadResolvedAsync(
                new SetPullRequestThreadResolvedRequest(capturedTarget, capturedThreadId!, resolved, operationId), cancellationToken);
            return await FinishOperationAsync(result, operationId.Value, clearAllOnSuccess: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { SetStatus("Thread update was cancelled; its outcome is unknown."); return null; }
        catch (Exception exception) { SetStatus($"Thread update outcome is unknown. {exception.Message}"); return null; }
    }

    public async Task RecoverPendingAsync(CancellationToken cancellationToken = default)
    {
        if (PendingOperationId is not { } pending || _projectId is not { } projectId || Snapshot is not { } snapshot)
        {
            SetStatus("There is no pending review operation to recover.");
            return;
        }
        try
        {
            var operation = (await _clientFactory().ListHostingOperationsAsync(cancellationToken))
                .FirstOrDefault(candidate => candidate.OperationId == pending);
            if (operation?.Result is not { } result)
            {
                SetStatus("Operation history has no confirmed outcome yet; the review remains locked.");
                return;
            }
            await FinishOperationAsync(result, pending, clearAllOnSuccess: result.Succeeded &&
                string.Equals(_pendingAction, "SubmitReview", StringComparison.Ordinal), cancellationToken);
        }
        catch (Exception exception) { SetStatus($"Could not refresh operation history: {exception.Message}"); }
    }

    public async Task DiscardDraftAsync(CancellationToken cancellationToken = default)
    {
        var generation = _loadGeneration;
        var ownsBusyState = false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            // Re-check all mutable state after acquiring the same gate used by
            // BeginPendingAsync.  Submit/Reply/Resolve may have won the race
            // while this call was waiting.
            if (generation != _loadGeneration || !_hasLoaded || IsBusy || _disposed ||
                _projectId is not { } projectId || Repository is not { } repository ||
                PullRequest is not { } pullRequest)
                return;
            if (PendingOperationId is not null)
            {
                SetStatus("The saved draft is locked by an uncertain operation. Refresh operation history before discarding it.");
                return;
            }

            _isDiscarding = true;
            Interlocked.Increment(ref _discardEpoch);
            IsBusy = true;
            ownsBusyState = true;
            _saveCancellation?.Cancel();
            var repositoryKey = PullRequestReviewDefaults.RepositoryKey(repository);
            try
            {
                await _draftStore.DeleteAsync(projectId, repositoryKey, pullRequest.Number, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetStatus("Review draft discard was cancelled; the saved draft was retained.");
                return;
            }
            catch (Exception exception)
            {
                SetStatus($"Review draft could not be discarded: {exception.Message}");
                return;
            }

            // The generation and pending identity are checked while the gate is
            // still held.  A newer selection or operation can therefore never
            // be cleared by this discard completion.
            if (generation != _loadGeneration || PendingOperationId is not null) return;
            Body = string.Empty;
            ReplyBody = string.Empty;
            InlineComments.Clear();
            _pendingAction = null;
            IsStaleHead = false;
            _draftHeadCommitId = Snapshot?.HeadCommitId;
            _completedDiscardEpoch = _discardEpoch;
            SetStatus("Review draft discarded.");
            RaiseState();
        }
        finally
        {
            _isDiscarding = false;
            if (ownsBusyState && !_disposed) IsBusy = false;
            _writeGate.Release();
            RaiseState();
        }
    }

    public async Task SaveNowAsync(CancellationToken cancellationToken = default)
    {
        _saveCancellation?.Cancel();
        await PersistDraftAsync(cancellationToken);
    }

    private async Task<CommandId?> BeginPendingAsync(ProjectId projectId, PullRequestReviewSnapshot snapshot,
        string action, string? replyThreadId, PullRequestReviewDraft capturedDraft, CancellationToken cancellationToken)
    {
        var generation = _loadGeneration;
        // A caller that passed its initial CanSubmit check immediately before
        // a discard began must not submit its stale captured payload after the
        // discard completes while it was waiting for the write gate.
        var discardEpoch = Volatile.Read(ref _discardEpoch);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (generation != _loadGeneration || !_hasLoaded || _disposed ||
                !ReferenceEquals(Snapshot, snapshot) || _projectId != projectId || PendingOperationId is not null || IsStaleHead) return null;
            if (_isDiscarding || discardEpoch != Volatile.Read(ref _discardEpoch)) return null;
            _saveCancellation?.Cancel();
            var id = CommandId.New();
            PendingOperationId = id;
            _pendingAction = action;
            _pendingGeneration = _loadGeneration;
            ReplyThreadId = replyThreadId ?? ReplyThreadId;
            try { await PersistDraftCoreAsync(capturedDraft with
                { PendingOperationId = id, ReplyThreadId = replyThreadId ?? capturedDraft.ReplyThreadId, PendingAction = action }, cancellationToken); }
            catch
            {
                if (generation == _loadGeneration) { PendingOperationId = null; _pendingAction = null; }
                throw;
            }
            SetStatus($"Submitting operation {id}…");
            return id;
        }
        finally { _writeGate.Release(); }
    }

    private async Task<SourceControlOperationResult> FinishOperationAsync(SourceControlOperationResult result, CommandId operationId,
        bool clearAllOnSuccess, CancellationToken cancellationToken)
    {
        if (PendingOperationId != operationId || _pendingGeneration != _loadGeneration ||
            (result.OperationId is { } returnedOperation && returnedOperation != operationId))
        {
            SetStatus("A review operation completed for an older review view. Refresh this pull request before continuing.");
            return result;
        }
        if (result.State is not (CommandReceiptState.Completed or CommandReceiptState.Rejected or CommandReceiptState.Failed))
        {
            SetStatus($"Operation {operationId} remains {result.State}; refresh operation history before retrying.");
            return result;
        }
        var completedAction = _pendingAction;
        var generation = _loadGeneration;
        if (result.Succeeded && clearAllOnSuccess)
        {
            if (_projectId is { } projectId && Repository is { } repository && PullRequest is { } pullRequest)
                await _draftStore.DeleteAsync(projectId, PullRequestReviewDefaults.RepositoryKey(repository), pullRequest.Number, cancellationToken);
            if (generation != _loadGeneration) return result;
            Body = string.Empty;
            InlineComments.Clear();
        }
        PendingOperationId = null;
        _pendingAction = null;
        if (result.Succeeded && string.Equals(completedAction, "Reply", StringComparison.Ordinal)) ReplyBody = string.Empty;
        if (!result.Succeeded || !clearAllOnSuccess || !string.IsNullOrEmpty(ReplyBody))
            await PersistDraftAsync(cancellationToken);
        if (generation != _loadGeneration) return result;
        SetStatus(result.Message);
        RaiseState();
        return result;
    }

    private void ScheduleSave()
    {
        if (!_hasLoaded || _disposed || PendingOperationId is not null) return;
        _saveCancellation?.Cancel();
        _saveCancellation?.Dispose();
        _saveCancellation = new CancellationTokenSource();
        var token = _saveCancellation.Token;
        _ = SaveAfterDelayAsync(token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(250, cancellationToken); await PersistDraftAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { SetStatus($"Review draft could not be saved: {exception.Message}"); }
    }

    private async Task PersistDraftAsync(CancellationToken cancellationToken)
        => await PersistDraftAsync(null, cancellationToken);

    private async Task PersistDraftAsync(PullRequestReviewDraft? capturedDraft, CancellationToken cancellationToken)
    {
        // Autosave and explicit flushes participate in the same serialization
        // as discard.  Otherwise an in-flight save could recreate a file just
        // after DiscardDraftAsync deletes it.
        await _writeGate.WaitAsync(cancellationToken);
        try { await PersistDraftCoreAsync(capturedDraft, cancellationToken); }
        finally { _writeGate.Release(); }
    }

    private async Task PersistDraftCoreAsync(PullRequestReviewDraft? capturedDraft, CancellationToken cancellationToken)
    {
        if (!_hasLoaded || _projectId is not { } projectId || Snapshot is not { } snapshot || Repository is not { } repository || PullRequest is not { } pullRequest) return;
        var draft = capturedDraft ?? CreateDraft(snapshot, ReplyThreadId);
        await _draftStore.SaveAsync(projectId, draft, cancellationToken);
    }

    private PullRequestReviewDraft CreateDraft(PullRequestReviewSnapshot snapshot, string? replyThreadId) =>
        CreateDraft(snapshot, replyThreadId, Body, ReviewEvent, InlineComments.ToArray(), ReplyBody);

    private PullRequestReviewDraft CreateDraft(PullRequestReviewSnapshot snapshot, string? replyThreadId,
        string body, PullRequestReviewEvent reviewEvent, IReadOnlyList<PullRequestInlineComment> comments, string replyBody) =>
        new(PullRequestReviewDefaults.RepositoryKey(snapshot.Repository), snapshot.PullRequest.Number,
            _draftHeadCommitId ?? snapshot.HeadCommitId, body, reviewEvent, comments,
            PendingOperationId, replyThreadId ?? ReplyThreadId, replyBody, _pendingAction);

    private void SetStatus(string status) => Status = status ?? string.Empty;

    private bool PayloadWithinLimit()
    {
        var size = Body.Length;
        foreach (var comment in InlineComments) size += comment.Path.Length + comment.Body.Length + 64;
        // JSON escaping can expand a character to six bytes; retain room for the envelope.
        return size <= 100_000;
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(CanReadReview));
        OnPropertyChanged(nameof(CanWriteReview));
        OnPropertyChanged(nameof(CanSubmit));
        OnPropertyChanged(nameof(CanAddInlineComment));
        OnPropertyChanged(nameof(CanReply));
        OnPropertyChanged(nameof(CanResolve));
        OnPropertyChanged(nameof(HasPendingOperation));
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(CanEditDraft));
        OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(HeadSummary));
        OnPropertyChanged(nameof(DetailsSummary));
        OnPropertyChanged(nameof(CommitsSummary));
        OnPropertyChanged(nameof(ChecksSummary));
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _loadGeneration);
        _loadCancellation?.Cancel();
        _saveCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _saveCancellation?.Dispose();
    }
}
