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

    public async Task<ProjectDescriptor[]> ListProjects() =>
        [.. await _environment.ListProjectsAsync(Context.ConnectionAborted).ConfigureAwait(false)];

    public Task<ProjectDescriptor> AddProject(AddProjectRequest request) =>
        _environment.AddProjectAsync(request, Context.ConnectionAborted);

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

    public async Task<ThreadDraft> GetThreadDraft(ThreadId threadId)
    {
        try
        {
            return await _environment.GetThreadDraftAsync(threadId, Context.ConnectionAborted).ConfigureAwait(false);
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
            return await _environment.GetThreadPiConfigurationAsync(threadId, Context.ConnectionAborted)
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

    public IAsyncEnumerable<ThreadEnvelope> SubscribeThread(
        ThreadId threadId,
        ThreadCursor? cursor) =>
        _environment.SubscribeThreadAsync(threadId, cursor, Context.ConnectionAborted);

    public IAsyncEnumerable<TerminalEnvelope> SubscribeTerminal(
        TerminalSessionId terminalSessionId,
        TerminalCursor? cursor) =>
        _environment.SubscribeTerminalAsync(terminalSessionId, cursor, Context.ConnectionAborted);
}
