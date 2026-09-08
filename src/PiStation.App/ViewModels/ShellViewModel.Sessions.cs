using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    public PiSessionsViewModel PiSessions { get; } = new();
    private CopyPiSessionRequest? _pendingSessionCopy;

    public async Task BrowsePiSessionsAsync(bool loadMore = false, CancellationToken cancellationToken = default)
    {
        if (!PiSessions.CanAct) return;
        PiSessions.IsBusy = true;
        PiSessions.Status = "Reading Pi session files…";
        var directory = PiSessions.Directory;
        try
        {
            var result = await RequireClient().BrowsePiSessionsAsync(new(directory, loadMore ? PiSessions.BrowserNextOffset ?? 0 : 0), cancellationToken).ConfigureAwait(false);
            await RunOnUiThreadAsync(() => { if (PiSessions.Directory == directory) PiSessions.Apply(result, loadMore); }).ConfigureAwait(false);
        }
        catch (Exception exception) { RunOnUiThread(() => PiSessions.Status = exception.Message); }
        finally { RunOnUiThread(() => PiSessions.IsBusy = false); }
    }

    public async Task InspectPiSessionAsync(bool loadMore = false, CancellationToken cancellationToken = default)
    {
        var thread = SelectedThread;
        if (thread is null) { PiSessions.Status = "Select a thread first."; return; }
        if (!PiSessions.CanAct) return;
        PiSessions.IsBusy = true;
        PiSessions.Status = "Reading the selected session tree…";
        try
        {
            var result = await RequireClient().InspectPiSessionPageAsync(new(thread.ThreadId,
                loadMore ? PiSessions.Snapshot?.NextOffset ?? 0 : 0, ExpectedRevision: loadMore ? PiSessions.Snapshot?.Revision : null), cancellationToken).ConfigureAwait(false);
            await RunOnUiThreadAsync(() => { if (SelectedThread?.ThreadId == thread.ThreadId) PiSessions.Apply(result, loadMore); }).ConfigureAwait(false);
        }
        catch (Exception exception) { RunOnUiThread(() => PiSessions.Status = exception.Message); }
        finally { RunOnUiThread(() => PiSessions.IsBusy = false); }
    }

    public async Task CopyPiSessionAsync(string? importPath = null, bool forkAtSelection = false, bool copyCurrent = false, CancellationToken cancellationToken = default)
    {
        if (importPath is not null) { await ImportLocalPiSessionAsync(importPath, cancellationToken); return; }
        var project = SelectedProject;
        var previousThreadId = SelectedThread?.ThreadId;
        if (project is null) { PiSessions.Status = "Select a project for the new thread."; return; }
        if (!PiSessions.CanAct) return;
        var snapshot = PiSessions.Snapshot;
        var sourceThread = copyCurrent || forkAtSelection ? SelectedThread?.ThreadId : null;
        if ((copyCurrent || forkAtSelection) && (snapshot is null || snapshot.ThreadId != sourceThread))
        { PiSessions.Status = "Refresh the selected session before copying or forking it."; return; }
        var entry = forkAtSelection ? PiSessions.SelectedEntry?.Entry : null;
        if (forkAtSelection && entry?.CanFork != true) { PiSessions.Status = "Select a completed assistant response to fork."; return; }
        var selectedSource = importPath ?? PiSessions.SelectedCandidate?.Session.Path;
        if (sourceThread is null && string.IsNullOrWhiteSpace(selectedSource)) { PiSessions.Status = "Choose a session to import."; return; }
        var request = new CopyPiSessionRequest(Guid.Empty, project.ProjectId, sourceThread is null ? selectedSource : null, sourceThread,
            entry?.Id, sourceThread is not null ? snapshot?.Revision : importPath is null ? PiSessions.SelectedCandidate?.Session.Revision : null,
            string.IsNullOrWhiteSpace(PiSessions.NewTitle) ? null : PiSessions.NewTitle);
        if (_pendingSessionCopy is null || _pendingSessionCopy with { OperationId = Guid.Empty } != request)
            _pendingSessionCopy = request with { OperationId = Guid.NewGuid() };
        PiSessions.IsBusy = true;
        PiSessions.Status = "Creating an independent session and thread…";
        try
        {
            var client = RequireClient();
            var thread = await client.CopyPiSessionAsync(_pendingSessionCopy, cancellationToken).ConfigureAwait(false);
            _pendingSessionCopy = null;
            await RunOnUiThreadAsync(() =>
            {
                PiSessions.Status = $"Created {thread.Title}. The original session and draft are preserved.";
                if (SelectedProject?.ProjectId != project.ProjectId) return;
                CancelThreadSearch();
                ThreadSearchQuery = string.Empty;
                IsShowingArchivedThreads = false;
                Replace(Threads, client.ThreadMetadata.GetProjectThreads(project.ProjectId));
            }).ConfigureAwait(false);
            if (SelectedProject?.ProjectId == project.ProjectId && SelectedThread?.ThreadId == previousThreadId)
                await SelectThreadAsync(thread, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) { RunOnUiThread(() => PiSessions.Status = exception.Message + " Retry the same action to check its saved outcome."); }
        finally { RunOnUiThread(() => PiSessions.IsBusy = false); }
    }

    public async Task ExportPiSessionAsync(string destinationPath, PiSessionExportFormat format, CancellationToken cancellationToken = default)
    {
        var thread = SelectedThread;
        if (thread is null || !PiSessions.CanAct) return;
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _sessionTransferCancellation = transfer;
        PiSessions.IsBusy = true;
        PiSessions.CanCancelTransfer = true;
        PiSessions.Status = "Preparing session export on the host…";
        try
        {
            var progress = new Progress<long>(bytes =>
            {
                if (ReferenceEquals(_sessionTransferCancellation, transfer)) PiSessions.Status = $"Downloading session: {bytes:N0} bytes…";
            });
            var result = await RequireClient().DownloadPiSessionAsync(thread.ThreadId, destinationPath, format, progress, transfer.Token);
            RunOnUiThread(() => PiSessions.Status = $"Exported {result.Bytes:N0} bytes to {result.Path}");
        }
        catch (OperationCanceledException) when (transfer.IsCancellationRequested)
        { PiSessions.Status = "Export canceled. Any previous destination file was preserved. You can export again."; }
        catch (Exception exception) { RunOnUiThread(() => PiSessions.Status = exception.Message); }
        finally { _sessionTransferCancellation = null; PiSessions.CanCancelTransfer = false; PiSessions.IsBusy = false; }
    }
}
