using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using PiStation.Protocol;
using PiStation.Protocol.Commands;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime;

public sealed class EnvironmentClient : IEnvironmentClient
{
    private readonly ConcurrentDictionary<ThreadId, ThreadSubscription> _subscriptions = new();
    private readonly ConcurrentDictionary<TerminalSessionId, TerminalSubscription> _terminalSubscriptions = new();
    private readonly ClientRuntimeOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ConnectionSupervisor _supervisor;
    private readonly SemaphoreSlim _synchronizationGate = new(1, 1);
    private bool _disposed;

    public EnvironmentClient(ClientRuntimeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        var baseAddress = new UriBuilder(_options.HubAddress)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
        _httpClient = new HttpClient { BaseAddress = baseAddress };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.BearerCredential);
        _supervisor = new ConnectionSupervisor(options);
        _supervisor.StateChanged += OnStateChanged;
        _supervisor.Reconnected += OnReconnected;
    }

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public EnvironmentConnectionState ConnectionState => _supervisor.State;

    public EnvironmentDescriptor? Descriptor { get; private set; }

    public PiConfigurationStore PiConfigurations { get; } = new();

    public ThreadMetadataStore ThreadMetadata { get; } = new();

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _supervisor.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _supervisor.DisconnectAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectDescriptor>> ListProjectsAsync(
        CancellationToken cancellationToken = default) =>
        await InvokeAsync<ProjectDescriptor[]>("ListProjects", cancellationToken).ConfigureAwait(false);

    public Task<ProjectDescriptor> AddProjectAsync(
        AddProjectRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<ProjectDescriptor>("AddProject", request, cancellationToken);

    public async Task RemoveProjectAsync(
        RemoveProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConnected();
        await _supervisor.Connection.InvokeAsync("RemoveProject", request, cancellationToken).ConfigureAwait(false);
        ThreadMetadata.RemoveProject(request.ProjectId);
    }

    public Task<ProjectDescriptor> UpdateProjectDefaultsAsync(
        UpdateProjectDefaultsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<ProjectDescriptor>("UpdateProjectDefaults", request, cancellationToken);
    }

    public Task<ProjectDescriptor> SetProjectScriptsTrustAsync(
        SetProjectScriptsTrustRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<ProjectDescriptor>("SetProjectScriptsTrust", request, cancellationToken);
    }

    public Task<ProjectSetupScriptResult> RunProjectSetupScriptAsync(
        RunProjectSetupScriptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<ProjectSetupScriptResult>("RunProjectSetupScript", request, cancellationToken);
    }

    public Task<ProjectSetupScriptResult> RunProjectScriptAsync(
        RunProjectScriptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<ProjectSetupScriptResult>("RunProjectScript", request, cancellationToken);
    }

    public Task<ComposerDiscoveryResult> GetComposerDiscoveryAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<ComposerDiscoveryResult>("GetComposerDiscovery", threadId, cancellationToken);

    public async Task<IReadOnlyList<PromptStash>> ListPromptStashesAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default) =>
        await InvokeAsync<PromptStash[]>("ListPromptStashes", projectId, cancellationToken).ConfigureAwait(false);

    public Task<PromptStash> SavePromptStashAsync(
        SavePromptStashRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<PromptStash>("SavePromptStash", request, cancellationToken);
    }

    public Task DeletePromptStashAsync(
        DeletePromptStashRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConnected();
        return _supervisor.Connection.InvokeAsync("DeletePromptStash", request, cancellationToken);
    }

    public Task<SourceControlRepository> DetectSourceControlAsync(
        DetectSourceControlRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<SourceControlRepository>("DetectSourceControl", request, cancellationToken);

    public Task<ListPullRequestsResult> ListPullRequestsAsync(
        ListPullRequestsRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<ListPullRequestsResult>("ListPullRequests", request, cancellationToken);

    public Task<SourceControlOperationResult> CloneHostedRepositoryAsync(
        CloneHostedRepositoryRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeHostingAsync("CloneHostedRepository", request.OperationId, id => request with { OperationId = id }, cancellationToken);

    public Task<SourceControlOperationResult> PublishHostedRepositoryAsync(
        PublishHostedRepositoryRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeHostingAsync("PublishHostedRepository", request.OperationId, id => request with { OperationId = id }, cancellationToken);

    public Task<SourceControlOperationResult> CreatePullRequestAsync(
        CreatePullRequestRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeHostingAsync("CreatePullRequest", request.OperationId, id => request with { OperationId = id }, cancellationToken);

    public Task<SourceControlOperationResult> MutatePullRequestAsync(
        MutatePullRequestRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeHostingAsync("MutatePullRequest", request.OperationId, id => request with { OperationId = id }, cancellationToken);

    public async Task<IReadOnlyList<HostingOperation>> ListHostingOperationsAsync(CancellationToken cancellationToken = default) =>
        await InvokeAsync<HostingOperation[]>("ListHostingOperations", cancellationToken).ConfigureAwait(false);

    private async Task<SourceControlOperationResult> InvokeHostingAsync<T>(string method, CommandId? operationId,
        Func<CommandId, T> createRequest, CancellationToken cancellationToken)
    {
        var id = operationId ?? CommandId.New();
        try { return await InvokeAsync<SourceControlOperationResult>(method, createRequest(id)!, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (CouldBeInterruptedDispatch(exception))
        {
            try
            {
                var operation = (await ListHostingOperationsAsync(CancellationToken.None).ConfigureAwait(false))
                    .FirstOrDefault(candidate => candidate.OperationId == id);
                if (operation?.Result is { } result) return result;
            }
            catch (Exception recoveryException) when (recoveryException is not OutOfMemoryException) { }
            return new SourceControlOperationResult(false,
                $"Connection interrupted during hosting operation {id.Value}. Reconnect and refresh operation history before retrying.",
                OperationId: id, State: CommandReceiptState.DispatchUncertain);
        }
    }

    public Task<GeneratedSourceControlText> GenerateSourceControlTextAsync(
        GenerateSourceControlTextRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<GeneratedSourceControlText>("GenerateSourceControlText", request, cancellationToken);

    public Task<DiagnosticsSnapshot> GetDiagnosticsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<DiagnosticsSnapshot>("GetDiagnostics", cancellationToken);

    public Task<PiRuntimeSetupResult> ConfigurePiRuntimeAsync(ConfigurePiRuntimeRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<PiRuntimeSetupResult>("ConfigurePiRuntime", request, cancellationToken);

    public Task<ExportDiagnosticsResult> ExportDiagnosticsAsync(
        ExportDiagnosticsRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<ExportDiagnosticsResult>("ExportDiagnostics", request, cancellationToken);

    public Task<SearchProjectFilesResult> SearchProjectFilesAsync(
        SearchProjectFilesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<SearchProjectFilesResult>("SearchProjectFiles", request, cancellationToken);
    }

    public Task<ListProjectEntriesResult> ListProjectEntriesAsync(
        ListProjectEntriesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<ListProjectEntriesResult>("ListProjectEntries", request, cancellationToken);
    }

    public Task<SearchProjectContentsResult> SearchProjectContentsAsync(
        SearchProjectContentsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<SearchProjectContentsResult>("SearchProjectContents", request, cancellationToken);
    }

    public Task<ReadProjectFileResult> ReadProjectFileAsync(
        ReadProjectFileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<ReadProjectFileResult>("ReadProjectFile", request, cancellationToken);
    }

    public Task<ReadProjectFileAssetResult> ReadProjectFileAssetAsync(
        ReadProjectFileAssetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<ReadProjectFileAssetResult>("ReadProjectFileAsset", request, cancellationToken);
    }

    public Task<SaveProjectFileResult> SaveProjectFileAsync(
        SaveProjectFileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<SaveProjectFileResult>("SaveProjectFile", request, cancellationToken);
    }

    public Task<OpenProjectFileInEditorResult> OpenProjectFileInEditorAsync(
        OpenProjectFileInEditorRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<OpenProjectFileInEditorResult>("OpenProjectFileInEditor", request, cancellationToken);
    }

    public Task<GetProjectChangesResult> GetProjectChangesAsync(
        GetProjectChangesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<GetProjectChangesResult>("GetProjectChanges", request, cancellationToken);
    }

    public Task<GetProjectChangeDiffResult> GetProjectChangeDiffAsync(
        GetProjectChangeDiffRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<GetProjectChangeDiffResult>("GetProjectChangeDiff", request, cancellationToken);
    }

    public Task<ListGitRefsResult> ListGitRefsAsync(
        ListGitRefsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<ListGitRefsResult>("ListGitRefs", request, cancellationToken);
    }

    public Task<ListGitWorktreesResult> ListGitWorktreesAsync(
        ListGitWorktreesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<ListGitWorktreesResult>("ListGitWorktrees", request, cancellationToken);
    }

    public async Task<ExecuteWorkspaceGitCommandResult> ExecuteWorkspaceGitCommandAsync(
        WorkspaceTarget target,
        WorkspaceGitCommand command,
        string? expectedHeadSha = null,
        string? expectedBranchName = null,
        string? expectedStatusToken = null,
        CommandId? commandId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(command);
        var descriptor = EnsureConnected();
        var resolvedCommandId = commandId ?? CommandId.New();
        var request = new ExecuteWorkspaceGitCommandRequest(
            ProtocolVersion.Current,
            descriptor.EnvironmentId,
            _options.ClientId,
            resolvedCommandId,
            target,
            command,
            expectedHeadSha,
            expectedBranchName,
            expectedStatusToken);
        try
        {
            return await InvokeAsync<ExecuteWorkspaceGitCommandResult>(
                "ExecuteWorkspaceGitCommand",
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (CouldBeInterruptedDispatch(exception))
        {
            var resolved = await TryResolveInterruptedWorkspaceCommandAsync(resolvedCommandId).ConfigureAwait(false);
            if (resolved?.Receipt.State is CommandReceiptState.Completed or
                                           CommandReceiptState.Rejected or
                                           CommandReceiptState.Failed or
                                           CommandReceiptState.DispatchUncertain)
            {
                return resolved;
            }

            throw new WorkspaceCommandDispatchUncertainException(
                resolvedCommandId,
                target.ProjectId,
                target.ThreadId,
                "The connection ended before Git command dispatch could be confirmed. Reconnect and check its receipt; do not resend automatically.",
                exception);
        }
    }

    public Task<ExecuteWorkspaceGitCommandResult?> GetWorkspaceGitCommandResultAsync(
        CommandId commandId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<ExecuteWorkspaceGitCommandResult?>(
            "GetWorkspaceGitCommandResult",
            _options.ClientId,
            commandId,
            cancellationToken);

    public Task<GetThreadCheckpointDiffResult> GetThreadCheckpointDiffAsync(
        GetThreadCheckpointDiffRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<GetThreadCheckpointDiffResult>(
            "GetThreadCheckpointDiff",
            request,
            cancellationToken);
    }

    public Task<DiscoverProjectPreviewServersResult> DiscoverProjectPreviewServersAsync(
        DiscoverProjectPreviewServersRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<DiscoverProjectPreviewServersResult>(
            "DiscoverProjectPreviewServers",
            request,
            cancellationToken);
    }

    public Task<TerminalSessionDescriptor> StartTerminalSessionAsync(
        StartTerminalSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<TerminalSessionDescriptor>("StartTerminalSession", request, cancellationToken);
    }

    public async Task<IReadOnlyList<TerminalSessionDescriptor>> ListTerminalSessionsAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default) =>
        await InvokeAsync<TerminalSessionDescriptor[]>(
            "ListTerminalSessions",
            projectId,
            cancellationToken).ConfigureAwait(false);

    public Task WriteTerminalInputAsync(
        WriteTerminalInputRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConnected();
        return _supervisor.Connection.InvokeAsync("WriteTerminalInput", request, cancellationToken);
    }

    public Task<TerminalSessionDescriptor> ResizeTerminalSessionAsync(
        ResizeTerminalSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<TerminalSessionDescriptor>("ResizeTerminalSession", request, cancellationToken);
    }

    public Task<TerminalSessionDescriptor> StopTerminalSessionAsync(
        StopTerminalSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<TerminalSessionDescriptor>("StopTerminalSession", request, cancellationToken);
    }

    public Task CloseTerminalSessionAsync(
        CloseTerminalSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConnected();
        return _supervisor.Connection.InvokeAsync("CloseTerminalSession", request, cancellationToken);
    }

    public async Task<IReadOnlyList<ThreadDescriptor>> ListThreadsAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        var descriptor = EnsureConnected();
        var threads = await _supervisor.Connection.InvokeAsync<ThreadDescriptor[]>(
            "ListThreads",
            projectId,
            cancellationToken).ConfigureAwait(false);
        ApplyThreadProjectSnapshot(
            descriptor,
            projectId,
            threads,
            includeArchived: false,
            isComplete: true);
        return ThreadMetadata.GetProjectThreads(projectId);
    }

    public async Task<ThreadDescriptor> GetThreadAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default)
    {
        var descriptor = EnsureConnected();
        var thread = await _supervisor.Connection.InvokeAsync<ThreadDescriptor>(
            "GetThread",
            threadId,
            cancellationToken).ConfigureAwait(false);
        ApplyThreadDescriptor(descriptor, thread, expectedThreadId: threadId);
        return ThreadMetadata.GetCurrent(threadId) ?? thread;
    }

    public async Task<SearchThreadsResult> SearchThreadsAsync(
        SearchThreadsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = EnsureConnected();
        SearchThreadsResult result;
        try
        {
            result = await _supervisor.Connection.InvokeAsync<SearchThreadsResult>(
                "SearchThreads",
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HubException exception)
        {
            var mapped = CreateThreadSearchException(exception.Message, request.ProjectId, exception);
            if (mapped is not null)
            {
                throw mapped;
            }

            throw;
        }

        ApplyThreadProjectSnapshot(
            descriptor,
            request.ProjectId,
            result.Threads,
            request.IncludeArchived,
            isComplete: string.IsNullOrWhiteSpace(request.Query) && !result.IsTruncated);
        return new SearchThreadsResult(
            result.Threads
                .Select(thread => ThreadMetadata.GetCurrent(thread.ThreadId) ?? thread)
                .ToArray(),
            result.IsTruncated);
    }

    public Task<GlobalSearchResult> SearchGlobalAsync(
        GlobalSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<GlobalSearchResult>("SearchGlobal", request, cancellationToken);
    }

    public async Task<ThreadDescriptor> CreateThreadAsync(
        CreateThreadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = EnsureConnected();
        var thread = await _supervisor.Connection.InvokeAsync<ThreadDescriptor>(
            "CreateThread",
            request,
            cancellationToken).ConfigureAwait(false);
        ApplyThreadDescriptor(descriptor, thread, request.ProjectId);
        return ThreadMetadata.GetCurrent(thread.ThreadId) ?? thread;
    }

    public async Task DeleteThreadAsync(
        DeleteThreadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConnected();
        await _supervisor.Connection.InvokeAsync("DeleteThread", request, cancellationToken).ConfigureAwait(false);
        ThreadMetadata.Remove(request.ThreadId);
    }

    public async Task<ApplyThreadBulkOperationResult> ApplyThreadBulkOperationAsync(
        ApplyThreadBulkOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = EnsureConnected();
        var result = await _supervisor.Connection.InvokeAsync<ApplyThreadBulkOperationResult>(
            "ApplyThreadBulkOperation", request, cancellationToken).ConfigureAwait(false);
        ApplyThreadProjectSnapshot(descriptor, request.ProjectId, result.Threads, includeArchived: false, isComplete: true);
        return result with { Threads = ThreadMetadata.GetProjectThreads(request.ProjectId) };
    }

    public async Task<IReadOnlyList<ThreadDescriptor>> SetThreadPinnedOrderAsync(
        SetThreadPinnedOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = EnsureConnected();
        var threads = await _supervisor.Connection.InvokeAsync<ThreadDescriptor[]>(
            "SetThreadPinnedOrder", request, cancellationToken).ConfigureAwait(false);
        ApplyThreadProjectSnapshot(descriptor, request.ProjectId, threads, includeArchived: false, isComplete: true);
        return ThreadMetadata.GetProjectThreads(request.ProjectId);
    }

    public async Task<ThreadDescriptor> LinkThreadPullRequestAsync(
        LinkThreadPullRequestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = EnsureConnected();
        var thread = await _supervisor.Connection.InvokeAsync<ThreadDescriptor>(
            "LinkThreadPullRequest", request, cancellationToken).ConfigureAwait(false);
        ApplyThreadDescriptor(descriptor, thread, expectedThreadId: request.ThreadId);
        return ThreadMetadata.GetCurrent(request.ThreadId) ?? thread;
    }

    public Task<ThreadLifecycleUpdateResult> RenameThreadAsync(
        ThreadId threadId,
        long expectedRevision,
        string title,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        ArgumentNullException.ThrowIfNull(title);
        return ExecuteThreadLifecycleAsync(
            threadId,
            new ThreadRenameCommand(expectedRevision, title),
            cancellationToken);
    }

    public Task<ThreadLifecycleUpdateResult> SetThreadArchivedAsync(
        ThreadId threadId,
        long expectedRevision,
        bool isArchived,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        return ExecuteThreadLifecycleAsync(
            threadId,
            new ThreadSetArchivedCommand(expectedRevision, isArchived),
            cancellationToken);
    }

    public Task<ThreadLifecycleUpdateResult> SetThreadPinnedAsync(
        ThreadId threadId,
        long expectedRevision,
        bool isPinned,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        return ExecuteThreadLifecycleAsync(
            threadId,
            new ThreadSetPinnedCommand(expectedRevision, isPinned),
            cancellationToken);
    }

    public Task<ThreadLifecycleUpdateResult> SetThreadSettledAsync(
        ThreadId threadId,
        long expectedRevision,
        bool isSettled,
        CancellationToken cancellationToken = default) => ExecuteThreadLifecycleAsync(
        threadId,
        new ThreadSetSettledCommand(expectedRevision, isSettled),
        cancellationToken);

    public Task<ThreadLifecycleUpdateResult> SetThreadSnoozedAsync(
        ThreadId threadId,
        long expectedRevision,
        DateTimeOffset? snoozedUntilUtc,
        CancellationToken cancellationToken = default) => ExecuteThreadLifecycleAsync(
        threadId,
        new ThreadSetSnoozedCommand(expectedRevision, snoozedUntilUtc),
        cancellationToken);

    public Task<ThreadLifecycleUpdateResult> SetThreadPinnedOrderAsync(
        ThreadId threadId,
        long expectedRevision,
        long pinnedOrder,
        CancellationToken cancellationToken = default) => ExecuteThreadLifecycleAsync(
        threadId,
        new ThreadSetPinnedOrderCommand(expectedRevision, pinnedOrder),
        cancellationToken);

    public Task<ThreadLifecycleUpdateResult> RegenerateThreadTitleAsync(
        ThreadId threadId,
        long expectedRevision,
        CancellationToken cancellationToken = default) => ExecuteThreadLifecycleAsync(
        threadId,
        new ThreadRegenerateTitleCommand(expectedRevision),
        cancellationToken);

    public Task<ThreadDraft> GetThreadDraftAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<ThreadDraft>("GetThreadDraft", threadId, cancellationToken);

    public async Task<ThreadPiConfigurationSnapshot> GetThreadPiConfigurationAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default)
    {
        var descriptor = EnsureConnected();
        var snapshot = await _supervisor.Connection.InvokeAsync<ThreadPiConfigurationSnapshot>(
            "GetThreadPiConfiguration",
            threadId,
            cancellationToken).ConfigureAwait(false);
        ApplyPiConfigurationSnapshot(descriptor, threadId, snapshot);
        return PiConfigurations.GetCurrent(threadId) ?? snapshot;
    }

    public async Task<ThreadPiConfigurationUpdateResult> UpdateThreadPiConfigurationAsync(
        ThreadId threadId,
        long expectedRevision,
        PiModelSelection? model,
        PiThinkingLevel? thinkingLevel,
        string? runtimeModeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        var commandId = CommandId.New();
        CommandReceipt receipt;
        try
        {
            receipt = await ExecuteAsync(
                threadId,
                null,
                null,
                new ThreadUpdatePiConfigurationCommand(
                    expectedRevision,
                    model,
                    thinkingLevel,
                    runtimeModeId),
                cancellationToken,
                commandId).ConfigureAwait(false);
        }
        catch (HubException exception)
        {
            var configurationException = CreatePiConfigurationException(
                exception.Message,
                commandId,
                threadId,
                exception);
            if (configurationException is not null)
            {
                throw configurationException;
            }

            throw;
        }

        var rejected = CreatePiConfigurationException(
            receipt.ErrorCode,
            commandId,
            threadId);
        if (rejected is not null)
        {
            throw rejected;
        }

        var snapshot = receipt.State == CommandReceiptState.Completed
            ? await GetThreadPiConfigurationAsync(threadId, cancellationToken).ConfigureAwait(false)
            : null;
        return new ThreadPiConfigurationUpdateResult(receipt, snapshot);
    }

    private async Task<ThreadLifecycleUpdateResult> ExecuteThreadLifecycleAsync(
        ThreadId threadId,
        ThreadCommand command,
        CancellationToken cancellationToken)
    {
        var commandId = CommandId.New();
        CommandReceipt receipt;
        try
        {
            receipt = await ExecuteAsync(
                threadId,
                null,
                null,
                command,
                cancellationToken,
                commandId).ConfigureAwait(false);
        }
        catch (HubException exception)
        {
            var lifecycleException = CreateThreadLifecycleException(
                exception.Message,
                commandId,
                threadId,
                exception);
            if (lifecycleException is not null)
            {
                throw lifecycleException;
            }

            throw;
        }

        var rejected = CreateThreadLifecycleException(
            receipt.ErrorCode,
            commandId,
            threadId);
        if (rejected is not null)
        {
            throw rejected;
        }

        if (receipt.State == CommandReceiptState.DispatchUncertain)
        {
            throw new CommandDispatchUncertainException(
                commandId,
                threadId,
                "The host could not confirm the lifecycle command. Reconnect and reload thread metadata; " +
                "do not resend it automatically.");
        }

        var thread = receipt.State == CommandReceiptState.Completed
            ? await GetThreadAsync(threadId, cancellationToken).ConfigureAwait(false)
            : null;
        return new ThreadLifecycleUpdateResult(receipt, thread);
    }

    public Task<ThreadDraftSaveResult> SaveThreadDraftAsync(
        ThreadId threadId, DraftId draftId, long expectedRevision, string text,
        CancellationToken cancellationToken = default) =>
        SaveThreadDraftAsync(threadId, draftId, expectedRevision, text, null, cancellationToken);

    public async Task<ThreadDraft> RestorePromptStashAsync(ThreadId threadId, DraftId draftId, long expectedRevision, string stashId, CancellationToken cancellationToken = default)
    {
        var receipt = await ExecuteAsync(threadId, null, null,
            new ThreadRestoreStashCommand(stashId, draftId, expectedRevision), cancellationToken).ConfigureAwait(false);
        if (receipt.State != CommandReceiptState.Completed)
            throw new InvalidOperationException($"Prompt restore is {receipt.State}. Reload the draft before retrying.");
        return await GetThreadDraftAsync(threadId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ThreadDraftSaveResult> SaveThreadDraftAsync(
        ThreadId threadId,
        DraftId draftId,
        long expectedRevision,
        string text,
        IReadOnlyList<ComposerContext>? context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var receipt = await ExecuteAsync(
            threadId,
            null,
            null,
            new ThreadSaveDraftCommand(draftId, expectedRevision, text, context),
            cancellationToken).ConfigureAwait(false);
        var draft = receipt.State == CommandReceiptState.Completed
            ? await GetThreadDraftAsync(threadId, cancellationToken).ConfigureAwait(false)
            : null;
        return new ThreadDraftSaveResult(receipt, draft);
    }

    public async Task<DraftAttachmentUploadResult> UploadDraftAttachmentAsync(
        ThreadId threadId,
        DraftId draftId,
        long expectedRevision,
        string fileName,
        string? mediaType,
        Stream content,
        long byteLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);
        var descriptor = EnsureConnected();
        var commandId = CommandId.New();
        var attachmentId = AttachmentId.New();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"threads/{Uri.EscapeDataString(threadId.Value)}/draft-attachments" +
            $"?fileName={Uri.EscapeDataString(fileName)}");
        request.Headers.Add("X-PiStation-Protocol-Version", ProtocolVersion.Current.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-PiStation-Environment-Id", descriptor.EnvironmentId.Value);
        request.Headers.Add("X-PiStation-Client-Id", _options.ClientId.Value);
        request.Headers.Add("X-PiStation-Command-Id", commandId.Value);
        request.Headers.Add("X-PiStation-Draft-Id", draftId.Value);
        request.Headers.Add("X-PiStation-Attachment-Id", attachmentId.Value);
        request.Headers.Add("X-PiStation-Draft-Revision", expectedRevision.ToString(CultureInfo.InvariantCulture));
        request.Content = new StreamContent(new LeaveOpenStream(content));
        request.Content.Headers.ContentLength = byteLength;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType);
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                ProtocolError? error = null;
                try
                {
                    error = await DeserializeAsync(
                        response,
                        ProtocolJsonContext.Default.ProtocolError,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                }

                error ??= new ProtocolError(
                    "AttachmentUploadFailed",
                    $"The attachment upload failed with HTTP status {(int)response.StatusCode}.");
                throw new AttachmentUploadException(response.StatusCode, error);
            }

            return await DeserializeAsync(
                    response,
                    ProtocolJsonContext.Default.DraftAttachmentUploadResult,
                    cancellationToken).ConfigureAwait(false) ??
                throw new InvalidDataException("The attachment upload returned an empty response.");
        }
        catch (Exception exception) when (CouldBeInterruptedDispatch(exception))
        {
            var receipt = await TryResolveInterruptedCommandAsync(commandId).ConfigureAwait(false);
            if (receipt is not null && receipt.State is CommandReceiptState.Completed or
                                                      CommandReceiptState.Rejected or
                                                      CommandReceiptState.Failed or
                                                      CommandReceiptState.DispatchUncertain)
            {
                var draft = await GetThreadDraftAsync(threadId, CancellationToken.None).ConfigureAwait(false);
                return new DraftAttachmentUploadResult(
                    receipt,
                    draft,
                    draft.Attachments.SingleOrDefault(item => item.AttachmentId == attachmentId));
            }

            throw new CommandDispatchUncertainException(
                commandId,
                threadId,
                "The connection ended before attachment storage could be confirmed. Reconnect and reload the draft; do not upload it again automatically.",
                exception);
        }
    }

    public async Task<DraftAttachmentRemoveResult> RemoveDraftAttachmentAsync(
        ThreadId threadId,
        DraftId draftId,
        AttachmentId attachmentId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var receipt = await ExecuteAsync(
            threadId,
            null,
            null,
            new ThreadRemoveDraftAttachmentCommand(draftId, attachmentId, expectedRevision),
            cancellationToken).ConfigureAwait(false);
        var draft = receipt.State == CommandReceiptState.Completed
            ? await GetThreadDraftAsync(threadId, cancellationToken).ConfigureAwait(false)
            : null;
        return new DraftAttachmentRemoveResult(receipt, draft);
    }

    public async Task<ThreadDraftClearResult> ClearThreadDraftAsync(
        ThreadId threadId,
        DraftId draftId,
        long expectedRevision,
        IReadOnlyList<AttachmentId> expectedAttachmentIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedAttachmentIds);
        var receipt = await ExecuteAsync(
            threadId,
            null,
            null,
            new ThreadClearDraftCommand(draftId, expectedRevision, expectedAttachmentIds),
            cancellationToken).ConfigureAwait(false);
        var draft = receipt.State == CommandReceiptState.Completed
            ? await GetThreadDraftAsync(threadId, cancellationToken).ConfigureAwait(false)
            : null;
        return new ThreadDraftClearResult(receipt, draft);
    }

    public Task<CommandReceipt> StartTurnAsync(
        ThreadId threadId,
        string prompt,
        ProjectionEpoch? expectedProjectionEpoch = null,
        DraftId? draftId = null,
        long? draftRevision = null,
        IReadOnlyList<AttachmentId>? attachmentIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        attachmentIds ??= [];
        if (string.IsNullOrWhiteSpace(prompt) && attachmentIds.Count == 0)
        {
            throw new ArgumentException("A turn must contain text or at least one attachment.", nameof(prompt));
        }

        return ExecuteAsync(
            threadId,
            expectedProjectionEpoch,
            null,
            new ThreadStartTurnCommand(prompt, draftId, draftRevision, attachmentIds),
            cancellationToken);
    }

    public Task<CommandReceipt> StopTurnAsync(
        ThreadId threadId,
        ProjectionEpoch? expectedProjectionEpoch = null,
        TurnId? expectedTurnId = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            threadId,
            expectedProjectionEpoch,
            expectedTurnId,
            new ThreadStopTurnCommand(),
            cancellationToken);

    public Task<CommandReceipt> QueueSteeringAsync(
        ThreadId threadId,
        string prompt,
        ProjectionEpoch? expectedProjectionEpoch = null,
        TurnId? expectedTurnId = null,
        DraftId? draftId = null,
        long? draftRevision = null,
        IReadOnlyList<AttachmentId>? attachmentIds = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(
        threadId,
        expectedProjectionEpoch,
        expectedTurnId,
        new ThreadQueueSteeringCommand(prompt, draftId, draftRevision, attachmentIds),
        cancellationToken);

    public Task<CommandReceipt> QueueFollowUpAsync(
        ThreadId threadId,
        string prompt,
        ProjectionEpoch? expectedProjectionEpoch = null,
        TurnId? expectedTurnId = null,
        DraftId? draftId = null,
        long? draftRevision = null,
        IReadOnlyList<AttachmentId>? attachmentIds = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(
        threadId,
        expectedProjectionEpoch,
        expectedTurnId,
        new ThreadQueueFollowUpCommand(prompt, draftId, draftRevision, attachmentIds),
        cancellationToken);

    public Task<CommandReceipt> ClearTurnQueueAsync(
        ThreadId threadId,
        ProjectionEpoch? expectedProjectionEpoch = null,
        TurnId? expectedTurnId = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(
        threadId,
        expectedProjectionEpoch,
        expectedTurnId,
        new ThreadClearQueueCommand(),
        cancellationToken);

    public Task<CommandReceipt> RefreshTurnQueueAsync(
        ThreadId threadId,
        ProjectionEpoch? expectedProjectionEpoch = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(
        threadId,
        expectedProjectionEpoch,
        null,
        new ThreadRefreshQueueCommand(),
        cancellationToken);

    public Task<CommandReceipt> SetQueueDeliveryModeAsync(
        ThreadId threadId,
        QueuedMessageKind kind,
        QueueDeliveryMode mode,
        ProjectionEpoch? expectedProjectionEpoch = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(
        threadId,
        expectedProjectionEpoch,
        null,
        new ThreadSetQueueDeliveryModeCommand(kind, mode),
        cancellationToken);

    public Task<CommandReceipt> CompactThreadContextAsync(
        ThreadId threadId,
        string? customInstructions = null,
        ProjectionEpoch? expectedProjectionEpoch = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(
        threadId,
        expectedProjectionEpoch,
        null,
        new ThreadCompactContextCommand(customInstructions),
        cancellationToken);

    public Task<CommandReceipt> InterruptAgentAsync(
        ThreadId threadId,
        string activityId,
        ProjectionEpoch? expectedProjectionEpoch = null,
        TurnId? expectedTurnId = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(
        threadId,
        expectedProjectionEpoch,
        expectedTurnId,
        new ThreadInterruptAgentCommand(activityId),
        cancellationToken);

    public Task<CommandReceipt> RestartThreadAsync(
        ThreadId threadId,
        ProjectionEpoch? expectedProjectionEpoch = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            threadId,
            expectedProjectionEpoch,
            null,
            new ThreadRestartRuntimeCommand(),
            cancellationToken);

    public Task<CommandReceipt> RevertThreadToCheckpointAsync(
        ThreadId threadId,
        int turnCount,
        ProjectionEpoch? expectedProjectionEpoch = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            threadId,
            expectedProjectionEpoch,
            null,
            new ThreadRevertCheckpointCommand(turnCount),
            cancellationToken);

    public Task<CommandReceipt> RespondToApprovalAsync(
        ThreadId threadId,
        InteractionId interactionId,
        ApprovalDecision decision,
        ProjectionEpoch? expectedProjectionEpoch = null,
        TurnId? expectedTurnId = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(
        threadId,
        expectedProjectionEpoch,
        expectedTurnId,
        new ThreadRespondToApprovalCommand(interactionId, decision),
        cancellationToken);

    public Task<CommandReceipt> AnswerQuestionAsync(
        ThreadId threadId,
        InteractionId interactionId,
        string answer,
        ProjectionEpoch? expectedProjectionEpoch = null,
        TurnId? expectedTurnId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return ExecuteAsync(
            threadId,
            expectedProjectionEpoch,
            expectedTurnId,
            new ThreadAnswerQuestionCommand(interactionId, answer),
            cancellationToken);
    }

    public Task<CommandReceipt> CancelInteractionAsync(
        ThreadId threadId,
        InteractionId interactionId,
        ProjectionEpoch? expectedProjectionEpoch = null,
        TurnId? expectedTurnId = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(
        threadId,
        expectedProjectionEpoch,
        expectedTurnId,
        new ThreadCancelInteractionCommand(interactionId),
        cancellationToken);

    public Task<CommandReceipt?> GetCommandReceiptAsync(
        CommandId commandId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<CommandReceipt?>("GetCommandReceipt", _options.ClientId, commandId, cancellationToken);

    public ThreadSubscription SubscribeThread(ThreadId threadId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnected();
        return _subscriptions.GetOrAdd(threadId, id => new ThreadSubscription(_supervisor, id));
    }

    public TerminalSubscription SubscribeTerminal(TerminalSessionId terminalSessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnected();
        return _terminalSubscriptions.GetOrAdd(
            terminalSessionId,
            id => new TerminalSubscription(
                _supervisor,
                id,
                subscription => RemoveTerminalSubscription(id, subscription)));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _supervisor.StateChanged -= OnStateChanged;
        _supervisor.Reconnected -= OnReconnected;
        foreach (var subscription in _subscriptions.Values)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }

        _subscriptions.Clear();
        foreach (var subscription in _terminalSubscriptions.Values)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }

        _terminalSubscriptions.Clear();
        _httpClient.Dispose();
        await _supervisor.DisposeAsync().ConfigureAwait(false);
        _synchronizationGate.Dispose();
    }

    private async Task<CommandReceipt> ExecuteAsync(
        ThreadId threadId,
        ProjectionEpoch? expectedProjectionEpoch,
        TurnId? expectedTurnId,
        ThreadCommand command,
        CancellationToken cancellationToken,
        CommandId? commandId = null)
    {
        var descriptor = EnsureConnected();
        var resolvedCommandId = commandId ?? CommandId.New();
        var request = new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            descriptor.EnvironmentId,
            _options.ClientId,
            resolvedCommandId,
            threadId,
            expectedProjectionEpoch,
            expectedTurnId,
            command);
        try
        {
            return await InvokeAsync<CommandReceipt>(
                "ExecuteThreadCommand",
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (CouldBeInterruptedDispatch(exception))
        {
            var resolved = await TryResolveInterruptedCommandAsync(resolvedCommandId).ConfigureAwait(false);
            if (resolved is not null && resolved.State is CommandReceiptState.Completed or
                                                       CommandReceiptState.Rejected or
                                                       CommandReceiptState.Failed or
                                                       CommandReceiptState.DispatchUncertain)
            {
                return resolved;
            }

            throw new CommandDispatchUncertainException(
                resolvedCommandId,
                threadId,
                "The connection ended before command dispatch could be confirmed. Reconnect and check its receipt; do not resend automatically.",
                exception);
        }
    }

    private bool CouldBeInterruptedDispatch(Exception exception) =>
        exception is OperationCanceledException ||
        exception is HttpRequestException ||
        exception is IOException ||
        exception is JsonException ||
        _supervisor.Connection.State != HubConnectionState.Connected ||
        exception is HubException &&
        exception.Message.Contains("connection closed", StringComparison.OrdinalIgnoreCase);

    private async Task<CommandReceipt?> TryResolveInterruptedCommandAsync(CommandId commandId)
    {
        if (_supervisor.Connection.State != HubConnectionState.Connected)
        {
            return null;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            return await _supervisor.Connection.InvokeAsync<CommandReceipt?>(
                "GetCommandReceipt",
                _options.ClientId,
                commandId,
                timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task<ExecuteWorkspaceGitCommandResult?> TryResolveInterruptedWorkspaceCommandAsync(
        CommandId commandId)
    {
        if (_supervisor.Connection.State != HubConnectionState.Connected)
        {
            return null;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            return await _supervisor.Connection.InvokeAsync<ExecuteWorkspaceGitCommandResult?>(
                "GetWorkspaceGitCommandResult",
                _options.ClientId,
                commandId,
                timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        await _synchronizationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _supervisor.MarkSynchronizing();
            var descriptor = await _supervisor.Connection.InvokeAsync<EnvironmentDescriptor>(
                "GetEnvironmentDescriptor",
                cancellationToken).ConfigureAwait(false);
            if (ProtocolVersion.Current < descriptor.MinimumProtocolVersion ||
                ProtocolVersion.Current > descriptor.MaximumProtocolVersion)
            {
                var exception = new InvalidOperationException(
                    $"Protocol {ProtocolVersion.Current} is outside the environment range " +
                    $"{descriptor.MinimumProtocolVersion}-{descriptor.MaximumProtocolVersion}.");
                _supervisor.MarkIncompatible(exception);
                throw exception;
            }

            if (Descriptor is not null && Descriptor.EnvironmentId != descriptor.EnvironmentId)
            {
                PiConfigurations.Clear();
                ThreadMetadata.Clear();
            }

            Descriptor = descriptor;
            foreach (var projectId in ThreadMetadata.TrackedProjectIds)
            {
                try
                {
                    var request = new SearchThreadsRequest(
                        projectId,
                        string.Empty,
                        IncludeArchived: true,
                        Limit: ThreadLifecycleDefaults.MaximumSearchLimit);
                    var result = await _supervisor.Connection.InvokeAsync<SearchThreadsResult>(
                        "SearchThreads",
                        request,
                        cancellationToken).ConfigureAwait(false);
                    ApplyThreadProjectSnapshot(
                        descriptor,
                        projectId,
                        result.Threads,
                        includeArchived: true,
                        isComplete: !result.IsTruncated);
                }
                catch (HubException exception) when (
                    exception.Message.Contains(ProtocolErrorCodes.ProjectNotFound, StringComparison.Ordinal))
                {
                    ThreadMetadata.RemoveProject(projectId);
                }
                catch (HubException)
                {
                    // A domain failure for one cached project must not block transport recovery.
                }
            }

            foreach (var threadId in PiConfigurations.TrackedThreadIds)
            {
                try
                {
                    var snapshot = await _supervisor.Connection
                        .InvokeAsync<ThreadPiConfigurationSnapshot>(
                            "GetThreadPiConfiguration",
                            threadId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    ApplyPiConfigurationSnapshot(descriptor, threadId, snapshot);
                }
                catch (HubException exception) when (
                    exception.Message.Contains(
                        ProtocolErrorCodes.ThreadNotFound,
                        StringComparison.Ordinal))
                {
                    PiConfigurations.Remove(threadId);
                }
                catch (HubException)
                {
                    // A domain failure for one cached thread must not block transport recovery.
                }
            }

            _supervisor.MarkConnected();
        }
        finally
        {
            _synchronizationGate.Release();
        }
    }

    private EnvironmentDescriptor EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ConnectionState == EnvironmentConnectionState.Connected && Descriptor is not null
            ? Descriptor
            : throw new EnvironmentConnectionException(ConnectionState);
    }

    private Task<T> InvokeAsync<T>(string method, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return _supervisor.Connection.InvokeAsync<T>(method, cancellationToken);
    }

    private Task<T> InvokeAsync<T>(string method, object argument, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return _supervisor.Connection.InvokeAsync<T>(method, argument, cancellationToken);
    }

    private Task<T> InvokeAsync<T>(
        string method,
        object firstArgument,
        object secondArgument,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        return _supervisor.Connection.InvokeAsync<T>(
            method,
            firstArgument,
            secondArgument,
            cancellationToken);
    }

    private void OnStateChanged(object? sender, ConnectionStateChangedEventArgs args) =>
        ConnectionStateChanged?.Invoke(this, args);

    private void OnReconnected(object? sender, EventArgs args) => _ = ResynchronizeAfterReconnectAsync();

    private void ApplyPiConfigurationSnapshot(
        EnvironmentDescriptor descriptor,
        ThreadId requestedThreadId,
        ThreadPiConfigurationSnapshot snapshot)
    {
        if (snapshot.Configuration.EnvironmentId != descriptor.EnvironmentId)
        {
            throw new InvalidDataException(
                "The Pi configuration snapshot belongs to another environment.");
        }

        if (snapshot.Configuration.ThreadId != requestedThreadId)
        {
            throw new InvalidDataException(
                "The Pi configuration snapshot belongs to another thread.");
        }

        PiConfigurations.Apply(snapshot);
    }

    private void ApplyThreadProjectSnapshot(
        EnvironmentDescriptor descriptor,
        ProjectId projectId,
        IReadOnlyList<ThreadDescriptor> threads,
        bool includeArchived,
        bool isComplete)
    {
        foreach (var thread in threads)
        {
            ValidateThreadDescriptor(descriptor, thread, projectId, null);
        }

        ThreadMetadata.ApplyProjectSnapshot(projectId, threads, includeArchived, isComplete);
    }

    private void ApplyThreadDescriptor(
        EnvironmentDescriptor descriptor,
        ThreadDescriptor thread,
        ProjectId? expectedProjectId = null,
        ThreadId? expectedThreadId = null)
    {
        ValidateThreadDescriptor(descriptor, thread, expectedProjectId, expectedThreadId);
        ThreadMetadata.Apply(thread);
    }

    private static void ValidateThreadDescriptor(
        EnvironmentDescriptor descriptor,
        ThreadDescriptor thread,
        ProjectId? expectedProjectId,
        ThreadId? expectedThreadId)
    {
        if (thread.EnvironmentId != descriptor.EnvironmentId)
        {
            throw new InvalidDataException("The thread metadata belongs to another environment.");
        }

        if (expectedProjectId is not null && thread.ProjectId != expectedProjectId)
        {
            throw new InvalidDataException("The thread metadata belongs to another project.");
        }

        if (expectedThreadId is not null && thread.ThreadId != expectedThreadId)
        {
            throw new InvalidDataException("The thread metadata belongs to another thread.");
        }
    }

    private static ThreadLifecycleException? CreateThreadLifecycleException(
        string? error,
        CommandId commandId,
        ThreadId threadId,
        Exception? innerException = null)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        if (error.Contains(ProtocolErrorCodes.ThreadConflict, StringComparison.Ordinal))
        {
            return new ThreadLifecycleConflictException(
                ProtocolErrorCodes.ThreadConflict,
                commandId,
                threadId,
                error,
                innerException);
        }

        if (error.Contains(ProtocolErrorCodes.ThreadInvalid, StringComparison.Ordinal))
        {
            return new ThreadLifecycleInvalidException(
                ProtocolErrorCodes.ThreadInvalid,
                commandId,
                threadId,
                error,
                innerException);
        }

        return error.Contains(ProtocolErrorCodes.ThreadNotFound, StringComparison.Ordinal)
            ? new ThreadLifecycleNotFoundException(
                ProtocolErrorCodes.ThreadNotFound,
                commandId,
                threadId,
                error,
                innerException)
            : null;
    }

    private static ThreadSearchException? CreateThreadSearchException(
        string? error,
        ProjectId projectId,
        Exception? innerException = null)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        if (error.Contains(ProtocolErrorCodes.ThreadSearchInvalid, StringComparison.Ordinal))
        {
            return new ThreadSearchException(
                ProtocolErrorCodes.ThreadSearchInvalid,
                projectId,
                error,
                innerException);
        }

        return error.Contains(ProtocolErrorCodes.ProjectNotFound, StringComparison.Ordinal)
            ? new ThreadSearchException(
                ProtocolErrorCodes.ProjectNotFound,
                projectId,
                error,
                innerException)
            : null;
    }

    private static PiConfigurationException? CreatePiConfigurationException(
        string? error,
        CommandId commandId,
        ThreadId threadId,
        Exception? innerException = null)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        if (error.Contains(ProtocolErrorCodes.PiConfigurationConflict, StringComparison.Ordinal))
        {
            return new PiConfigurationConflictException(
                ProtocolErrorCodes.PiConfigurationConflict,
                commandId,
                threadId,
                error,
                innerException);
        }

        if (error.Contains(ProtocolErrorCodes.PiConfigurationUnsupported, StringComparison.Ordinal))
        {
            return new PiConfigurationUnsupportedException(
                ProtocolErrorCodes.PiConfigurationUnsupported,
                commandId,
                threadId,
                error,
                innerException);
        }

        return error.Contains(ProtocolErrorCodes.PiConfigurationInvalid, StringComparison.Ordinal)
            ? new PiConfigurationInvalidException(
                ProtocolErrorCodes.PiConfigurationInvalid,
                commandId,
                threadId,
                error,
                innerException)
            : null;
    }

    private static async Task<T?> DeserializeAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task ResynchronizeAfterReconnectAsync()
    {
        try
        {
            await SynchronizeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The supervisor publishes the terminal state. Callers can explicitly reconnect.
        }
    }

    private void RemoveTerminalSubscription(
        TerminalSessionId terminalSessionId,
        TerminalSubscription subscription)
    {
        if (_terminalSubscriptions.TryGetValue(terminalSessionId, out var current) &&
            ReferenceEquals(current, subscription))
        {
            _terminalSubscriptions.TryRemove(terminalSessionId, out _);
        }
    }

    private sealed class LeaveOpenStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) => base.Dispose(disposing);
        public override ValueTask DisposeAsync() => base.DisposeAsync();
    }
}
