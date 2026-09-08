using PiStation.ClientRuntime;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    public Microsoft.UI.Xaml.Visibility LocalSessionFolderPickerVisibility => IsRemote
        ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    private PiSessionImportFile? _pendingSessionImport;
    private CancellationTokenSource? _sessionTransferCancellation;
    private Task? _sessionImportRecoveryLoad;
    private PiSessionImportRecoveryStore SessionImportRecovery => new(Path.Combine(Path.GetDirectoryName(_attachmentCacheRoot)!, "session-import-recovery"));

    private Task RestorePendingSessionImportAsync()
    {
        if (_client?.Descriptor is not { } descriptor) return Task.CompletedTask;
        return _sessionImportRecoveryLoad ??= LoadPendingSessionImportAsync(descriptor.EnvironmentId);
    }

    private async Task LoadPendingSessionImportAsync(EnvironmentId environmentId)
    {
        try
        {
            var pending = await SessionImportRecovery.LoadAsync(environmentId);
            if (pending is null || _pendingSessionImport is not null || PiSessions.IsBusy) return;
            _pendingSessionImport = pending;
            PiSessions.HasPendingImport = true;
            PiSessions.Status = "A previous file import needs checking. Retry last file import to recover its result.";
        }
        catch (Exception error) { PiSessions.Status = $"Could not restore the last file import: {error.Message}"; }
    }

    public void CancelSessionTransfer() => _sessionTransferCancellation?.Cancel();

    public Task RetryPiSessionImportAsync(CancellationToken cancellationToken = default) =>
        ImportLocalPiSessionAsync(null, cancellationToken);

    public async Task ImportLocalPiSessionAsync(string? filePath, CancellationToken cancellationToken = default)
    {
        await RestorePendingSessionImportAsync();
        if (!PiSessions.CanAct) return;
        var project = SelectedProject;
        if (project is null) { PiSessions.Status = "Select a project for the imported session."; return; }
        if (filePath is null && _pendingSessionImport?.Request.ProjectId != project.ProjectId)
        { PiSessions.Status = "Select the original target project before retrying its file import."; return; }
        var previousThreadId = SelectedThread?.ThreadId;
        var client = RequireClient();
        var environmentId = client.Descriptor!.EnvironmentId;
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _sessionTransferCancellation = transfer;
        PiSessions.IsBusy = true;
        PiSessions.CanCancelTransfer = true;
        try
        {
            if (filePath is not null)
            {
                PiSessions.Status = "Checking the local session file…";
                var pending = await PiSessionImportFile.CreateAsync(project.ProjectId, filePath, PiSessions.NewTitle, transfer.Token);
                if (_pendingSessionImport is { } previous &&
                    string.Equals(previous.FilePath, pending.FilePath, StringComparison.OrdinalIgnoreCase) &&
                    previous.Request with { OperationId = Guid.Empty } == pending.Request with { OperationId = Guid.Empty })
                    pending = previous;
                await SessionImportRecovery.SaveAsync(environmentId, pending, transfer.Token);
                _pendingSessionImport = pending;
                PiSessions.HasPendingImport = true;
            }
            var import = _pendingSessionImport ?? throw new InvalidOperationException("There is no file import to retry.");
            PiSessions.Status = "Checking the import's saved result, then uploading if needed…";
            var progress = new Progress<long>(bytes =>
            {
                if (ReferenceEquals(_sessionTransferCancellation, transfer))
                    PiSessions.Status = $"Uploading session: {bytes:N0} of {import.Request.ByteLength:N0} bytes…";
            });
            var thread = await client.ImportPiSessionFileAsync(import, progress, transfer.Token);
            await SessionImportRecovery.ClearAsync(environmentId, import.Request.OperationId, CancellationToken.None);
            _pendingSessionImport = null;
            PiSessions.HasPendingImport = false;
            await ShowCopiedSessionAsync(thread, project.ProjectId, previousThreadId, CancellationToken.None);
        }
        catch (OperationCanceledException) when (transfer.IsCancellationRequested)
        { PiSessions.Status = "File transfer canceled. Retry last file import to check whether the host completed it."; }
        catch (Exception error)
        { PiSessions.Status = error.Message + " Retry last file import to check its saved result."; }
        finally
        {
            _sessionTransferCancellation = null;
            PiSessions.CanCancelTransfer = false;
            PiSessions.IsBusy = false;
        }
    }

    private async Task ShowCopiedSessionAsync(ThreadDescriptor thread, ProjectId projectId, ThreadId? previousThreadId, CancellationToken cancellationToken)
    {
        await RunOnUiThreadAsync(() =>
        {
            PiSessions.Status = $"Created {thread.Title} on {EnvironmentLabel}. The original session and draft are preserved.";
            if (SelectedProject?.ProjectId != projectId) return;
            CancelThreadSearch();
            ThreadSearchQuery = string.Empty;
            IsShowingArchivedThreads = false;
            Replace(Threads, RequireClient().ThreadMetadata.GetProjectThreads(projectId));
        });
        if (!_runtimeStopped && SelectedProject?.ProjectId == projectId && SelectedThread?.ThreadId == previousThreadId)
            await SelectThreadAsync(thread, cancellationToken);
    }
}
