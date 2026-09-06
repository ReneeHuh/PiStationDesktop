using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed class ComposerSaveFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}

public sealed class ComposerViewModel : ObservableObject, IAsyncDisposable
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
    private CancellationTokenSource? _pendingSaveDelay;
    private ThreadDraft? _draft;
    private string _status = "No active draft";
    private string _text = string.Empty;
    private ThreadId? _threadId;
    private bool _disposed;
    private bool _settingLoadedText;

    public ComposerViewModel(
        DispatcherQueue dispatcherQueue,
        Func<ThreadId, CancellationToken, Task<ThreadDraft>> loadDraft,
        Func<ThreadDraft, string, CancellationToken, Task<ThreadDraft>> saveDraft,
        Func<ThreadDraft, string, string?, Stream, long, CancellationToken, Task<ThreadDraft>> uploadAttachment,
        Func<ThreadDraft, AttachmentId, CancellationToken, Task<ThreadDraft>> removeAttachment,
        Func<ThreadDraft, CancellationToken, Task<ThreadDraft>> clearDraft)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _loadDraft = loadDraft ?? throw new ArgumentNullException(nameof(loadDraft));
        _saveDraft = saveDraft ?? throw new ArgumentNullException(nameof(saveDraft));
        _uploadAttachment = uploadAttachment ?? throw new ArgumentNullException(nameof(uploadAttachment));
        _removeAttachment = removeAttachment ?? throw new ArgumentNullException(nameof(removeAttachment));
        _clearDraft = clearDraft ?? throw new ArgumentNullException(nameof(clearDraft));
        ContextChips.CollectionChanged += OnContextChanged;
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

    public double AttachmentRailHeight => HasAttachments ? 36 : 1;

    public double AttachmentNoticeHeight => HasAttachments ? double.NaN : 1;

    public bool CanAttach => _draft is not null && Attachments.Count < AttachmentDefaults.MaximumPerDraft;

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
            if (!SetProperty(ref _text, value) || _settingLoadedText || _draft is null)
            {
                return;
            }

            Status = "Unsaved";
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
        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentThreadId = await RunOnUiThreadAsync(() => _threadId).ConfigureAwait(false);
            if (currentThreadId == threadId)
            {
                return;
            }

            await FlushAsync(cancellationToken).ConfigureAwait(false);
            CancelPendingDelay();
            await RunOnUiThreadAsync(() =>
            {
                _threadId = threadId;
                _draft = null;
                SetLoadedText(string.Empty);
                ReplaceContext([]);
                ReplaceAttachments([]);
                Status = threadId is null ? "No active draft" : "Loading draft";
            }).ConfigureAwait(false);
            if (threadId is null)
            {
                return;
            }

            var loaded = await _loadDraft(threadId.Value, cancellationToken).ConfigureAwait(false);
            await RunOnUiThreadAsync(() =>
            {
                if (_threadId != threadId)
                {
                    return;
                }

                _draft = loaded;
                SetLoadedText(loaded.Text);
                ReplaceContext(loaded.Context);
                ReplaceAttachments(loaded.Attachments);
                Status = "Saved";
            }).ConfigureAwait(false);
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
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunOnUiThreadAsync(() => Status = "Clearing sent draft").ConfigureAwait(false);
            var saved = await _clearDraft(sentDraft, cancellationToken).ConfigureAwait(false);
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
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                cancellationToken).ConfigureAwait(false);
            await RunOnUiThreadAsync(() => ApplySavedDraft(saved)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
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
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var draft = await RunOnUiThreadAsync(() => _draft).ConfigureAwait(false);
            if (draft is null)
            {
                return;
            }

            await RunOnUiThreadAsync(() => Status = $"Removing {attachment.FileName}").ConfigureAwait(false);
            var saved = await _removeAttachment(draft, attachment.AttachmentId, cancellationToken)
                .ConfigureAwait(false);
            await RunOnUiThreadAsync(() => ApplySavedDraft(saved)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ContextChips.CollectionChanged -= OnContextChanged;
        CancelPendingDelay();
        await _saveGate.WaitAsync().ConfigureAwait(false);
        _saveGate.Release();
        _saveGate.Dispose();
        _switchGate.Dispose();
    }

    private void ScheduleSave()
    {
        CancelPendingDelay();
        var delay = new CancellationTokenSource();
        _pendingSaveDelay = delay;
        _ = SaveAfterDelayAsync(delay);
    }

    private async Task SaveAfterDelayAsync(CancellationTokenSource delay)
    {
        try
        {
            await Task.Delay(SaveDelay, delay.Token).ConfigureAwait(false);
            await SaveCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (delay.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
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
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            var saved = await _saveDraft(snapshot.Draft with { Context = snapshot.Context }, snapshot.Text, cancellationToken).ConfigureAwait(false);
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
