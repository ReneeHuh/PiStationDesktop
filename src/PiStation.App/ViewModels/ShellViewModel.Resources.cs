using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    public PiResourcesViewModel PiResources { get; } = new();
    private int _packageSearchOffset;
    private string _packageSearchQuery = "";
    public async Task SearchPiPackagesAsync(bool loadMore)
    {
        if (PiResources.IsBusy) return;
        if (!loadMore || _packageSearchQuery != PiResources.PackageSearchQuery)
        {
            PiResources.ResetPackageSearch(); _packageSearchOffset = 0; _packageSearchQuery = PiResources.PackageSearchQuery;
        }
        else if (PiResources.NextPackageSearchOffset is { } offset) _packageSearchOffset = offset;
        else return;
        await ManagePiResourcesAsync("packageSearch");
    }

    public Task RefreshPiResourcesAsync(CancellationToken cancellationToken = default) =>
        ManagePiResourcesAsync("inspect", cancellationToken: cancellationToken);

    public async Task ManagePiResourcesAsync(string action, PiResourceRow? resource = null, bool? enabled = null,
        CancellationToken cancellationToken = default)
    {
        var thread = SelectedThread;
        if (thread is null) { PiResources.Status = "Select a thread to inspect and manage its Pi resources."; return; }
        if (PiResources.IsBusy) return;
        if (action != "inspect" && (PiResources.ThreadId != thread.ThreadId || PiResources.Snapshot is null))
        {
            PiResources.Status = "Refresh the selected thread before making changes.";
            return;
        }
        var apiKeyLogin = action == "loginApiKey";
        if (apiKeyLogin) action = "login";
        var request = new ManagePiResourcesRequest(thread.ThreadId, action,
            action is "login" or "logout" ? PiResources.SelectedProvider?.ProviderId : resource?.Resource.Id, enabled,
            action == "saveTransport" ? PiResources.Snapshot?.NativePreferences?.TransportRevision : action == "saveModel" ? PiResources.Snapshot?.ModelsRevision : resource?.Resource.Revision,
            action == "saveModel" ? PiResources.CreateModel() : null,
            PiResources.PackageSource.Trim(), PiResources.PackageLocal, PiResources.PackageSearchQuery,
            action == "packageSearch" ? _packageSearchOffset : 0, action == "saveTransport" ? PiResources.Transport : null, action == "login" ? apiKeyLogin ? "api_key" : "oauth" : null, action == "toolExecution" ? PiResources.ToolExecution : null);
        PiResources.IsBusy = true;
        PiResources.Status = action == "inspect" ? "Reading Pi resources…" : "Saving Pi configuration…";
        try
        {
            var snapshot = await RequireClient().ManagePiResourcesAsync(request, cancellationToken).ConfigureAwait(false);
            await RunOnUiThreadAsync(() =>
            {
                if (SelectedThread?.ThreadId == thread.ThreadId) PiResources.Apply(thread.ThreadId, snapshot);
            }).ConfigureAwait(false);
            if (action is "reload" or "login" or "logout")
                await RefreshPiConfigurationAsync(thread.ThreadId, cancellationToken).ConfigureAwait(false);
            if (action == "reload")
            {
                var discovery = await RequireClient().GetComposerDiscoveryAsync(thread.ThreadId, cancellationToken).ConfigureAwait(false);
                await RunOnUiThreadAsync(() => { if (SelectedThread?.ThreadId == thread.ThreadId) ComposerPower.ApplyDiscovery(discovery); }).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            RunOnUiThread(() => { if (SelectedThread?.ThreadId == thread.ThreadId) PiResources.Status = exception.Message; });
        }
        finally { RunOnUiThread(() => PiResources.IsBusy = false); }
    }

    public async Task StartPiSetupTerminalAsync(string action, CancellationToken cancellationToken = default)
    {
        var project = SelectedProject;
        var threadId = SelectedThread?.ThreadId;
        if (project is null) { PiResources.Status = "Add or select a project before opening Pi setup."; return; }
        try
        {
            var result = await RequireClient().StartPiSetupAsync(new(project.ProjectId, threadId, action), cancellationToken).ConfigureAwait(false);
            var sessions = FilterTerminalSessions(
                await RequireClient().ListTerminalSessionsAsync(project.ProjectId, cancellationToken).ConfigureAwait(false), threadId);
            await RunOnUiThreadAsync(() =>
            {
                PiResources.Status = result.Instructions;
                if (SelectedProject?.ProjectId != project.ProjectId || SelectedThread?.ThreadId != threadId) return;
                WorkbenchTerminal.ApplySessions(sessions, result.Terminal.TerminalSessionId);
                WorkbenchTerminal.Status = result.Instructions;
                Layout.IsRightPanelOpen = true;
                Layout.SelectedPanel = WorkbenchPanelKind.Terminal;
                SaveWorkbenchTerminalLayout();
            }).ConfigureAwait(false);
            await SynchronizeTerminalSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RunOnUiThread(() =>
            {
                PiResources.Status = exception.Message;
                WorkbenchTerminal.Status = exception.Message;
                Layout.IsRightPanelOpen = true;
                Layout.SelectedPanel = WorkbenchPanelKind.Terminal;
            });
        }
    }
}
