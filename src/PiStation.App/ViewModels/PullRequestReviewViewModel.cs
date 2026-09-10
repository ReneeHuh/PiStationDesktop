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

public sealed partial class PullRequestReviewViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Func<IEnvironmentClient> _clientFactory;
    private readonly PullRequestReviewDraftStore _draftStore;
    private readonly Func<bool> _canOperate;
    private bool _isCheckingOut;
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
    private string _status = "Select a pull request to review.";
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

    public PullRequestReviewViewModel(Func<IEnvironmentClient> clientFactory, PullRequestReviewDraftStore draftStore, Func<bool>? canOperate = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _draftStore = draftStore ?? throw new ArgumentNullException(nameof(draftStore));
        _canOperate = canOperate ?? (() => true);
    }

    public ObservableCollection<PullRequestChangedFile> Files { get; } = [];
    public ObservableCollection<PullRequestReviewLineViewModel> Lines { get; } = [];
    public ObservableCollection<PullRequestDiscussion> Discussions { get; } = [];
    public ObservableCollection<PullRequestInlineComment> InlineComments { get; } = [];

    public PullRequestReviewSnapshot? Snapshot
    {
        get => _snapshot;
        private set
        {
            if (!SetProperty(ref _snapshot, value)) return;
            if (UpdateMethods.Count > 0 && !UpdateMethods.Contains(_selectedUpdateMethod)) _selectedUpdateMethod = UpdateMethods[0];
        }
    }
    public ProjectId? ProjectId => _projectId;
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
                foreach (var line in value.Lines)
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
            if (_updatingDiscussions) return;
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
    public PullRequestReviewCapabilities ReviewCapabilities => Snapshot?.Capabilities ?? HostingCapabilities.Review(PullRequest?.Provider ?? SourceControlProvider.Unknown);
    public IReadOnlyList<PullRequestReviewEvent> ReviewEvents => ReviewCapabilities.Verdicts;
    public bool HasProviderDiff => ReviewCapabilities.Diff;
    public bool HasReviewComposer => ReviewEvents.Count > 0;
    public bool CanCreateReviewThread => _hasLoaded && Snapshot?.Repository.Provider == SourceControlProvider.GitHub &&
        _capturedTarget is not null && !IsBusy && !_isCheckingOut && !IsStaleHead && PendingOperationId is null &&
        !_isDiscarding && !_disposed && _canOperate();

    public async Task<ThreadDescriptor?> CreateReviewThreadAsync(PiModelSelection? model = null,
        PiThinkingLevel? thinking = null, CancellationToken cancellationToken = default)
    {
        if (!CanCreateReviewThread || Snapshot is not { } snapshot || _capturedTarget is not { } workspace) return null;
        var generation = _loadGeneration;
        var client = _clientFactory();
        var request = new CreatePullRequestReviewThreadRequest(new(workspace,
            PullRequestReviewDefaults.RepositoryKey(snapshot.Repository), snapshot.PullRequest.Number, snapshot.HeadCommitId), model, thinking);
        _isCheckingOut = true;
        IsBusy = true;
        SetStatus("Preparing an isolated PR worktree and Pi review thread…");
        try
        {
            await SaveNowAsync(cancellationToken);
            var thread = await client.CreatePullRequestReviewThreadAsync(request, cancellationToken);
            if (_disposed || generation != _loadGeneration) return null;
            SetStatus($"Ready: {thread.Title}. Send the prepared prompt to start the Pi review.");
            return thread;
        }
        catch (Exception exception)
        {
            if (!_disposed && generation == _loadGeneration)
                SetStatus($"Checkout could not finish: {exception.Message} Retry to recover the same review thread.");
            return null;
        }
        finally
        {
            _isCheckingOut = false;
            if (generation == _loadGeneration) IsBusy = false;
            RaiseState();
        }
    }
    public bool CanWriteReview => CanReadReview && Repository?.CanWrite == true && PullRequest is { Provider: var provider } && HostingCapabilities.CanWriteReview(provider);
    public bool CanSubmit => _hasLoaded && CanWriteReview && !IsBusy && !IsStaleHead && PendingOperationId is null &&
        Snapshot is not null && ReviewEvents.Contains(ReviewEvent) &&
        (ReviewEvent == PullRequestReviewEvent.Approve || !string.IsNullOrWhiteSpace(Body)) &&
        Body.Length <= PullRequestReviewDefaults.MaximumBodyCharacters &&
        InlineComments.Count <= PullRequestReviewDefaults.MaximumInlineComments && PayloadWithinLimit();
    public bool CanAddInlineComment => _hasLoaded && CanWriteReview && ReviewCapabilities.InlineComments && HasReviewComposer && !IsBusy && !IsStaleHead && PendingOperationId is null &&
        InlineComments.Count < PullRequestReviewDefaults.MaximumInlineComments && Enum.IsDefined(SelectedSide) &&
        SelectedLine is { CanComment: true } && (SelectedSide == PullRequestDiffSide.Left
            ? SelectedLine.Line.OldLine is not null
            : SelectedLine.Line.NewLine is not null);
    public bool CanReply => _hasLoaded && CanWriteReview && HasReviewComposer && !IsBusy && !IsStaleHead && PendingOperationId is null &&
        SelectedDiscussion is { CanReply: true } && !string.IsNullOrWhiteSpace(ReplyBody) &&
        ReplyBody.Length <= PullRequestReviewDefaults.MaximumBodyCharacters;
    public bool CanResolve => _hasLoaded && CanWriteReview && HasReviewComposer && !IsBusy && !IsStaleHead && PendingOperationId is null && SelectedDiscussion?.CanResolve == true;
    public bool HasPendingOperation => PendingOperationId is not null;
    public bool HasSnapshot => Snapshot is not null;
    public bool CanLoadMore => _hasLoaded && Snapshot?.NextPages?.Count > 0 && !IsBusy && !IsStaleHead &&
        PendingOperationId is null && !_isDiscarding && !_disposed;
    public string PaginationSummary => Snapshot is null ? string.Empty :
        $"{Files.Count} files • {Snapshot.Commits.Count} commits • {Snapshot.Checks.Count} checks • {Discussions.Count} discussions" +
        (Snapshot.NextPages?.Count > 0 ? " • more available" : string.Empty);
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
        $"{Snapshot.PullRequest.SourceBranch} → {Snapshot.PullRequest.TargetBranch}" +
        (Snapshot.PullRequest.Labels.Count == 0 ? string.Empty : $"\nLabels: {string.Join(", ", Snapshot.PullRequest.Labels)}") +
        (Snapshot.PullRequest.Reviewers.Count == 0 ? string.Empty : $"\nReviewers: {string.Join(", ", Snapshot.PullRequest.Reviewers)}");
    public string CommitsSummary => Snapshot is null ? string.Empty :
        Snapshot.Commits.Count == 0 ? "No commits reported." :
        string.Join("\n", Snapshot.Commits.Select(commit =>
            $"{commit.Sha[..Math.Min(10, commit.Sha.Length)]}  {commit.Title}  ({commit.Author})"));
    public string ChecksSummary => Snapshot is null ? string.Empty :
        Snapshot.Checks.Count == 0 ? "No checks reported." :
        string.Join("\n", Snapshot.Checks.Select(check =>
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
        ResetWorkflows();
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
                SetStatus("Detailed review is unavailable for this provider.");
                return;
            }

            var snapshot = await _clientFactory().GetPullRequestReviewAsync(
                new GetPullRequestReviewRequest(target, pullRequest.Number), token);
            if (generation != Volatile.Read(ref _loadGeneration) || token.IsCancellationRequested) return;
            var expectedRepository = pullRequest.Repository.Trim().Trim('/');
            var returnedRepository = $"{snapshot.Repository.Owner}/{snapshot.Repository.Name}";
            if (snapshot.Repository.Provider != pullRequest.Provider || snapshot.PullRequest.Provider != pullRequest.Provider ||
                PullRequestReviewDefaults.HostingAuthority(pullRequest.Provider, pullRequest.Url) is not { } expectedHost ||
                expectedHost != PullRequestReviewDefaults.HostingAuthority(snapshot.Repository.Provider, snapshot.Repository.WebUrl) ||
                !string.Equals(snapshot.PullRequest.Number, pullRequest.Number, StringComparison.Ordinal) ||
                !string.Equals(snapshot.PullRequest.Repository.Trim().Trim('/'), expectedRepository, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(returnedRepository, expectedRepository, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The review response belongs to another pull request.");
            Snapshot = snapshot;
            Repository = snapshot.Repository;
            foreach (var file in snapshot.Files) Files.Add(file);
            foreach (var discussion in snapshot.Discussions) Discussions.Add(discussion);
            SelectedFile = Files.FirstOrDefault();
            SelectedDiscussion = Discussions.FirstOrDefault();
            var repositoryKey = PullRequestReviewDefaults.RepositoryKey(snapshot.Repository);
            var draft = await _draftStore.LoadAsync(projectId, repositoryKey, pullRequest.Number, token);
            if (generation != Volatile.Read(ref _loadGeneration) || token.IsCancellationRequested) return;
            PendingOperationId = null;
            _pendingAction = null;
            _draftHeadCommitId = snapshot.HeadCommitId;
            _liveRevisionChanged = false;
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
            LoadManagementDraft(snapshot, draft?.Management);
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

    public async Task LoadMoreAsync(CancellationToken cancellationToken = default)
    {
        if (!CanLoadMore || Snapshot is not { } original || _capturedTarget is null) return;
        var continuation = original.NextPages![0];
        var generation = Volatile.Read(ref _loadGeneration);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            _loadCancellation?.Token ?? CancellationToken.None);
        var token = cancellation.Token;
        IsBusy = true;
        SetStatus("Loading more review data…");
        try
        {
            var page = await _clientFactory().GetPullRequestReviewAsync(
                new GetPullRequestReviewRequest(_capturedTarget, original.PullRequest.Number, continuation), token);
            if (generation != Volatile.Read(ref _loadGeneration) || token.IsCancellationRequested) return;
            if (PullRequestReviewDefaults.RepositoryKey(page.Repository) != PullRequestReviewDefaults.RepositoryKey(original.Repository) ||
                page.PullRequest.Number != original.PullRequest.Number)
                throw new InvalidDataException("The review page belongs to another pull request.");
            if (page.HeadCommitId != original.HeadCommitId || page.BaseCommitId != original.BaseCommitId)
            {
                _liveRevisionChanged = true;
                IsStaleHead = true;
                throw new InvalidDataException("The pull request head or base changed. Reload the review.");
            }
            if (page.NextPages?.Contains(continuation) == true)
                throw new InvalidDataException("The review page did not advance. Reload the review.");
            var mergedFiles = original.Files.ToList();
            foreach (var file in page.Files)
            {
                var index = mergedFiles.FindIndex(existing => existing.Path == file.Path);
                if (index < 0)
                {
                    if (file.PatchLineOffset != 0) throw new InvalidDataException("The review patch page is out of order.");
                    mergedFiles.Add(file);
                }
                else
                {
                    var existing = mergedFiles[index];
                    if (file.PatchLineOffset != existing.Lines.Count) throw new InvalidDataException("The review patch page is out of order.");
                    mergedFiles[index] = existing with { Lines = [.. existing.Lines, .. file.Lines] };
                }
            }
            var mergedDiscussions = original.Discussions.ToList();
            foreach (var discussion in page.Discussions)
            {
                var index = mergedDiscussions.FindIndex(existing => existing.Id == discussion.Id);
                if (index < 0) mergedDiscussions.Add(discussion);
                else mergedDiscussions[index] = discussion with
                {
                    Comments = MergeByKey(mergedDiscussions[index].Comments, discussion.Comments, comment => comment.Id)
                };
            }
            var next = original.NextPages!.Skip(1).Concat(page.NextPages ?? []).Distinct().ToArray();
            var notices = new[] { original.Notice, page.Notice }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal);
            var notice = string.Join(" ", notices);
            var selectedPath = SelectedFile?.Path;
            var selectedLine = SelectedLine?.Line;
            var discussionId = SelectedDiscussion?.Id ?? ReplyThreadId;
            var mergedChecks = MergeByKey(original.Checks, page.Checks, check => check.Id ?? $"{check.Name}:{check.Url}");
            Snapshot = original with
            {
                Files = mergedFiles,
                Commits = MergeByKey(original.Commits, page.Commits, commit => commit.Sha),
                Checks = mergedChecks,
                Discussions = mergedDiscussions,
                PullRequest = original.PullRequest with
                {
                    Checks = PullRequestReviewDefaults.GetCheckState(mergedChecks, next.Any(value => value.Kind == PullRequestReviewPageKind.Checks)),
                    Labels = original.PullRequest.Labels.Concat(page.PullRequest.Labels).Distinct(StringComparer.Ordinal).ToArray(),
                    Reviewers = original.PullRequest.Reviewers.Concat(page.PullRequest.Reviewers).Distinct(StringComparer.Ordinal).ToArray()
                },
                NextPages = next, IsTruncated = next.Length > 0 || notice.Length > 0,
                Notice = notice.Length == 0 ? null : notice
            };
            foreach (var file in mergedFiles)
            {
                var existing = Files.FirstOrDefault(value => value.Path == file.Path);
                if (existing is null) Files.Add(file);
                else if (!ReferenceEquals(existing, file)) Files[Files.IndexOf(existing)] = file;
            }
            SelectedFile = Files.FirstOrDefault(file => file.Path == selectedPath) ?? Files.FirstOrDefault();
            if (selectedLine is not null) SelectedLine = Lines.FirstOrDefault(line => line.Line == selectedLine);
            foreach (var discussion in mergedDiscussions)
            {
                var existing = Discussions.FirstOrDefault(value => value.Id == discussion.Id);
                if (existing is null) Discussions.Add(discussion);
                else if (!ReferenceEquals(existing, discussion)) Discussions[Discussions.IndexOf(existing)] = discussion;
            }
            // Updating the selected thread's comments must not retarget an unsent reply.
            _selectedDiscussion = Discussions.FirstOrDefault(discussion => discussion.Id == discussionId);
            OnPropertyChanged(nameof(SelectedDiscussion));
            OnPropertyChanged(nameof(DiscussionSummary));
            RaiseState();
            SetStatus(notice.Length > 0 ? notice : next.Length > 0 ? "More review data loaded." : "All available review data loaded.");
            RefreshManagementComments();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _loadGeneration))
            {
                if (exception.Message.Contains("head changed", StringComparison.OrdinalIgnoreCase) ||
                    exception.Message.Contains("base changed", StringComparison.OrdinalIgnoreCase)) IsStaleHead = true;
                SetStatus($"Could not load more review data: {exception.Message}");
            }
        }
        finally { if (generation == Volatile.Read(ref _loadGeneration)) IsBusy = false; }
    }

    private static List<T> MergeByKey<T>(IReadOnlyList<T> original, IReadOnlyList<T> page, Func<T, string> key)
    {
        var values = original.ToList();
        var indices = values.Select((value, index) => (Key: key(value), Index: index))
            .ToDictionary(pair => pair.Key, pair => pair.Index, StringComparer.Ordinal);
        foreach (var value in page)
        {
            var id = key(value);
            if (indices.TryGetValue(id, out var index)) values[index] = value;
            else { indices[id] = values.Count; values.Add(value); }
        }
        return values;
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
            IsStaleHead = _liveRevisionChanged;
            _draftHeadCommitId = Snapshot?.HeadCommitId;
            _completedDiscardEpoch = _discardEpoch;
            if (Snapshot is { } current) LoadManagementDraft(current, null);
            // A live refresh retained the old diff coordinates. Do not recreate an empty draft for that revision.
            if (_liveRevisionChanged) _hasLoaded = false;
            SetStatus(_liveRevisionChanged ? "Review draft discarded. Reload the review to load its current revision." : "Review draft discarded.");
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
            _mutationGeneration++;
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
        CompleteManagementAction(result, completedAction);
        var generation = _loadGeneration;
        var preserveOtherDrafts = !string.IsNullOrEmpty(ReplyBody) || HasManagementEdits;
        if (result.Succeeded && clearAllOnSuccess)
        {
            if (!preserveOtherDrafts && _projectId is { } projectId && Repository is { } repository && PullRequest is { } pullRequest)
                await _draftStore.DeleteAsync(projectId, PullRequestReviewDefaults.RepositoryKey(repository), pullRequest.Number, cancellationToken);
            if (generation != _loadGeneration) return result;
            Body = string.Empty;
            InlineComments.Clear();
        }
        PendingOperationId = null;
        _pendingAction = null;
        if (result.Succeeded && string.Equals(completedAction, "Reply", StringComparison.Ordinal)) ReplyBody = string.Empty;
        if (!result.Succeeded || !clearAllOnSuccess || !string.IsNullOrEmpty(ReplyBody) || HasManagementEdits)
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
            PendingOperationId, replyThreadId ?? ReplyThreadId, replyBody, _pendingAction, _managementDraft);

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
        RaiseManagementState();
        OnPropertyChanged(nameof(CanCreateReviewThread));
        OnPropertyChanged(nameof(CanLoadMore));
        OnPropertyChanged(nameof(PaginationSummary));
        OnPropertyChanged(nameof(CanReadReview));
        OnPropertyChanged(nameof(ReviewCapabilities));
        OnPropertyChanged(nameof(ReviewEvents));
        OnPropertyChanged(nameof(HasProviderDiff));
        OnPropertyChanged(nameof(HasReviewComposer));
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
