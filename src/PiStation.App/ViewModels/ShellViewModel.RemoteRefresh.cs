using Microsoft.UI.Dispatching;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    private DispatcherQueueTimer? _remoteRefreshTimer;
    private CancellationTokenSource? _remoteRefreshCancellation;
    private bool _windowActive;
    private bool _remoteRefreshPending;

    internal void SetWindowActive(bool active)
    {
        _windowActive = active;
        if (!IsRemote || active && _runtimeStopped) return;
        if (!active)
        {
            _remoteRefreshTimer?.Stop();
            if (_runtimeStopped && _remoteRefreshTimer is { } timer)
            {
                timer.Tick -= OnRemoteRefreshTick;
                _remoteRefreshTimer = null;
            }
            _remoteRefreshCancellation?.Cancel();
            return;
        }
        if (_remoteRefreshTimer is null)
        {
            _remoteRefreshTimer = _dispatcherQueue.CreateTimer();
            _remoteRefreshTimer.Interval = TimeSpan.FromSeconds(5);
            _remoteRefreshTimer.Tick += OnRemoteRefreshTick;
        }
        _remoteRefreshTimer.Start();
        _ = RefreshRemoteWorkspaceAsync();
    }

    private void OnRemoteRefreshTick(DispatcherQueueTimer sender, object args) => _ = RefreshRemoteWorkspaceAsync();

    private async Task RefreshRemoteWorkspaceAsync()
    {
        if (!_windowActive || !IsRemote || _runtimeStopped || _remoteRefreshPending || !IsConnected ||
            _commandPending || SelectedProject is not { } project) return;
        _remoteRefreshPending = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _remoteRefreshCancellation = cancellation;
        var token = cancellation.Token;
        var threadId = SelectedThread?.ThreadId;
        var panel = Layout.SelectedPanel;
        bool IsCurrent() => !token.IsCancellationRequested && !_runtimeStopped && IsConnected &&
            SelectedProject?.ProjectId == project.ProjectId && SelectedThread?.ThreadId == threadId && Layout.SelectedPanel == panel;
        try
        {
            if (threadId is { } thread && !PiConfiguration.IsPending)
                await RequireClient().GetThreadPiConfigurationAsync(thread, token);
            if (!IsCurrent()) return;
            if (panel == WorkbenchPanelKind.Files)
            {
                if (!_loadingMoreFiles && string.IsNullOrEmpty(WorkbenchFiles.SearchQuery) && WorkbenchFiles.SearchMode == WorkspaceFileSearchMode.Paths)
                {
                    var generation = _workbenchFileSearchCancellation;
                    var loadedCount = WorkbenchFiles.LoadedEntryCount;
                    var oldOffset = WorkbenchFiles.NextOffset;
                    var entries = new List<ProjectWorkspaceEntry>();
                    int? nextOffset = 0;
                    do
                    {
                        var page = await RequireClient().ListProjectEntriesAsync(new(project.ProjectId, ThreadId: threadId, Offset: nextOffset!.Value), token);
                        entries.AddRange(page.Entries);
                        nextOffset = page.NextOffset;
                    } while (nextOffset is not null && entries.Count < loadedCount);
                    if (!IsCurrent()) return;
                    if (!_loadingMoreFiles && ReferenceEquals(generation, _workbenchFileSearchCancellation) &&
                        WorkbenchFiles.LoadedEntryCount == loadedCount && WorkbenchFiles.NextOffset == oldOffset &&
                        string.IsNullOrEmpty(WorkbenchFiles.SearchQuery) && WorkbenchFiles.SearchMode == WorkspaceFileSearchMode.Paths)
                    {
                        WorkbenchFiles.ApplyEntriesIfChanged(entries);
                        WorkbenchFiles.NextOffset = nextOffset;
                    }
                }
                if (WorkbenchFiles.ActiveDocument is not { IsDirty: false, IsSaving: false, IsLoading: false, IsExternal: false, UsesAssetContent: false } document) return;
                var version = document.EditVersion;
                var revision = document.Revision;
                var result = await RequireClient().ReadProjectFileAsync(new(project.ProjectId, document.RelativePath, ThreadId: threadId), token);
                if (IsCurrent() && WorkbenchFiles.OpenDocuments.Contains(document) && !document.IsDirty && !document.IsSaving &&
                    document.Revision == revision && result.Revision != revision)
                    document.ApplyTextIfUnchanged(result, version);
            }
            else if (panel == WorkbenchPanelKind.Changes && _workbenchChangesLoadCancellation is null && _workbenchDiffLoadCancellation is null)
            {
                if (WorkbenchChanges.SelectedChange is null && WorkbenchChanges.DiffContent.Length > 0) return;
                var result = await RequireClient().GetProjectChangesAsync(new(project.ProjectId, ThreadId: threadId), token);
                if (!IsCurrent() || _commandPending || _workbenchChangesLoadCancellation is not null || _workbenchDiffLoadCancellation is not null) return;
                if (!WorkbenchChanges.Matches(result))
                {
                    var selected = WorkbenchChanges.SelectedChange?.RelativePath;
                    WorkbenchChanges.Apply(result);
                    if (selected is not null)
                        await SelectWorkbenchChangeAsync(WorkbenchChanges.Changes.FirstOrDefault(change => change.RelativePath == selected));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            // The connection supervisor presents transport failures. The next active refresh
            // retries metadata reads without clearing already displayed work or local edits.
        }
        finally
        {
            _remoteRefreshCancellation = null;
            _remoteRefreshPending = false;
        }
    }
}
