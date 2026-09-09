using Microsoft.AspNetCore.SignalR;
using PiStation.Host.Errors;
using PiStation.Protocol.Commands;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Hubs;

public sealed class EnvironmentHub(EnvironmentService environment) : Hub
{
    private readonly EnvironmentService _environment = environment ?? throw new ArgumentNullException(nameof(environment));

    public EnvironmentDescriptor GetEnvironmentDescriptor() => _environment.GetDescriptor();
    private string BrowserPrincipal => PiStation.Host.Preview.PreviewLeaseRegistry.Principal(Context.GetHttpContext()!);
    public async Task<BrowserAutomationLease> OpenBrowserAutomation(OpenBrowserAutomationRequest request)
    {
        try { return await _environment.OpenBrowserAutomationAsync(request, BrowserPrincipal, Context.ConnectionId, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException or HostOperationException)
        { throw new HubException(error.Message); }
    }
    public Task<BrowserAutomationPoll> PollBrowserAutomation(string id) =>
        _environment.BrowserAutomation.PollAsync(id, BrowserPrincipal, Context.ConnectionId, Context.ConnectionAborted);
    public Task CompleteBrowserAutomation(string id, string requestId, BrowserAutomationResult result) =>
        _environment.BrowserAutomation.CompleteAsync(id, requestId, result, BrowserPrincipal, Context.ConnectionId, Context.ConnectionAborted);
    public Task CloseBrowserAutomation(string id) =>
        _environment.BrowserAutomation.CloseAsync(id, BrowserPrincipal, Context.ConnectionId);
    public async Task<SourceControlWritingSettings> GetSourceControlWritingSettings()
    {
        try { return await _environment.GetSourceControlWritingSettingsAsync(Context.ConnectionAborted).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ArgumentException or System.Text.Json.JsonException) { throw new HubException(error.Message); }
    }
    public async Task<SourceControlWritingSettings> SaveSourceControlWritingSettings(SourceControlWritingSettings settings)
    {
        try { return await _environment.SaveSourceControlWritingSettingsAsync(settings, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException) { throw new HubException(error.Message); }
    }
    public Task<PiAutomationSettings> GetPiAutomationSettings() => _environment.GetPiAutomationSettingsAsync(Context.ConnectionAborted);
    public Task<PiAutomationSettings> SavePiAutomationSettings(PiAutomationSettings settings) => _environment.SavePiAutomationSettingsAsync(settings, Context.ConnectionAborted);
    public Task<PiAutomationStatus> GetPiAutomationStatus(ThreadId threadId) => _environment.GetPiAutomationStatusAsync(threadId, Context.ConnectionAborted);
    public Task<PiAutomationStatus> ApplyPiAutomation(ThreadId threadId) => _environment.ApplyPiAutomationAsync(threadId, Context.ConnectionAborted);
    public Task<SettlementSettings> GetSettlementSettings() => _environment.GetSettlementSettingsAsync(Context.ConnectionAborted);
    public Task SaveSettlementSettings(SettlementSettings settings) => _environment.SaveSettlementSettingsAsync(settings, Context.ConnectionAborted);

    public Task<BackgroundTaskResult> SubmitBackgroundTask(SubmitBackgroundTaskRequest request) =>
        _environment.SubmitBackgroundTaskAsync(request, Context.ConnectionAborted);

    public RemoteUpdateDescriptor GetRemoteUpdateDescriptor() => _environment.Updates.Descriptor;
    public RemoteUpdateReceipt[] GetRemoteUpdateHistory() => _environment.Updates.GetHistory(
        PiStation.Host.Preview.PreviewLeaseRegistry.Principal(Context.GetHttpContext()!));
    public RemoteUpdateReceipt? GetRemoteUpdateReceipt(Guid id) => _environment.Updates.GetReceipt(id);
    public RemoteUpdateReceipt PrepareRemoteUpdate(PrepareRemoteUpdateRequest request) => _environment.Updates.Prepare(request,
        PiStation.Host.Preview.PreviewLeaseRegistry.Principal(Context.GetHttpContext()!));
    public RemoteUpdateReceipt CommitRemoteUpdate(CommitRemoteUpdateRequest request) => _environment.Updates.Commit(request,
        PiStation.Host.Preview.PreviewLeaseRegistry.Principal(Context.GetHttpContext()!));
    public RemoteUpdateReceipt CancelRemoteUpdate(Guid id) => _environment.Updates.Cancel(id,
        PiStation.Host.Preview.PreviewLeaseRegistry.Principal(Context.GetHttpContext()!));

    public Task<PreviewLease> OpenPreview(OpenPreviewRequest request) => _environment.OpenPreviewAsync(request,
        PiStation.Host.Preview.PreviewLeaseRegistry.Principal(Context.GetHttpContext()!), Context.ConnectionId, Context.ConnectionAborted);

    public PreviewLease RenewPreview(string id) => _environment.PreviewLeases.Renew(id,
        PiStation.Host.Preview.PreviewLeaseRegistry.Principal(Context.GetHttpContext()!), Context.ConnectionId);

    public void ClosePreview(string id) => _environment.PreviewLeases.Close(id,
        PiStation.Host.Preview.PreviewLeaseRegistry.Principal(Context.GetHttpContext()!));

    public async IAsyncEnumerable<CatalogBatch> SubscribeCatalog(CatalogCursor? cursor,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Context.ConnectionAborted);
        await foreach (var batch in _environment.SubscribeCatalogAsync(cursor, lifetime.Token).ConfigureAwait(false)) yield return batch;
    }

    public async Task<ProjectDescriptor[]> ListProjects() =>
        [.. await _environment.ListProjectsAsync(Context.ConnectionAborted).ConfigureAwait(false)];

    public Task<ProjectDescriptor> AddProject(AddProjectRequest request) =>
        _environment.AddProjectAsync(request, Context.ConnectionAborted);

    public async Task RemoveProject(RemoveProjectRequest request)
    {
        try { await _environment.RemoveProjectAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<ProjectDescriptor> UpdateProjectDefaults(UpdateProjectDefaultsRequest request)
    {
        try { return await _environment.UpdateProjectDefaultsAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        { throw new HubException(exception.Message); }
    }

    public async Task<ProjectDescriptor> SetProjectScriptsTrust(SetProjectScriptsTrustRequest request)
    {
        try
        {
            return await _environment.SetProjectScriptsTrustAsync(request, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<ProjectSetupScriptResult> RunProjectSetupScript(RunProjectSetupScriptRequest request)
    {
        try
        {
            return await _environment.RunProjectSetupScriptAsync(request, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<ProjectSetupScriptResult> RunProjectScript(RunProjectScriptRequest request)
    {
        try { return await _environment.RunProjectScriptAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<ComposerDiscoveryResult> GetComposerDiscovery(ThreadId threadId)
    {
        try { return await _environment.GetComposerDiscoveryAsync(threadId, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<PromptStash[]> ListPromptStashes(ProjectId projectId)
    {
        try { return [.. await _environment.ListPromptStashesAsync(projectId, Context.ConnectionAborted).ConfigureAwait(false)]; }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<PromptStash> SavePromptStash(SavePromptStashRequest request)
    {
        try { return await _environment.SavePromptStashAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task DeletePromptStash(DeletePromptStashRequest request)
    {
        try { await _environment.DeletePromptStashAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<SourceControlRepository> DetectSourceControl(DetectSourceControlRequest request)
    {
        try { return await _environment.DetectSourceControlAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<PullRequestReviewSnapshot> GetPullRequestReview(GetPullRequestReviewRequest request)
    {
        try { return await _environment.GetPullRequestReviewAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<SourceControlOperationResult> ManagePullRequest(ManagePullRequestRequest request)
    {
        try { return await _environment.ManagePullRequestAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<PullRequestWorkflowsResult> GetPullRequestWorkflows(GetPullRequestWorkflowsRequest request)
    {
        try { return await _environment.GetPullRequestWorkflowsAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<ThreadDescriptor> CreatePullRequestReviewThread(CreatePullRequestReviewThreadRequest request)
    {
        try { return await _environment.CreatePullRequestReviewThreadAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<SourceControlOperationResult> SubmitPullRequestReview(SubmitPullRequestReviewRequest request)
    {
        try { return await _environment.SubmitPullRequestReviewAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<SourceControlOperationResult> ReplyPullRequestThread(ReplyPullRequestThreadRequest request)
    {
        try { return await _environment.ReplyPullRequestThreadAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<SourceControlOperationResult> SetPullRequestThreadResolved(SetPullRequestThreadResolvedRequest request)
    {
        try { return await _environment.SetPullRequestThreadResolvedAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<ListPullRequestsResult> ListPullRequests(ListPullRequestsRequest request)
    {
        try { return await _environment.ListPullRequestsAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<SourceControlOperationResult> CloneHostedRepository(CloneHostedRepositoryRequest request)
    {
        try { return await _environment.CloneHostedRepositoryAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<SourceControlOperationResult> PublishHostedRepository(PublishHostedRepositoryRequest request)
    {
        try { return await _environment.PublishHostedRepositoryAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<SourceControlOperationResult> CreatePullRequest(CreatePullRequestRequest request)
    {
        try { return await _environment.CreatePullRequestAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<SourceControlOperationResult> MutatePullRequest(MutatePullRequestRequest request)
    {
        try { return await _environment.MutatePullRequestAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<GeneratedSourceControlText> GenerateSourceControlText(GenerateSourceControlTextRequest request)
    {
        try { return await _environment.GenerateSourceControlTextAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or PiStation.PiRpc.Diagnostics.PiRpcException) { throw new HubException(error.Message); }
    }

    public Task<DiagnosticsSnapshot> GetDiagnostics() =>
        _environment.GetDiagnosticsAsync(Context.ConnectionAborted);

    public Task<PiRuntimeSetupResult> ConfigurePiRuntime(ConfigurePiRuntimeRequest request) =>
        _environment.ConfigurePiRuntimeAsync(request, Context.ConnectionAborted);

    public Task<PiRuntimeConfiguration> GetPiRuntimeConfiguration() =>
        SessionOperationAsync(() => _environment.GetPiRuntimeConfigurationAsync(Context.ConnectionAborted));

    public Task<DiagnosticsDownload> DownloadDiagnostics() =>
        SessionOperationAsync(() => _environment.DownloadDiagnosticsAsync(Context.ConnectionAborted));

    public Task<HostPathPage> BrowseHostPath(BrowseHostPathRequest request) =>
        SessionOperationAsync(() => _environment.BrowseHostPathAsync(request, Context.ConnectionAborted));

    public Task<byte[]?> ReadProjectIcon(ProjectId projectId) =>
        SessionOperationAsync(() => _environment.ReadProjectIconAsync(projectId, Context.ConnectionAborted));

    public async Task<PiResourcesSnapshot> ManagePiResources(ManagePiResourcesRequest request)
    {
        try { return await _environment.ManagePiResourcesAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public Task<PiSessionBrowserResult> BrowsePiSessions(BrowsePiSessionsRequest request) =>
        SessionOperationAsync(() => _environment.BrowsePiSessionsAsync(request, Context.ConnectionAborted));
    public Task<PiSessionSnapshot> InspectPiSession(ThreadId threadId) =>
        SessionOperationAsync(() => _environment.InspectPiSessionAsync(threadId, Context.ConnectionAborted));
    public Task<PiSessionSnapshot> InspectPiSessionPage(PiSessionPageRequest request) =>
        SessionOperationAsync(() => _environment.InspectPiSessionPageAsync(request, Context.ConnectionAborted));
    public Task<ThreadDescriptor> CopyPiSession(CopyPiSessionRequest request) =>
        SessionOperationAsync(() => _environment.CopyPiSessionAsync(request, Context.ConnectionAborted));
    public Task<PiSessionExportResult> ExportPiSession(ExportPiSessionRequest request) =>
        SessionOperationAsync(() => _environment.ExportPiSessionAsync(request, Context.ConnectionAborted));

    private static async Task<T> SessionOperationAsync<T>(Func<Task<T>> operation)
    {
        try { return await operation().ConfigureAwait(false); }
        catch (Exception exception) when (exception is HostOperationException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        { throw new HubException(exception.Message); }
    }

    public async Task<PiSetupTerminalResult> StartPiSetup(StartPiSetupRequest request)
    {
        try { return await _environment.StartPiSetupAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (Exception exception) when (exception is HostOperationException or ArgumentException) { throw new HubException(exception.Message); }
    }

    public async Task<HostingOperation[]> ListHostingOperations() =>
        [.. await _environment.ListHostingOperationsAsync(Context.ConnectionAborted).ConfigureAwait(false)];

    public async Task<ExportDiagnosticsResult> ExportDiagnostics(ExportDiagnosticsRequest request)
    {
        try { return await _environment.ExportDiagnosticsAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<SearchProjectFilesResult> SearchProjectFiles(SearchProjectFilesRequest request)
    {
        try
        {
            return await _environment.SearchProjectFilesAsync(request, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<ReadProjectFileResult> ReadProjectFile(ReadProjectFileRequest request)
    {
        try
        {
            return await _environment.ReadProjectFileAsync(request, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<ReadArtifactFileResult> ReadArtifactFile(ReadArtifactFileRequest request)
    {
        try { return await _environment.ReadArtifactFileAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException error) { throw new HubException($"{error.Code}: {error.Message}"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { throw new HubException(error.Message); }
    }

    public async Task<ListProjectEntriesResult> ListProjectEntries(ListProjectEntriesRequest request)
    {
        try
        {
            return await _environment.ListProjectEntriesAsync(request, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<SearchProjectContentsResult> SearchProjectContents(SearchProjectContentsRequest request)
    {
        try
        {
            return await _environment.SearchProjectContentsAsync(request, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<ReadProjectFileAssetResult> ReadProjectFileAsset(ReadProjectFileAssetRequest request)
    {
        try
        {
            return await _environment.ReadProjectFileAssetAsync(request, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<SaveProjectFileResult> SaveProjectFile(SaveProjectFileRequest request)
    {
        try
        {
            return await _environment.SaveProjectFileAsync(request, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<OpenProjectFileInEditorResult> OpenProjectFileInEditor(OpenProjectFileInEditorRequest request)
    {
        try
        {
            return await _environment.OpenProjectFileInEditorAsync(request, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<GetProjectChangesResult> GetProjectChanges(GetProjectChangesRequest request)
    {
        try
        {
            return await _environment.GetProjectChangesAsync(request, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<GetProjectChangeDiffResult> GetProjectChangeDiff(GetProjectChangeDiffRequest request)
    {
        try
        {
            return await _environment.GetProjectChangeDiffAsync(request, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<ListGitRefsResult> ListGitRefs(ListGitRefsRequest request)
    {
        try
        {
            return await _environment.ListGitRefsAsync(request, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<ListGitWorktreesResult> ListGitWorktrees(ListGitWorktreesRequest request)
    {
        try
        {
            return await _environment.ListGitWorktreesAsync(request, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<ExecuteWorkspaceGitCommandResult> ExecuteWorkspaceGitCommand(
        ExecuteWorkspaceGitCommandRequest request)
    {
        try
        {
            return await _environment.ExecuteWorkspaceGitCommandAsync(request, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public Task<ExecuteWorkspaceGitCommandResult?> GetWorkspaceGitCommandResult(
        ClientId clientId,
        CommandId commandId) =>
        _environment.GetWorkspaceGitCommandResultAsync(clientId, commandId, Context.ConnectionAborted);

    public async Task<GetThreadCheckpointDiffResult> GetThreadCheckpointDiff(
        GetThreadCheckpointDiffRequest request)
    {
        try
        {
            return await _environment.GetThreadCheckpointDiffAsync(request, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<DiscoverProjectPreviewServersResult> DiscoverProjectPreviewServers(
        DiscoverProjectPreviewServersRequest request)
    {
        try
        {
            return await _environment.DiscoverProjectPreviewServersAsync(
                request,
                Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<TerminalSessionDescriptor> StartTerminalSession(StartTerminalSessionRequest request)
    {
        try
        {
            return await _environment.StartTerminalSessionAsync(request, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<TerminalSessionDescriptor[]> ListTerminalSessions(ProjectId projectId)
    {
        try
        {
            return [.. await _environment.ListTerminalSessionsAsync(projectId, Context.ConnectionAborted)
                .ConfigureAwait(false)];
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task WriteTerminalInput(WriteTerminalInputRequest request)
    {
        try
        {
            await _environment.WriteTerminalInputAsync(request, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<TerminalSessionDescriptor> ResizeTerminalSession(ResizeTerminalSessionRequest request)
    {
        try
        {
            return await _environment.ResizeTerminalSessionAsync(request, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<TerminalSessionDescriptor> StopTerminalSession(StopTerminalSessionRequest request)
    {
        try
        {
            return await _environment.StopTerminalSessionAsync(request, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task CloseTerminalSession(CloseTerminalSessionRequest request)
    {
        try
        {
            await _environment.CloseTerminalSessionAsync(request, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<ThreadDescriptor[]> ListThreads(ProjectId projectId) =>
        [.. await _environment.ListThreadsAsync(projectId, Context.ConnectionAborted).ConfigureAwait(false)];

    public async Task<ThreadDescriptor> GetThread(ThreadId threadId)
    {
        try
        {
            return await _environment.GetThreadAsync(threadId, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<SearchThreadsResult> SearchThreads(SearchThreadsRequest request)
    {
        try
        {
            return await _environment.SearchThreadsAsync(request, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<GlobalSearchResult> SearchGlobal(GlobalSearchRequest request)
    {
        try
        {
            return await _environment.SearchGlobalAsync(request, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public Task<ThreadDescriptor> CreateThread(CreateThreadRequest request) =>
        _environment.CreateThreadAsync(request, Context.ConnectionAborted);

    public async Task DeleteThread(DeleteThreadRequest request)
    {
        try { await _environment.DeleteThreadAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<ApplyThreadBulkOperationResult> ApplyThreadBulkOperation(ApplyThreadBulkOperationRequest request)
    {
        try { return await _environment.ApplyThreadBulkOperationAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<ThreadDescriptor[]> SetThreadPinnedOrder(SetThreadPinnedOrderRequest request)
    {
        try { return [.. await _environment.SetThreadPinnedOrderAsync(request, Context.ConnectionAborted).ConfigureAwait(false)]; }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<ThreadDescriptor> LinkThreadPullRequest(LinkThreadPullRequestRequest request)
    {
        try { return await _environment.LinkThreadPullRequestAsync(request, Context.ConnectionAborted).ConfigureAwait(false); }
        catch (HostOperationException exception) { throw new HubException($"{exception.Code}: {exception.Message}"); }
    }

    public async Task<ThreadDraft> GetThreadDraft(ThreadId threadId)
    {
        try
        {
            var passive = _environment.Updates.IsDraining || Context.Items.TryGetValue(PiStation.Host.Security.RemoteAuthorizationFilter.ReadOnlyItem, out var readOnly) &&
                readOnly is true;
            return await (passive
                ? _environment.GetThreadDraftPassiveAsync(threadId, Context.ConnectionAborted)
                : _environment.GetThreadDraftAsync(threadId, Context.ConnectionAborted)).ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<ThreadPiConfigurationSnapshot> GetThreadPiConfiguration(ThreadId threadId)
    {
        try
        {
            var passive = _environment.Updates.IsDraining || Context.Items.TryGetValue(PiStation.Host.Security.RemoteAuthorizationFilter.ReadOnlyItem, out var readOnly) &&
                readOnly is true;
            return await (passive
                    ? _environment.GetThreadPiConfigurationPassiveAsync(threadId, Context.ConnectionAborted)
                    : _environment.GetThreadPiConfigurationAsync(threadId, Context.ConnectionAborted))
                .ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public async Task<CommandReceipt> ExecuteThreadCommand(ExecuteThreadCommandRequest request)
    {
        try
        {
            return await _environment.ExecuteThreadCommandAsync(request, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            throw new HubException($"{exception.Code}: {exception.Message}");
        }
    }

    public Task<CommandReceipt?> GetCommandReceipt(
        ClientId clientId,
        CommandId commandId) =>
        _environment.GetCommandReceiptAsync(clientId, commandId, Context.ConnectionAborted);

    public async IAsyncEnumerable<ThreadEnvelope> SubscribeThread(
        ThreadId threadId,
        ThreadCursor? cursor,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Context.ConnectionAborted);
        using var tracked = _environment.TrackStream(terminal: false);
        var stream = _environment.Updates.IsDraining || Context.Items.TryGetValue(PiStation.Host.Security.RemoteAuthorizationFilter.ReadOnlyItem, out var readOnly) && readOnly is true
            ? _environment.SubscribeThreadPassiveAsync(threadId, cursor, linked.Token)
            : _environment.SubscribeThreadAsync(threadId, cursor, linked.Token);
        await foreach (var item in stream.ConfigureAwait(false)) yield return item;
    }

    public async IAsyncEnumerable<TerminalEnvelope> SubscribeTerminal(
        TerminalSessionId terminalSessionId,
        TerminalCursor? cursor,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Context.ConnectionAborted);
        using var tracked = _environment.TrackStream(terminal: true);
        await foreach (var item in _environment.SubscribeTerminalAsync(terminalSessionId, cursor, linked.Token).ConfigureAwait(false))
            yield return item;
    }
}
