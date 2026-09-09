using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PiStation.ClientRuntime;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed class ComposerSaveFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}

public sealed partial class ComposerViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<ThreadId, CancellationToken, Task<ThreadDraft>> _loadDraft;
    private readonly Func<ThreadDraft, string, string?, Stream, long, CancellationToken, Task<ThreadDraft>>
        _uploadAttachment;
    private readonly Func<ThreadDraft, AttachmentId, CancellationToken, Task<ThreadDraft>> _removeAttachment;
    private readonly Func<ThreadDraft, CancellationToken, Task<ThreadDraft>> _clearDraft;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Func<ThreadDraft, string, CancellationToken, Task<ThreadDraft>> _saveDraft;
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _pendingSaveDelay;
    private ThreadDraft? _draft;
    private string _status = "No active draft";
    private string _text = string.Empty;
    private ThreadId? _threadId;
    private bool _disposed;
    private bool _settingLoadedText;
    private readonly EditingRecoveryStore? _recovery;
    private RecoveredDraft? _conflictingRecovery;

    public ComposerViewModel(
        DispatcherQueue dispatcherQueue,
        Func<ThreadId, CancellationToken, Task<ThreadDraft>> loadDraft,
        Func<ThreadDraft, string, CancellationToken, Task<ThreadDraft>> saveDraft,
        Func<ThreadDraft, string, string?, Stream, long, CancellationToken, Task<ThreadDraft>> uploadAttachment,
        Func<ThreadDraft, AttachmentId, CancellationToken, Task<ThreadDraft>> removeAttachment,
        Func<ThreadDraft, CancellationToken, Task<ThreadDraft>> clearDraft,
        EditingRecoveryStore? recovery = null)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _loadDraft = loadDraft ?? throw new ArgumentNullException(nameof(loadDraft));
        _saveDraft = saveDraft ?? throw new ArgumentNullException(nameof(saveDraft));
        _uploadAttachment = uploadAttachment ?? throw new ArgumentNullException(nameof(uploadAttachment));
        _removeAttachment = removeAttachment ?? throw new ArgumentNullException(nameof(removeAttachment));
        _clearDraft = clearDraft ?? throw new ArgumentNullException(nameof(clearDraft));
        ContextChips.CollectionChanged += OnContextChanged;
        _recovery = recovery;
    }

    public event EventHandler<ComposerSaveFailedEventArgs>? SaveFailed;

    public ObservableCollection<DraftAttachmentViewModel> Attachments { get; } = [];

    public ObservableCollection<ComposerContextChipViewModel> ContextChips { get; } = [];

    public void AddContext(ComposerContextChipViewModel context)
    {
        if (_draft is null) throw new InvalidOperationException("Wait for the thread draft to load before adding context.");
        ComposerContextDefaults.Validate([.. GetContext(), context.ToContext()]);
        ContextChips.Add(context);
    }

    private ComposerContext[] GetContext() => ContextChips.Select(static chip => chip.ToContext()).ToArray();

    private void OnContextChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        OnPropertyChanged(nameof(ContextChips));
        if (_settingLoadedText || _draft is null) return;
        Status = "Unsaved";
        PersistRecovery();
        ScheduleSave();
    }

    private void ReplaceContext(IReadOnlyList<ComposerContext>? context)
    {
        _settingLoadedText = true;
        try
        {
            ContextChips.Clear();
            foreach (var item in context ?? []) ContextChips.Add(ComposerContextChipViewModel.FromContext(item));
        }
        finally { _settingLoadedText = false; }
    }

    public bool HasAttachments => Attachments.Count != 0;
    public bool HasDraft => _draft is not null;

    internal bool HasUnsavedChanges => _draft is not null && (!string.Equals(_text, _draft.Text, StringComparison.Ordinal) ||
        !GetContext().SequenceEqual(_draft.Context ?? []));
    public Visibility RecoveryConflictVisibility => _conflictingRecovery is null ? Visibility.Collapsed : Visibility.Visible;
    public bool HasRecoveryConflict => _conflictingRecovery is not null;
    public string RecoveredText => _conflictingRecovery?.Text ?? string.Empty;

    public void RestoreRecoveredDraft()
    {
        if (_conflictingRecovery is not { } recovered) return;
        _conflictingRecovery = null;
        OnPropertyChanged(nameof(RecoveredText));
        OnPropertyChanged(nameof(RecoveryConflictVisibility));
        OnPropertyChanged(nameof(HasRecoveryConflict));
        OnPropertyChanged(nameof(CanAttach));
        Text = recovered.Text;
        ReplaceContext(recovered.Context);
        ScheduleSave();
        PersistRecovery();
    }

    public void KeepHostDraft()
    {
        _conflictingRecovery = null;
        OnPropertyChanged(nameof(RecoveredText));
        OnPropertyChanged(nameof(RecoveryConflictVisibility));
        OnPropertyChanged(nameof(HasRecoveryConflict));
        OnPropertyChanged(nameof(CanAttach));
        PersistRecovery();
        Status = HasUnsavedChanges ? "Unsaved" : "Saved";
    }

    public double AttachmentRailHeight => HasAttachments ? 36 : 1;

    public double AttachmentNoticeHeight => HasAttachments ? double.NaN : 1;

    public bool CanAttach => !HasRecoveryConflict && _draft is not null && Attachments.Count < AttachmentDefaults.MaximumPerDraft;

    public bool OwnsDraft(ThreadId threadId) => _draft?.ThreadId == threadId && _threadId == threadId;

    public string AttachmentNotice => HasAttachments
        ? "Attachments will be sent with your next message. Supported images are included directly; other files are shared by path."
        : string.Empty;

    public string Text
    {
        get => _text;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_disposed || _lifetimeCancellation.IsCancellationRequested) return;
            if (!SetProperty(ref _text, value) || _settingLoadedText || _draft is null)
            {
                return;
            }

            Status = "Unsaved";
            PersistRecovery();
            ScheduleSave();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public async Task SelectThreadAsync(
        ThreadId? threadId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var operationCancellation = CreateLifetimeCancellation(cancellationToken);
        await _switchGate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            var currentThreadId = await RunOnUiThreadAsync(() => _threadId).ConfigureAwait(false);
            if (currentThreadId == threadId)
            {
                return;
            }

            await FlushAsync(operationCancellation.Token).ConfigureAwait(false);
            CancelPendingDelay();
            await RunOnUiThreadAsync(() =>
            {
                _threadId = threadId;
                _draft = null;
                OnPropertyChanged(nameof(HasDraft));
                _conflictingRecovery = null;
                OnPropertyChanged(nameof(RecoveredText));
                OnPropertyChanged(nameof(RecoveryConflictVisibility));
                OnPropertyChanged(nameof(HasRecoveryConflict));
                SetLoadedText(string.Empty);
                ReplaceContext([]);
                ReplaceAttachments([]);
                Status = threadId is null ? "No active draft" : "Loading draft";
            }).ConfigureAwait(false);
            if (threadId is null)
            {
                return;
            }

            var loaded = await _loadDraft(threadId.Value, operationCancellation.Token).ConfigureAwait(false);
            await RunOnUiThreadAsync(() =>
            {
                if (_threadId != threadId)
                {
                    return;
                }

                _draft = loaded;
                OnPropertyChanged(nameof(HasDraft));
                var recovered = _recovery?.LoadDraft(loaded.ThreadId.Value);
                var differs = recovered is not null && (recovered.Text != loaded.Text ||
                    !(recovered.Context ?? []).SequenceEqual(loaded.Context ?? []));
                var restore = differs && recovered!.DraftId == loaded.DraftId.Value && recovered.BaseText == loaded.Text &&
                    (recovered.BaseContext ?? []).SequenceEqual(loaded.Context ?? []);
                _conflictingRecovery = differs && !restore ? recovered : null;
                OnPropertyChanged(nameof(RecoveredText));
                SetLoadedText(restore ? recovered!.Text : loaded.Text);
                ReplaceContext(restore ? recovered!.Context : loaded.Context);
                ReplaceAttachments(loaded.Attachments);
                Status = _conflictingRecovery is not null ? "Recovered draft differs from the host. Choose which text and context to keep."
                    : restore ? "Recovered local draft • ready to edit or send" : "Saved";
                OnPropertyChanged(nameof(RecoveryConflictVisibility));
                OnPropertyChanged(nameof(HasRecoveryConflict));
                OnPropertyChanged(nameof(CanAttach));
                PersistRecovery();
            }).ConfigureAwait(false);
        }
        catch when (_disposed)
        {
            throw;
        }
        catch
        {
            await RunOnUiThreadAsync(() => Status = "Draft unavailable").ConfigureAwait(false);
            throw;
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public async Task<ThreadDraft?> PrepareTurnAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        return await RunOnUiThreadAsync(() => _draft).ConfigureAwait(false);
    }

    public Task ApplyRestoredDraftAsync(ThreadDraft expected, ThreadDraft restored) => RunOnUiThreadAsync(() =>
    {
        if (_draft?.DraftId != expected.DraftId) return;
        var localText = string.Equals(_text, expected.Text, StringComparison.Ordinal) ? string.Empty : _text;
        var localContext = GetContext();
        _draft = restored;
        SetLoadedText(string.IsNullOrWhiteSpace(localText) ? restored.Text : $"{restored.Text}{Environment.NewLine}{localText}");
        ReplaceContext((restored.Context ?? []).Concat(localContext).DistinctBy(static item => item.Id).ToArray());
        ReplaceAttachments(restored.Attachments);
        Status = "Saved";
        if (localText.Length > 0 || localContext.Length > 0) ScheduleSave();
    });

    public async Task ClearAcceptedTurnAsync(
        ThreadDraft sentDraft,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sentDraft);
        using var operationCancellation = CreateLifetimeCancellation(cancellationToken);
        await _saveGate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            await RunOnUiThreadAsync(() => Status = "Clearing sent draft").ConfigureAwait(false);
            var saved = await _clearDraft(sentDraft, operationCancellation.Token).ConfigureAwait(false);
            await RunOnUiThreadAsync(() => ApplyClearedDraft(sentDraft, saved)).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancelPendingDelay();
        await SaveCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddAttachmentAsync(
        string fileName,
        string? mediaType,
        Stream content,
        long byteLength,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);
        CancelPendingDelay();
        await SaveCurrentAsync(cancellationToken).ConfigureAwait(false);
        using var operationCancellation = CreateLifetimeCancellation(cancellationToken);
        await _saveGate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            var draft = await RunOnUiThreadAsync(() => _draft).ConfigureAwait(false);
            if (draft is null)
            {
                return;
            }

            await RunOnUiThreadAsync(() => Status = $"Uploading {fileName}").ConfigureAwait(false);
            var saved = await _uploadAttachment(
                draft,
                fileName,
                mediaType,
                content,
                byteLength,
                operationCancellation.Token).ConfigureAwait(false);
            await RunOnUiThreadAsync(() => ApplySavedDraft(saved)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (_disposed) return;
            await RunOnUiThreadAsync(() =>
            {
                Status = "Attachment failed";
                SaveFailed?.Invoke(this, new ComposerSaveFailedEventArgs(exception));
            }).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task RemoveAttachmentAsync(
        DraftAttachmentViewModel attachment,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(attachment);
        CancelPendingDelay();
        await SaveCurrentAsync(cancellationToken).ConfigureAwait(false);
        using var operationCancellation = CreateLifetimeCancellation(cancellationToken);
        await _saveGate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            var draft = await RunOnUiThreadAsync(() => _draft).ConfigureAwait(false);
            if (draft is null)
            {
                return;
            }

            await RunOnUiThreadAsync(() => Status = $"Removing {attachment.FileName}").ConfigureAwait(false);
            var saved = await _removeAttachment(draft, attachment.AttachmentId, operationCancellation.Token)
                .ConfigureAwait(false);
            await RunOnUiThreadAsync(() => ApplySavedDraft(saved)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (_disposed) return;
            await RunOnUiThreadAsync(() =>
            {
                Status = "Attachment removal failed";
                SaveFailed?.Invoke(this, new ComposerSaveFailedEventArgs(exception));
            }).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    internal void CancelPendingOperations()
    {
        if (_disposed) return;
        _lifetimeCancellation.Cancel();
        CancelPendingDelay();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        CancelPendingOperations();
        _disposed = true;
        ContextChips.CollectionChanged -= OnContextChanged;
        CancelPendingDelay();
        await _switchGate.WaitAsync().ConfigureAwait(false);
        _switchGate.Release();
        await _saveGate.WaitAsync().ConfigureAwait(false);
        _saveGate.Release();
        _saveGate.Dispose();
        _switchGate.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private void ScheduleSave()
    {
        if (_disposed || _lifetimeCancellation.IsCancellationRequested) return;
        CancelPendingDelay();
        var delay = new CancellationTokenSource();
        _pendingSaveDelay = delay;
        _ = SaveAfterDelayAsync(delay);
    }

    private async Task SaveAfterDelayAsync(CancellationTokenSource delay)
    {
        try
        {
            using var saveDelayCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                delay.Token, _lifetimeCancellation.Token);
            await Task.Delay(SaveDelay, saveDelayCancellation.Token).ConfigureAwait(false);
            await SaveCurrentAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (delay.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        catch (Exception exception)
        {
            if (_disposed)
            {
                return;
            }
            await RunOnUiThreadAsync(() =>
            {
                Status = "Save failed";
                SaveFailed?.Invoke(this, new ComposerSaveFailedEventArgs(exception));
            }).ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_pendingSaveDelay, delay))
            {
                _pendingSaveDelay = null;
            }

            delay.Dispose();
        }
    }

    private async Task SaveCurrentAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeCancellation.Token);
        var saveToken = lifetime.Token;
        await _saveGate.WaitAsync(saveToken).ConfigureAwait(false);
        try
        {
            var snapshot = await RunOnUiThreadAsync(() => new DraftSnapshot(_draft, _text, GetContext()))
                .ConfigureAwait(false);
            if (snapshot.Draft is null)
            {
                return;
            }

            if (string.Equals(snapshot.Draft.Text, snapshot.Text, StringComparison.Ordinal) &&
                (snapshot.Draft.Context ?? []).SequenceEqual(snapshot.Context))
            {
                await RunOnUiThreadAsync(() => Status = "Saved").ConfigureAwait(false);
                return;
            }

            await RunOnUiThreadAsync(() => Status = "Saving").ConfigureAwait(false);
            var saved = await _saveDraft(snapshot.Draft with { Context = snapshot.Context }, snapshot.Text, saveToken).ConfigureAwait(false);
            await RunOnUiThreadAsync(() =>
            {
                if (_draft?.DraftId != saved.DraftId)
                {
                    return;
                }

                ApplySavedDraft(saved);
            }).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private CancellationTokenSource CreateLifetimeCancellation(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);

    private void CancelPendingDelay()
    {
        var delay = Interlocked.Exchange(ref _pendingSaveDelay, null);
        delay?.Cancel();
    }

    private void SetLoadedText(string text)
    {
        _settingLoadedText = true;
        try
        {
            Text = text;
        }
        finally
        {
            _settingLoadedText = false;
        }
    }

    private void ApplySavedDraft(ThreadDraft saved)
    {
        if (_draft?.DraftId != saved.DraftId)
        {
            return;
        }

        _draft = saved;
        ReplaceAttachments(saved.Attachments);
        Status = string.Equals(_text, saved.Text, StringComparison.Ordinal) &&
            GetContext().SequenceEqual(saved.Context ?? []) ? "Saved" : "Unsaved";
        PersistRecovery();
    }

    private void ApplyClearedDraft(ThreadDraft sentDraft, ThreadDraft cleared)
    {
        if (_draft?.DraftId != cleared.DraftId)
        {
            return;
        }

        var textStillMatchesSentDraft = string.Equals(_text, sentDraft.Text, StringComparison.Ordinal);
        _draft = cleared;
        var sentContextIds = (sentDraft.Context ?? []).Select(static context => context.Id).ToHashSet(StringComparer.Ordinal);
        ReplaceContext(GetContext().Where(context => !sentContextIds.Contains(context.Id)).ToArray());
        ReplaceAttachments(cleared.Attachments);
        if (textStillMatchesSentDraft)
        {
            SetLoadedText(cleared.Text);
        }

        Status = string.Equals(_text, cleared.Text, StringComparison.Ordinal) && ContextChips.Count == 0 ? "Saved" : "Unsaved";
        if (Status == "Unsaved") ScheduleSave();
        PersistRecovery();
    }

    private void PersistRecovery()
    {
        if (_draft is null || _conflictingRecovery is not null) return;
        if (HasUnsavedChanges) _recovery?.SaveDraft(RecoveredDraft.Capture(_draft.ThreadId.Value, _draft.DraftId.Value, _draft.Text, _text, _draft.Context, GetContext()));
        else _recovery?.RemoveDraft(_draft.ThreadId.Value);
    }

    private void ReplaceAttachments(IReadOnlyList<DraftAttachment> attachments)
    {
        Attachments.Clear();
        foreach (var attachment in attachments)
        {
            Attachments.Add(new DraftAttachmentViewModel(attachment));
        }

        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(AttachmentRailHeight));
        OnPropertyChanged(nameof(AttachmentNoticeHeight));
        OnPropertyChanged(nameof(CanAttach));
        OnPropertyChanged(nameof(AttachmentNotice));
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("The UI dispatcher is unavailable."));
        }

        return completion.Task;
    }

    private Task<T> RunOnUiThreadAsync<T>(Func<T> action)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            return Task.FromResult(action());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    completion.TrySetResult(action());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("The UI dispatcher is unavailable."));
        }

        return completion.Task;
    }

    private sealed record DraftSnapshot(ThreadDraft? Draft, string Text, IReadOnlyList<ComposerContext> Context);
}
