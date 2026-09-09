using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiStation.Host.Attachments;
using PiStation.Host.Diagnostics;
using PiStation.Host.Errors;
using PiStation.Host.Files;
using PiStation.Host.Git;
using PiStation.Host.Persistence;
using PiStation.Host.Preview;
using PiStation.Host.Projects;
using PiStation.Host.Search;
using PiStation.Host.SourceControl;
using PiStation.Host.Threads;
using PiStation.Host.Terminals;
using PiStation.Host.Workspaces;
using PiStation.PiRpc.Diagnostics;
using PiStation.PiRpc.Transport;
using PiStation.Protocol;
using PiStation.Protocol.Commands;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Serialization;
using PiStation.Protocol.Streaming;

namespace PiStation.Host;

public sealed partial class EnvironmentService : IAsyncDisposable
{
    private readonly HostDatabase _database;
    private readonly DraftAttachmentStorage _attachmentStorage;
    private readonly HostEnvironmentRecord _environment;
    private readonly WorkspaceFileSearchService _fileSearch;
    private readonly WorkspaceFileReadService _fileRead;
    private readonly WorkspaceGitService _git;
    private readonly WorkspaceGitCommandService _gitCommands;
    private readonly WorkspaceCheckpointService _checkpoints;
    private readonly HostOptions _options;
    private readonly PreviewDiscoveryService _preview;
    private readonly ProjectService _projects;
    private readonly ProjectSetupScriptRunner _setupScripts;
    private readonly GlobalSearchService _search;
    private readonly SourceControlHostingService _sourceControl;
    private readonly HostDiagnosticsService _diagnostics;
    private readonly PiThreadRegistry _threads;
    private readonly TerminalSessionRegistry _terminals;
    private readonly CancellationTokenSource _catalogStopping = new();
    private readonly object _catalogGate = new();
    private readonly HashSet<TaskCompletionSource> _catalogReaders = [];
    private bool _catalogDisposed;

    private EnvironmentService(
        HostOptions options,
        HostDatabase database,
        DraftAttachmentStorage attachmentStorage,
        WorkspaceFileSearchService fileSearch,
        WorkspaceFileReadService fileRead,
        WorkspaceGitService git,
        WorkspaceGitCommandService gitCommands,
        WorkspaceCheckpointService checkpoints,
        PreviewDiscoveryService preview,
        HostEnvironmentRecord environment,
        ProjectService projects,
        ProjectSetupScriptRunner setupScripts,
        GlobalSearchService search,
        SourceControlHostingService sourceControl,
        HostDiagnosticsService diagnostics,
        PiThreadRegistry threads,
        TerminalSessionRegistry terminals)
    {
        _options = options;
        _database = database;
        _attachmentStorage = attachmentStorage;
        _fileSearch = fileSearch;
        _fileRead = fileRead;
        _git = git;
        _gitCommands = gitCommands;
        _checkpoints = checkpoints;
        _preview = preview;
        _environment = environment;
        _projects = projects;
        _setupScripts = setupScripts;
        _search = search;
        _sourceControl = sourceControl;
        _diagnostics = diagnostics;
        _threads = threads;
        _terminals = terminals;
        BrowserAutomation = new(options.BrowserAutomationRoot);
        Updates = new(options.CanonicalDataRoot, () => _threads.HasActiveWork || _terminals.HasActiveWork);
    }

    public EnvironmentId EnvironmentId => _environment.EnvironmentId;

    public PreviewLeaseRegistry PreviewLeases { get; } = new();
    public BrowserAutomationBridge BrowserAutomation { get; }

    public async Task<BrowserAutomationLease> OpenBrowserAutomationAsync(OpenBrowserAutomationRequest request, string principal, string connection, CancellationToken token)
    {
        if (await _database.GetThreadAsync(request.ThreadId, token).ConfigureAwait(false) is null)
            throw new HostOperationException(ProtocolErrorCodes.ThreadNotFound, "The browser thread no longer exists.");
        return await BrowserAutomation.OpenAsync(request, principal, connection, token).ConfigureAwait(false);
    }

    public PiStation.Host.Updates.RemoteUpdateCoordinator Updates { get; }

    private int _threadStreams;
    private int _terminalStreams;
    public int ActiveThreadStreamCount => Volatile.Read(ref _threadStreams);
    public int ActiveTerminalStreamCount => Volatile.Read(ref _terminalStreams);
    internal StreamLifetime TrackStream(bool terminal) => new(this, terminal);
    internal sealed class StreamLifetime : IDisposable
    {
        private readonly EnvironmentService _owner;
        private readonly bool _terminal;
        private int _disposed;
        internal StreamLifetime(EnvironmentService owner, bool terminal)
        {
            _owner = owner; _terminal = terminal;
            if (terminal) Interlocked.Increment(ref owner._terminalStreams);
            else Interlocked.Increment(ref owner._threadStreams);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_terminal) Interlocked.Decrement(ref _owner._terminalStreams);
            else Interlocked.Decrement(ref _owner._threadStreams);
        }
    }

    public async Task<PreviewLease> OpenPreviewAsync(OpenPreviewRequest request, string principal, string connectionId, CancellationToken cancellationToken)
    {
        if (await _database.GetProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is null)
            throw new HostOperationException(ProtocolErrorCodes.ProjectNotFound, "The preview project no longer exists.");
        return PreviewLeases.Open(request, principal, connectionId, cancellationToken);
    }

    private RemoteExposure? _remoteExposure;
    internal RemoteExposure? CurrentRemoteExposure => Volatile.Read(ref _remoteExposure);
    internal void RegisterRemoteExposure(object owner, Uri address, string fingerprint) =>
        Volatile.Write(ref _remoteExposure, new(owner, address, fingerprint));
    internal void UnregisterRemoteExposure(object owner)
    {
        var exposure = CurrentRemoteExposure;
        if (exposure?.Owner == owner) Interlocked.CompareExchange(ref _remoteExposure, null, exposure);
    }
    internal sealed record RemoteExposure(object Owner, Uri Address, string CertificateFingerprint);

    public static async Task<EnvironmentService> CreateAsync(
        HostOptions options,
        IPiProcessFactory? processFactory = null,
        Func<ThreadWorkspaceResolver, ProjectService, SourceControlHostingService>? sourceControlFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var database = new HostDatabase(options);
        var environment = await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var projects = new ProjectService(database);
        var attachmentStorage = new DraftAttachmentStorage(options);
        var workspaceResolver = new ThreadWorkspaceResolver(database);
        var workspaceLocks = new WorkspaceOperationLocks();
        var fileSearch = new WorkspaceFileSearchService(database, options, workspaceResolver);
        var fileRead = new WorkspaceFileReadService(database, workspaceResolver, workspaceLocks);
        var git = new WorkspaceGitService(database, workspaceResolver);
        var checkpoints = new WorkspaceCheckpointService(database, workspaceResolver, workspaceLocks);
        var gitCommands = new WorkspaceGitCommandService(database, git, workspaceResolver, options, workspaceLocks);
        var search = new GlobalSearchService(database, gitCommands, options, environment.EnvironmentId);
        var preview = new PreviewDiscoveryService(database);
        var threads = new PiThreadRegistry(
            environment,
            database,
            processFactory ?? new PiProcessFactory(options),
            checkpoints,
            options);
        var terminals = new TerminalSessionRegistry(database, options, workspaceResolver);
        var setupScripts = new ProjectSetupScriptRunner(database, terminals);
        var sourceControl = sourceControlFactory?.Invoke(workspaceResolver, projects) ?? new SourceControlHostingService(workspaceResolver, projects,
            textGenerator: new PiSourceControlTextGenerator(options), writingSettings: new SourceControlWritingSettingsStore(options.CanonicalDataRoot));
        var diagnostics = new HostDiagnosticsService(options, database, threads, terminals);
        var service = new EnvironmentService(
            options,
            database,
            attachmentStorage,
            fileSearch,
            fileRead,
            git,
            gitCommands,
            checkpoints,
            preview,
            environment,
            projects,
            setupScripts,
            search,
            sourceControl,
            diagnostics,
            threads,
            terminals);
        var knownProjects = await projects.ListAsync(cancellationToken).ConfigureAwait(false);
        await new ProjectAutoPullService(diagnostics.Record)
            .PullEligibleProjectsAsync(knownProjects, cancellationToken).ConfigureAwait(false);
        service._settlementWorker = Task.Run(service.RunSettlementWorkerAsync, CancellationToken.None);
        return service;
    }

    public EnvironmentDescriptor GetDescriptor() => new(
        _environment.EnvironmentId,
        _environment.Name,
        typeof(EnvironmentService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        ProtocolVersion.Current,
        ProtocolVersion.Current,
        _options.PiInstallation is not null,
        _options.PiInstallation?.PiVersion.ToString(),
        ["catalog.read", "project.read", "project.write", "project.remove", "project.defaults", "project.scripts", "file.search", "file.content-search", "file.read", "file.write", "file.assets", "file.artifacts", "editor.open", "git.read", "git.write", "git.refs", "git.worktrees", "source-control.hosting", "source-control.text-generation", "source-control.pull-requests", "checkpoint.read", "checkpoint.revert", "preview.discover", "search.global", "terminal.operate", "thread.read", "thread.operate", "thread.interact", "thread.queue", "thread.agents", "thread.draft", "thread.composer", "thread.compaction", "thread.inbox", "thread.titles", "thread.configure", "thread.lifecycle", "thread.search", "attachment.upload", "attachment.download", "session.transfer", "browser.automation", "diagnostics.read", "diagnostics.export", "usage.read"]);

    public Task<IReadOnlyList<ProjectDescriptor>> ListProjectsAsync(CancellationToken cancellationToken = default) =>
        _projects.ListAsync(cancellationToken);

    public async IAsyncEnumerable<CatalogBatch> SubscribeCatalogAsync(CatalogCursor? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource lifetime;
        lock (_catalogGate)
        {
            if (_catalogDisposed) yield break;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _catalogStopping.Token);
            _catalogReaders.Add(completed);
        }
        using (lifetime)
        try
        {
            var initial = true;
            while (!lifetime.IsCancellationRequested)
            {
                var changed = false;
                await foreach (var batch in _database.ReadCatalogAsync(cursor, lifetime.Token).ConfigureAwait(false))
                {
                    changed = true;
                    if (batch.Complete) cursor = new(batch.Epoch, batch.Sequence);
                    yield return batch;
                }
                if (initial && !changed && cursor is not null)
                    yield return new CatalogBatch(EnvironmentId, cursor.Epoch, cursor.Sequence, false, true, [], [], [], []);
                initial = false;
                await Task.Delay(TimeSpan.FromMilliseconds(250), lifetime.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_catalogGate) _catalogReaders.Remove(completed);
            completed.TrySetResult();
        }
    }

    public Task<ProjectDescriptor> AddProjectAsync(
        AddProjectRequest request,
        CancellationToken cancellationToken = default) => _projects.AddAsync(request, cancellationToken);

    public Task<ProjectDescriptor> SetProjectScriptsTrustAsync(
        SetProjectScriptsTrustRequest request,
        CancellationToken cancellationToken = default) => _projects.SetScriptsTrustAsync(request, cancellationToken);

    public Task<ProjectSetupScriptResult> RunProjectSetupScriptAsync(
        RunProjectSetupScriptRequest request,
        CancellationToken cancellationToken = default) => _setupScripts.RunAsync(request, cancellationToken);

    public Task<ProjectSetupScriptResult> RunProjectScriptAsync(
        RunProjectScriptRequest request,
        CancellationToken cancellationToken = default) => _setupScripts.RunNamedAsync(request, cancellationToken);

    public async Task<ProjectDescriptor> UpdateProjectDefaultsAsync(
        UpdateProjectDefaultsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.UploadedIcon is { } upload)
        {
            if (!request.UpdateCustomization) throw new ArgumentException("An icon upload requires project customization.");
            _ = await _database.GetProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
                ?? throw new HostOperationException(ProtocolErrorCodes.ProjectNotFound, "The project was not found.");
            var icon = await Projects.ProjectIconStorage.SaveAsync(_options.CanonicalDataRoot, upload, cancellationToken).ConfigureAwait(false);
            request = request with { Icon = icon, UploadedIcon = null };
        }
        return await _projects.UpdateDefaultsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveProjectAsync(
        RemoveProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        var threads = await _database.ListThreadsAsync(request.ProjectId, includeArchived: true, cancellationToken)
            .ConfigureAwait(false);
        var attachments = new List<DraftAttachment>();
        foreach (var stash in await _database.ListPromptStashesAsync(request.ProjectId, cancellationToken).ConfigureAwait(false))
            attachments.AddRange(stash.Attachments ?? []);
        foreach (var thread in threads)
        {
            await _threads.StopAndForgetAsync(thread.ThreadId, cancellationToken).ConfigureAwait(false);
            try
            {
                attachments.AddRange((await _database.GetOrCreateThreadDraftAsync(thread.ThreadId, cancellationToken)
                    .ConfigureAwait(false)).Attachments);
                attachments.AddRange((await _database.ListSentMessagesAsync(thread.ThreadId, cancellationToken)
                    .ConfigureAwait(false)).Values.SelectMany(content => content.Attachments));
            }
            catch (KeyNotFoundException)
            {
            }
        }
        foreach (var terminal in await _terminals.ListAsync(request.ProjectId, cancellationToken).ConfigureAwait(false))
        {
            await _terminals.CloseAsync(new CloseTerminalSessionRequest(terminal.TerminalSessionId), cancellationToken)
                .ConfigureAwait(false);
        }
        await _projects.RemoveAsync(request, cancellationToken).ConfigureAwait(false);
        foreach (var attachment in attachments)
        {
            await DeleteUnreferencedAttachmentAsync(attachment).ConfigureAwait(false);
        }
        _diagnostics.Record($"Project {request.ProjectId} was removed from Pi Station.");
    }

    public Task<ComposerDiscoveryResult> GetComposerDiscoveryAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default) => GetComposerDiscoveryCoreAsync(threadId, cancellationToken);

    private async Task<ComposerDiscoveryResult> GetComposerDiscoveryCoreAsync(
        ThreadId threadId,
        CancellationToken cancellationToken)
    {
        var controller = await _threads.GetAsync(threadId, cancellationToken).ConfigureAwait(false);
        return await controller.GetComposerDiscoveryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PromptStash>> ListPromptStashesAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default) => _database.ListPromptStashesAsync(projectId, cancellationToken);

    public async Task<PromptStash> SavePromptStashAsync(
        SavePromptStashRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((string.IsNullOrWhiteSpace(request.Text) && request.DraftId is null) || request.Text.Length > 128 * 1024)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.PromptStashInvalid,
                "A prompt stash must contain between 1 and 131072 characters.");
        }
        if (await _database.GetProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new HostOperationException(ProtocolErrorCodes.ProjectNotFound, $"Project '{request.ProjectId}' was not found.");
        }
        return await _database.SavePromptStashAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeletePromptStashAsync(
        DeletePromptStashRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var stash = await _database.GetPromptStashAsync(request.StashId, cancellationToken).ConfigureAwait(false);
            await _database.DeletePromptStashAsync(request.StashId, cancellationToken).ConfigureAwait(false);
            foreach (var attachment in stash?.Attachments ?? []) await DeleteUnreferencedAttachmentAsync(attachment).ConfigureAwait(false);
        }
        catch (KeyNotFoundException exception)
        {
            throw new HostOperationException(ProtocolErrorCodes.PromptStashNotFound, exception.Message);
        }
    }

    public Task<SourceControlRepository> DetectSourceControlAsync(
        DetectSourceControlRequest request,
        CancellationToken cancellationToken = default) => _sourceControl.DetectAsync(request, cancellationToken);

    public Task<ListPullRequestsResult> ListPullRequestsAsync(
        ListPullRequestsRequest request,
        CancellationToken cancellationToken = default) => _sourceControl.ListPullRequestsAsync(request, cancellationToken);

    public Task<SourceControlOperationResult> CloneHostedRepositoryAsync(
        CloneHostedRepositoryRequest request,
        CancellationToken cancellationToken = default) => new HostingOperationRunner(_database).RunAsync(
            request.OperationId, "Clone repository", JsonSerializer.Serialize(request, ProtocolJsonContext.Default.CloneHostedRepositoryRequest),
            token => _sourceControl.CloneAsync(request, token), cancellationToken);

    public Task<SourceControlOperationResult> PublishHostedRepositoryAsync(
        PublishHostedRepositoryRequest request,
        CancellationToken cancellationToken = default) => new HostingOperationRunner(_database).RunAsync(
            request.OperationId, "Publish repository", JsonSerializer.Serialize(request, ProtocolJsonContext.Default.PublishHostedRepositoryRequest),
            token => _sourceControl.PublishAsync(request, token), cancellationToken);

    public Task<SourceControlOperationResult> CreatePullRequestAsync(
        CreatePullRequestRequest request,
        CancellationToken cancellationToken = default) => new HostingOperationRunner(_database).RunAsync(
            request.OperationId, "Create pull request", JsonSerializer.Serialize(request, ProtocolJsonContext.Default.CreatePullRequestRequest),
            token => _sourceControl.CreatePullRequestAsync(request, token), cancellationToken);

    public Task<IReadOnlyList<HostingOperation>> ListHostingOperationsAsync(CancellationToken cancellationToken = default) =>
        _database.ListHostingOperationsAsync(cancellationToken);

    public async Task<SourceControlOperationResult> MutatePullRequestAsync(
        MutatePullRequestRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await new HostingOperationRunner(_database).RunAsync(
            request.OperationId, "Update pull request", JsonSerializer.Serialize(request, ProtocolJsonContext.Default.MutatePullRequestRequest),
            token => _sourceControl.MutatePullRequestAsync(request, token), cancellationToken).ConfigureAwait(false);
        if (result.Succeeded && request.Mutation is PullRequestMutationKind.Merge or PullRequestMutationKind.Close)
        {
            try { await SweepThreadSettlementAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) { _diagnostics.Record("Pull request updated; immediate settlement refresh failed: " + error.Message); }
        }
        return result;
    }

    public Task<GeneratedSourceControlText> GenerateSourceControlTextAsync(
        GenerateSourceControlTextRequest request,
        CancellationToken cancellationToken = default) => _sourceControl.GenerateTextAsync(request, cancellationToken);

    public Task<SourceControlWritingSettings> GetSourceControlWritingSettingsAsync(CancellationToken token = default) => _sourceControl.GetWritingSettingsAsync(token);
    public Task<SourceControlWritingSettings> SaveSourceControlWritingSettingsAsync(SourceControlWritingSettings settings, CancellationToken token = default) => _sourceControl.SaveWritingSettingsAsync(settings, token);

    public Task<DiagnosticsSnapshot> GetDiagnosticsAsync(CancellationToken cancellationToken = default) =>
        _diagnostics.GetSnapshotAsync(cancellationToken);

    public Task<ExportDiagnosticsResult> ExportDiagnosticsAsync(
        ExportDiagnosticsRequest request,
        CancellationToken cancellationToken = default) => _diagnostics.ExportAsync(request, cancellationToken);

    public Task<SearchProjectFilesResult> SearchProjectFilesAsync(
        SearchProjectFilesRequest request,
        CancellationToken cancellationToken = default) => _fileSearch.SearchAsync(request, cancellationToken);

    public Task<ListProjectEntriesResult> ListProjectEntriesAsync(
        ListProjectEntriesRequest request,
        CancellationToken cancellationToken = default) => _fileSearch.ListAsync(request, cancellationToken);

    public Task<SearchProjectContentsResult> SearchProjectContentsAsync(
        SearchProjectContentsRequest request,
        CancellationToken cancellationToken = default) => _fileSearch.SearchContentsAsync(request, cancellationToken);

    public Task<ReadProjectFileResult> ReadProjectFileAsync(
        ReadProjectFileRequest request,
        CancellationToken cancellationToken = default) => _fileRead.ReadAsync(request, cancellationToken);

    public Task<ReadProjectFileAssetResult> ReadProjectFileAssetAsync(
        ReadProjectFileAssetRequest request,
        CancellationToken cancellationToken = default) => _fileRead.ReadAssetAsync(request, cancellationToken);

    public Task<SaveProjectFileResult> SaveProjectFileAsync(
        SaveProjectFileRequest request,
        CancellationToken cancellationToken = default) => _fileRead.SaveAsync(request, cancellationToken);

    public Task<OpenProjectFileInEditorResult> OpenProjectFileInEditorAsync(
        OpenProjectFileInEditorRequest request,
        CancellationToken cancellationToken = default) => _fileRead.OpenInEditorAsync(request, cancellationToken);

    public Task<GetProjectChangesResult> GetProjectChangesAsync(
        GetProjectChangesRequest request,
        CancellationToken cancellationToken = default) => _git.GetChangesAsync(request, cancellationToken);

    public Task<GetProjectChangeDiffResult> GetProjectChangeDiffAsync(
        GetProjectChangeDiffRequest request,
        CancellationToken cancellationToken = default) => _git.GetDiffAsync(request, cancellationToken);

    public Task<ListGitRefsResult> ListGitRefsAsync(
        ListGitRefsRequest request,
        CancellationToken cancellationToken = default) => _gitCommands.ListRefsAsync(request, cancellationToken);

    public Task<ListGitWorktreesResult> ListGitWorktreesAsync(
        ListGitWorktreesRequest request,
        CancellationToken cancellationToken = default) => _gitCommands.ListWorktreesAsync(request, cancellationToken);

    public Task<ExecuteWorkspaceGitCommandResult?> GetWorkspaceGitCommandResultAsync(
        ClientId clientId,
        CommandId commandId,
        CancellationToken cancellationToken = default) =>
        _gitCommands.GetResultAsync(clientId, commandId, cancellationToken);

    public async Task<ExecuteWorkspaceGitCommandResult> ExecuteWorkspaceGitCommandAsync(
        ExecuteWorkspaceGitCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var affectedThreadId = request.Target.ThreadId ??
            (request.Command as GitCreateWorktreeCommand)?.AssignToThreadId;
        if (affectedThreadId is { } threadId &&
            _threads.TryGetRuntimeState(threadId, out var runtimeState) &&
            runtimeState is ThreadRuntimeState.Running or ThreadRuntimeState.Stopping)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.GitConflict,
                "Git workspace operations are unavailable while the thread has an active turn.");
        }

        if (affectedThreadId is { } lifecycleThreadId &&
            request.Command is GitCreateWorktreeCommand or GitRemoveWorktreeCommand)
        {
            await _threads.StopAndForgetAsync(lifecycleThreadId, cancellationToken).ConfigureAwait(false);
        }

        return await _gitCommands.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task<GetThreadCheckpointDiffResult> GetThreadCheckpointDiffAsync(
        GetThreadCheckpointDiffRequest request,
        CancellationToken cancellationToken = default) => _checkpoints.GetDiffAsync(request, cancellationToken);

    public Task<DiscoverProjectPreviewServersResult> DiscoverProjectPreviewServersAsync(
        DiscoverProjectPreviewServersRequest request,
        CancellationToken cancellationToken = default) => _preview.DiscoverAsync(request, cancellationToken);

    public Task<TerminalSessionDescriptor> StartTerminalSessionAsync(
        StartTerminalSessionRequest request,
        CancellationToken cancellationToken = default) => _terminals.StartAsync(request, cancellationToken);

    public Task<IReadOnlyList<TerminalSessionDescriptor>> ListTerminalSessionsAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default) => _terminals.ListAsync(projectId, cancellationToken);

    public Task WriteTerminalInputAsync(
        WriteTerminalInputRequest request,
        CancellationToken cancellationToken = default) => _terminals.WriteAsync(request, cancellationToken);

    public Task<TerminalSessionDescriptor> ResizeTerminalSessionAsync(
        ResizeTerminalSessionRequest request,
        CancellationToken cancellationToken = default) => _terminals.ResizeAsync(request, cancellationToken);

    public Task<TerminalSessionDescriptor> StopTerminalSessionAsync(
        StopTerminalSessionRequest request,
        CancellationToken cancellationToken = default) => _terminals.StopAsync(request, cancellationToken);

    public Task CloseTerminalSessionAsync(
        CloseTerminalSessionRequest request,
        CancellationToken cancellationToken = default) => _terminals.CloseAsync(request, cancellationToken);

    public async Task<IReadOnlyList<ThreadDescriptor>> ListThreadsAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default) => (await _projects.ListThreadsAsync(projectId, cancellationToken).ConfigureAwait(false)).Select(WithRuntimeStatus).ToArray();

    public async Task<ThreadDescriptor> GetThreadAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default)
    {
        var thread = await _database.GetThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        return thread is not null
            ? await _database.EnrichThreadDescriptorAsync(thread, cancellationToken).ConfigureAwait(false)
            :
            throw new HostOperationException(
                ProtocolErrorCodes.ThreadNotFound,
                $"Thread '{threadId}' was not found.");
    }

    public async Task DeleteThreadAsync(
        DeleteThreadRequest request,
        CancellationToken cancellationToken = default)
    {
        var thread = await _database.GetThreadAsync(request.ThreadId, cancellationToken).ConfigureAwait(false)
            ?? throw new HostOperationException(ProtocolErrorCodes.ThreadNotFound, $"Thread '{request.ThreadId}' was not found.");
        await _threads.StopAndForgetAsync(thread.ThreadId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DraftAttachment> attachments = [];
        try
        {
            var draft = await _database.GetOrCreateThreadDraftAsync(thread.ThreadId, cancellationToken).ConfigureAwait(false);
            attachments = draft.Attachments.Concat((await _database.ListSentMessagesAsync(thread.ThreadId, cancellationToken)
                .ConfigureAwait(false)).Values.SelectMany(content => content.Attachments)).ToArray();
        }
        catch (KeyNotFoundException)
        {
        }
        await _database.DeleteThreadAsync(thread.ThreadId, cancellationToken).ConfigureAwait(false);
        foreach (var attachment in attachments) await DeleteUnreferencedAttachmentAsync(attachment).ConfigureAwait(false);
    }

    public async Task<ApplyThreadBulkOperationResult> ApplyThreadBulkOperationAsync(
        ApplyThreadBulkOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ThreadIds.Count is < 1 or > 200)
        {
            throw new HostOperationException(ProtocolErrorCodes.ThreadInvalid, "Select between 1 and 200 threads.");
        }
        if (request.Operation == ThreadBulkOperation.Snooze &&
            (request.SnoozedUntilUtc is null || request.SnoozedUntilUtc <= DateTimeOffset.UtcNow))
        {
            throw new HostOperationException(ProtocolErrorCodes.ThreadInvalid, "A snooze time must be in the future.");
        }
        if (request.Operation == ThreadBulkOperation.Delete)
        {
            var threadIds = request.ThreadIds.Distinct().ToArray();
            foreach (var threadId in threadIds)
            {
                var thread = await _database.GetThreadAsync(threadId, cancellationToken).ConfigureAwait(false)
                    ?? throw new HostOperationException(ProtocolErrorCodes.ThreadNotFound, $"Thread '{threadId}' was not found.");
                if (thread.ProjectId != request.ProjectId)
                {
                    throw new HostOperationException(ProtocolErrorCodes.ThreadInvalid, $"Thread '{threadId}' belongs to another project.");
                }
            }

            var attachments = new List<DraftAttachment>();
            foreach (var threadId in threadIds)
            {
                await _threads.StopAndForgetAsync(threadId, cancellationToken).ConfigureAwait(false);
                try
                {
                    attachments.AddRange((await _database.GetOrCreateThreadDraftAsync(threadId, cancellationToken)
                        .ConfigureAwait(false)).Attachments);
                    attachments.AddRange((await _database.ListSentMessagesAsync(threadId, cancellationToken)
                        .ConfigureAwait(false)).Values.SelectMany(content => content.Attachments));
                }
                catch (KeyNotFoundException)
                {
                }
            }
            var deleted = await _database.ApplyThreadBulkOperationAsync(request, cancellationToken).ConfigureAwait(false);
            foreach (var attachment in attachments)
            {
                await DeleteUnreferencedAttachmentAsync(attachment).ConfigureAwait(false);
            }
            return new ApplyThreadBulkOperationResult(
                deleted,
                await _projects.ListThreadsAsync(request.ProjectId, cancellationToken).ConfigureAwait(false));
        }
        var affected = await _database.ApplyThreadBulkOperationAsync(request, cancellationToken).ConfigureAwait(false);
        return new ApplyThreadBulkOperationResult(
            affected,
            await _projects.ListThreadsAsync(request.ProjectId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<ThreadDescriptor>> SetThreadPinnedOrderAsync(
        SetThreadPinnedOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        await _database.SetThreadPinnedOrderAsync(request, cancellationToken).ConfigureAwait(false);
        return await _projects.ListThreadsAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ThreadDescriptor> LinkThreadPullRequestAsync(
        LinkThreadPullRequestRequest request,
        CancellationToken cancellationToken = default)
    {
        var current = await _database.GetThreadAsync(request.ThreadId, cancellationToken).ConfigureAwait(false)
            ?? throw new HostOperationException(ProtocolErrorCodes.ThreadNotFound, $"Thread '{request.ThreadId}' was not found.");
        var update = await _database.UpdateThreadInboxAsync(
            request.ThreadId,
            current.Revision,
            pullRequest: request.PullRequest,
            updatePullRequest: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!update.WasUpdated || update.Thread is null)
        {
            throw new HostOperationException(ProtocolErrorCodes.ThreadConflict, "The thread changed before its pull request could be linked.");
        }
        return await _database.EnrichThreadDescriptorAsync(update.Thread, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SearchThreadsResult> SearchThreadsAsync(
        SearchThreadsRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _projects.SearchThreadsAsync(request, cancellationToken).ConfigureAwait(false);
        return result with { Threads = result.Threads.Select(WithRuntimeStatus).ToArray() };
    }

    private ThreadDescriptor WithRuntimeStatus(ThreadDescriptor thread)
    {
        if (thread.SnoozedUntilUtc <= DateTimeOffset.UtcNow) thread = thread with { SnoozedUntilUtc = null };
        if (!_threads.TryGetController(thread.ThreadId, out var controller) || controller is null) return thread;
        var projection = controller.Journal.Projection;
        return thread with { RuntimeState = projection.RuntimeState, NeedsAttention = projection.Timeline.Any(item =>
            item is ApprovalTimelineItem { State: InteractionState.Pending } or QuestionTimelineItem { State: InteractionState.Pending }) };
    }

    public Task<GlobalSearchResult> SearchGlobalAsync(
        GlobalSearchRequest request,
        CancellationToken cancellationToken = default) => _search.SearchAsync(request, cancellationToken);

    public async Task<ThreadDescriptor> CreateThreadAsync(
        CreateThreadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = await _database.GetProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new HostOperationException(
                ProtocolErrorCodes.ProjectNotFound,
                $"Project '{request.ProjectId}' was not found.");
        var workspaceMode = request.WorkspaceMode ?? project.DefaultWorkspaceMode;
        await using var sharedLease = request.ReuseWorktreeFromThreadId is not null
            ? await _gitCommands.AcquireProjectLockAsync(project.ProjectId, cancellationToken).ConfigureAwait(false) : null;
        HostThreadRecord? sharedWorktree = null;
        if (request.ReuseWorktreeFromThreadId is { } sourceId)
        {
            sharedWorktree = await _database.GetThreadAsync(sourceId, cancellationToken).ConfigureAwait(false);
            var registered = await _gitCommands.ListWorktreesAsync(new ListGitWorktreesRequest(project.ProjectId), cancellationToken).ConfigureAwait(false);
            if (sharedWorktree is null || sharedWorktree.ProjectId != project.ProjectId ||
                sharedWorktree.WorkspaceMode != ThreadWorkspaceMode.Worktree ||
                string.IsNullOrWhiteSpace(sharedWorktree.WorktreePath) || !Directory.Exists(sharedWorktree.WorktreePath) ||
                !registered.Worktrees.Any(tree => string.Equals(Path.GetFullPath(tree.Path), Path.GetFullPath(sharedWorktree.WorktreePath), StringComparison.OrdinalIgnoreCase)))
                throw new HostOperationException(ProtocolErrorCodes.WorktreeOwnershipMismatch, "The source thread no longer has a registered worktree in this project.");
        }
        var thread = await _projects.CreateThreadAsync(request, cancellationToken).ConfigureAwait(false);
        if (sharedWorktree is not null)
        {
            await _database.UpdateThreadWorkspaceAsync(thread.ThreadId, ThreadWorkspaceMode.Worktree,
                sharedWorktree.BranchName, sharedWorktree.WorktreePath, true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return await GetThreadAsync(thread.ThreadId, cancellationToken).ConfigureAwait(false);
        }
        if (workspaceMode != ThreadWorkspaceMode.Worktree)
        {
            return thread;
        }

        try
        {
            await _gitCommands.CreateManagedWorktreeAsync(
                request.ProjectId,
                thread.ThreadId,
                request.BaseBranch,
                request.StartFromOrigin,
                request.BranchName,
                cancellationToken).ConfigureAwait(false);
            if (request.RunSetupScript)
            {
                _ = await _setupScripts.RunAsync(
                    new RunProjectSetupScriptRequest(request.ProjectId, thread.ThreadId),
                    cancellationToken).ConfigureAwait(false);
            }
            return await GetThreadAsync(thread.ThreadId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _database.DeleteThreadAsync(thread.ThreadId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ThreadDraft> GetThreadDraftAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _database.GetOrCreateThreadDraftAsync(threadId, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException exception)
        {
            throw new HostOperationException(ProtocolErrorCodes.ThreadNotFound, exception.Message);
        }
    }

    public async Task<ThreadDraft> GetThreadDraftPassiveAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default)
    {
        var draft = await _database.GetThreadDraftAsync(threadId, cancellationToken).ConfigureAwait(false);
        if (draft is not null)
        {
            return draft;
        }

        if (await _database.GetThreadAsync(threadId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new HostOperationException(ProtocolErrorCodes.ThreadNotFound, $"Thread '{threadId}' was not found.");
        }

        var stableId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(threadId.Value))).ToLowerInvariant()[..32];
        return new ThreadDraft(
            _environment.EnvironmentId,
            threadId,
            DraftId.Parse(stableId),
            string.Empty,
            0,
            DateTimeOffset.UnixEpoch,
            []);
    }

    public async Task<ThreadPiConfigurationSnapshot> GetThreadPiConfigurationAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default)
    {
        var controller = await _threads.GetAsync(threadId, cancellationToken).ConfigureAwait(false);
        return await controller.GetPiConfigurationAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ThreadPiConfigurationSnapshot> GetThreadPiConfigurationPassiveAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default)
    {
        var controller = await _threads.GetAsync(threadId, cancellationToken).ConfigureAwait(false);
        return await controller.GetPersistedPiConfigurationAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommandReceipt> ExecuteThreadCommandAsync(
        ExecuteThreadCommandRequest request,
        CancellationToken cancellationToken = default) =>
        await ExecuteThreadCommandCoreAsync(request, allowAttachmentAdd: false, cancellationToken)
            .ConfigureAwait(false);

    public async Task<DraftAttachmentUploadResult> UploadDraftAttachmentAsync(
        UploadDraftAttachmentRequest request,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);
        ValidateUploadRequest(request);
        var current = await GetThreadDraftAsync(request.ThreadId, cancellationToken).ConfigureAwait(false);
        if (current.DraftId != request.DraftId)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.DraftNotFound,
                "The draft identity is not valid for this thread.");
        }

        var stored = await _attachmentStorage.StoreAsync(request, content, cancellationToken).ConfigureAwait(false);
        var commandRequest = new ExecuteThreadCommandRequest(
            request.ProtocolVersion,
            request.EnvironmentId,
            request.ClientId,
            request.CommandId,
            request.ThreadId,
            null,
            null,
            new ThreadAddDraftAttachmentCommand(
                request.DraftId,
                request.AttachmentId,
                request.ExpectedDraftRevision,
                stored.Attachment.FileName,
                stored.Attachment.MediaType,
                stored.Attachment.ByteLength,
                stored.Attachment.Sha256,
                stored.Attachment.ServerPath));
        try
        {
            var receipt = await ExecuteThreadCommandCoreAsync(
                commandRequest,
                allowAttachmentAdd: true,
                cancellationToken).ConfigureAwait(false);
            var draft = await GetThreadDraftAsync(request.ThreadId, cancellationToken).ConfigureAwait(false);
            var attachment = draft.Attachments.SingleOrDefault(
                candidate => candidate.AttachmentId == request.AttachmentId);
            if (attachment is null)
            {
                await DeleteUnreferencedAttachmentAsync(stored.Attachment).ConfigureAwait(false);
            }

            return new DraftAttachmentUploadResult(
                receipt,
                receipt.State == CommandReceiptState.Completed ? draft : null,
                receipt.State == CommandReceiptState.Completed ? attachment : null);
        }
        catch
        {
            try
            {
                var draft = await GetThreadDraftAsync(request.ThreadId, CancellationToken.None).ConfigureAwait(false);
                if (draft.Attachments.All(candidate => candidate.AttachmentId != request.AttachmentId))
                {
                    await DeleteUnreferencedAttachmentAsync(stored.Attachment).ConfigureAwait(false);
                }
            }
            catch
            {
                // Preserve the original upload/dispatch failure rather than masking it with cleanup failure.
            }

            throw;
        }
    }

    private async Task<CommandReceipt> ExecuteThreadCommandCoreAsync(
        ExecuteThreadCommandRequest request,
        bool allowAttachmentAdd,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var bodyHash = ComputeBodyHash(request);
        var now = DateTimeOffset.UtcNow;
        var received = new CommandReceipt(
            _environment.EnvironmentId,
            request.ClientId,
            request.CommandId,
            request.ThreadId,
            CommandReceiptState.Received,
            null,
            now,
            now);
        var acquisition = await _database.AcquireReceiptAsync(received, bodyHash, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(acquisition.StoredReceipt.BodyHash, bodyHash, StringComparison.Ordinal) ||
            acquisition.StoredReceipt.Receipt.ThreadId != request.ThreadId ||
            acquisition.StoredReceipt.Receipt.EnvironmentId != request.EnvironmentId)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.CommandConflict,
                "The command ID was already used with a different command body.");
        }

        if (!acquisition.WasCreated)
        {
            return acquisition.StoredReceipt.Receipt;
        }

        await _database.UpdateReceiptStateAsync(
            request.ClientId,
            request.CommandId,
            CommandReceiptState.Dispatching,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            PiThreadController? controller = null;
            if (request.Command is not (ThreadSaveDraftCommand or
                                        ThreadAddDraftAttachmentCommand or
                                        ThreadRemoveDraftAttachmentCommand or
                                        ThreadClearDraftCommand or
                                        ThreadRestoreStashCommand or
                                        ThreadRenameCommand or
                                        ThreadSetArchivedCommand or
                                        ThreadSetPinnedCommand or
                                        ThreadSetSettledCommand or
                                        ThreadSetReadStateCommand or
                                        ThreadSetSnoozedCommand or
                                        ThreadSetPinnedOrderCommand))
            {
                controller = await _threads.GetAsync(request.ThreadId, cancellationToken).ConfigureAwait(false);
                ValidateExpectations(request, controller);
            }

            switch (request.Command)
            {
                case ThreadSetReadStateCommand readState:
                    await _database.SetReadStateAsync(request.ThreadId, readState.ObservedCompletionSequence,
                        readState.IsUnread, cancellationToken).ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadManageAgentsCommand agents:
                    await controller!.ManageAgentsAsync(agents, cancellationToken).ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadRunAgentWorkflowCommand workflow:
                    await controller!.RunAgentWorkflowAsync(workflow.Workflow, request.ClientId, request.CommandId, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadManagePlanCommand { Action: "execute" } plan:
                    await controller!.ExecutePlanAsync(plan.ExpectedRevision, request.ClientId, request.CommandId, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadManagePlanCommand plan:
                    await controller!.ManagePlanAsync(plan, cancellationToken).ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadStartTurnCommand start:
                    ArgumentNullException.ThrowIfNull(start.Prompt);
                    var promptAttachments = await ResolveTurnAttachmentsAsync(
                        request.ThreadId,
                        start.Prompt,
                        start.DraftId,
                        start.DraftRevision,
                        start.AttachmentIds,
                        cancellationToken).ConfigureAwait(false);
                    await controller!.StartTurnAsync(
                        await RetainSentContentAsync(request.ThreadId, start.Prompt, start.DraftId,
                            start.DraftRevision, promptAttachments, cancellationToken).ConfigureAwait(false),
                        promptAttachments,
                        request.ClientId,
                        request.CommandId,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadQueueSteeringCommand steering:
                    var steeringAttachments = await ResolveTurnAttachmentsAsync(
                        request.ThreadId,
                        steering.Prompt,
                        steering.DraftId,
                        steering.DraftRevision,
                        steering.AttachmentIds,
                        cancellationToken).ConfigureAwait(false);
                    await controller!.QueueMessageAsync(
                        QueuedMessageKind.Steering,
                        await RetainSentContentAsync(request.ThreadId, steering.Prompt, steering.DraftId,
                            steering.DraftRevision, steeringAttachments, cancellationToken).ConfigureAwait(false),
                        steeringAttachments,
                        cancellationToken).ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadQueueFollowUpCommand followUp:
                    var followUpAttachments = await ResolveTurnAttachmentsAsync(
                        request.ThreadId,
                        followUp.Prompt,
                        followUp.DraftId,
                        followUp.DraftRevision,
                        followUp.AttachmentIds,
                        cancellationToken).ConfigureAwait(false);
                    await controller!.QueueMessageAsync(
                        QueuedMessageKind.FollowUp,
                        await RetainSentContentAsync(request.ThreadId, followUp.Prompt, followUp.DraftId,
                            followUp.DraftRevision, followUpAttachments, cancellationToken).ConfigureAwait(false),
                        followUpAttachments,
                        cancellationToken).ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadClearQueueCommand:
                    await controller!.ClearQueueAsync(cancellationToken).ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadRefreshQueueCommand:
                    await controller!.RefreshQueueAsync(cancellationToken).ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadSetQueueDeliveryModeCommand deliveryMode:
                    await controller!.SetQueueDeliveryModeAsync(
                        deliveryMode.Kind,
                        deliveryMode.Mode,
                        cancellationToken).ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadStopTurnCommand:
                    await controller!.StopTurnAsync(
                        request.ClientId,
                        request.CommandId,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadInterruptAgentCommand interrupt:
                    if (await controller!.InterruptAgentAsync(
                        interrupt.ActivityId,
                        request.ClientId,
                        request.CommandId,
                        cancellationToken).ConfigureAwait(false))
                        await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadRestartRuntimeCommand:
                    await controller!.RestartRuntimeAsync(cancellationToken).ConfigureAwait(false);
                    await _database.UpdateReceiptStateAsync(
                        request.ClientId,
                        request.CommandId,
                        CommandReceiptState.Completed,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadRevertCheckpointCommand revert:
                    await controller!.RevertToCheckpointAsync(revert.TurnCount, cancellationToken)
                        .ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadUpdatePiConfigurationCommand updateConfiguration:
                    await controller!.UpdatePiConfigurationAsync(
                        updateConfiguration.ExpectedRevision,
                        updateConfiguration.Model,
                        updateConfiguration.ThinkingLevel,
                        updateConfiguration.RuntimeModeId,
                        cancellationToken).ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadRenameCommand rename:
                    var renamedThread = await UpdateThreadMetadataAsync(
                        request.ThreadId,
                        rename.ExpectedRevision,
                        ThreadMetadataValidation.NormalizeTitle(rename.Title),
                        null,
                        null,
                        cancellationToken).ConfigureAwait(false);
                    await _database.SetThreadTitleKindAsync(
                        request.ThreadId,
                        ThreadTitleKind.Manual,
                        cancellationToken).ConfigureAwait(false);
                    if (_threads.TryGetController(request.ThreadId, out var activeController))
                    {
                        await activeController!.ApplyRenamedThreadAsync(renamedThread, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadSetArchivedCommand archive:
                    await UpdateThreadMetadataAsync(
                        request.ThreadId,
                        archive.ExpectedRevision,
                        null,
                        archive.IsArchived,
                        null,
                        cancellationToken).ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadSetPinnedCommand pin:
                    await UpdateThreadMetadataAsync(
                        request.ThreadId,
                        pin.ExpectedRevision,
                        null,
                        null,
                        pin.IsPinned,
                        cancellationToken).ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadSetSettledCommand settled:
                    await UpdateThreadInboxAsync(
                        request.ThreadId,
                        settled.ExpectedRevision,
                        isSettled: settled.IsSettled,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadSetSnoozedCommand snoozed:
                    if (snoozed.SnoozedUntilUtc is { } until && until <= DateTimeOffset.UtcNow)
                    {
                        throw new HostOperationException(
                            ProtocolErrorCodes.ThreadInvalid,
                            "A snooze time must be in the future; use unsnooze to clear it.");
                    }
                    await UpdateThreadInboxAsync(
                        request.ThreadId,
                        snoozed.ExpectedRevision,
                        snoozedUntilUtc: snoozed.SnoozedUntilUtc,
                        updateSnooze: true,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadSetPinnedOrderCommand pinnedOrder:
                    if (pinnedOrder.PinnedOrder < 0)
                    {
                        throw new HostOperationException(ProtocolErrorCodes.ThreadInvalid, "Pinned order cannot be negative.");
                    }
                    await UpdateThreadInboxAsync(
                        request.ThreadId,
                        pinnedOrder.ExpectedRevision,
                        pinnedOrder: pinnedOrder.PinnedOrder,
                        updatePinnedOrder: true,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadRegenerateTitleCommand regenerate:
                    await controller!.RegenerateTitleAsync(regenerate.ExpectedRevision, cancellationToken)
                        .ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadCompactContextCommand compact:
                    await controller!.CompactContextAsync(compact.CustomInstructions, cancellationToken)
                        .ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadRespondToApprovalCommand response:
                    await controller!.RespondToApprovalAsync(
                        response.InteractionId,
                        response.Decision,
                        cancellationToken).ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadAnswerQuestionCommand answer:
                    ArgumentNullException.ThrowIfNull(answer.Answer);
                    await controller!.AnswerQuestionAsync(
                        answer.InteractionId,
                        answer.Answer,
                        cancellationToken).ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadCancelInteractionCommand cancel:
                    await controller!.CancelInteractionAsync(cancel.InteractionId, cancellationToken)
                        .ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadAddDraftAttachmentCommand add:
                    if (!allowAttachmentAdd)
                    {
                        throw new HostOperationException(
                            ProtocolErrorCodes.AttachmentInvalid,
                            "Attachment content must be uploaded through the authenticated upload endpoint.");
                    }

                    ValidateAttachmentMetadata(add);
                    var addition = await _database.AddDraftAttachmentAsync(
                        request.ThreadId,
                        add.DraftId,
                        add.ExpectedRevision,
                        new DraftAttachment(
                            _environment.EnvironmentId,
                            request.ThreadId,
                            add.DraftId,
                            add.AttachmentId,
                            add.FileName,
                            add.MediaType,
                            add.ByteLength,
                            add.Sha256,
                            add.ServerPath,
                            DateTimeOffset.UtcNow),
                        _options.MaximumAttachmentsPerDraft,
                        cancellationToken).ConfigureAwait(false);
                    ThrowForAttachmentMutation(addition, add.ExpectedRevision);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadRemoveDraftAttachmentCommand remove:
                    if (remove.ExpectedRevision < 0)
                    {
                        throw new HostOperationException(
                            ProtocolErrorCodes.DraftConflict,
                            "A draft revision cannot be negative.");
                    }

                    var removal = await _database.RemoveDraftAttachmentAsync(
                        request.ThreadId,
                        remove.DraftId,
                        remove.AttachmentId,
                        remove.ExpectedRevision,
                        cancellationToken).ConfigureAwait(false);
                    ThrowForAttachmentMutation(removal, remove.ExpectedRevision);
                    if (removal.RemovedAttachment is not null)
                    {
                        await DeleteUnreferencedAttachmentAsync(removal.RemovedAttachment).ConfigureAwait(false);
                    }

                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadSaveDraftCommand save:
                    ArgumentNullException.ThrowIfNull(save.Text);
                    if (save.ExpectedRevision < 0)
                    {
                        throw new HostOperationException(
                            ProtocolErrorCodes.DraftConflict,
                            "A draft revision cannot be negative.");
                    }

                    var update = await _database.UpdateThreadDraftAsync(
                        request.ThreadId,
                        save.DraftId,
                        save.ExpectedRevision,
                        save.Text,
                        save.Context,
                        cancellationToken).ConfigureAwait(false);
                    if (update.Draft is null || update.Draft.DraftId != save.DraftId)
                    {
                        throw new HostOperationException(
                            ProtocolErrorCodes.DraftNotFound,
                            "The draft identity is not valid for this thread.");
                    }

                    if (!update.WasUpdated)
                    {
                        throw new HostOperationException(
                            ProtocolErrorCodes.DraftConflict,
                            $"The draft changed from expected revision {save.ExpectedRevision} to " +
                            $"revision {update.Draft.Revision}; reload it before saving.");
                    }

                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadRestoreStashCommand restore:
                    var stash = await _database.GetPromptStashAsync(restore.StashId, cancellationToken).ConfigureAwait(false)
                        ?? throw new HostOperationException(ProtocolErrorCodes.PromptStashNotFound, "The stash is no longer available.");
                    foreach (var attachment in stash.Attachments ?? [])
                        await _attachmentStorage.ValidateForPromptAsync(attachment, cancellationToken).ConfigureAwait(false);
                    await _database.RestorePromptStashAsync(request.ThreadId, restore.DraftId, restore.ExpectedRevision, restore.StashId, cancellationToken).ConfigureAwait(false);
                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case ThreadClearDraftCommand clear:
                    ArgumentNullException.ThrowIfNull(clear.ExpectedAttachmentIds);
                    if (clear.ExpectedRevision < 0)
                    {
                        throw new HostOperationException(
                            ProtocolErrorCodes.DraftConflict,
                            "A draft revision cannot be negative.");
                    }

                    var cleared = await _database.ClearThreadDraftAsync(
                        request.ThreadId,
                        clear.DraftId,
                        clear.ExpectedRevision,
                        clear.ExpectedAttachmentIds,
                        cancellationToken).ConfigureAwait(false);
                    ThrowForDraftClear(cleared, clear.ExpectedRevision);
                    foreach (var attachment in cleared.RemovedAttachments)
                    {
                        await DeleteUnreferencedAttachmentAsync(attachment).ConfigureAwait(false);
                    }

                    await CompleteReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new HostOperationException(
                        ProtocolErrorCodes.ProtocolIncompatible,
                        $"Unsupported thread command '{request.Command.GetType().Name}'.");
            }
        }
        catch (HostOperationException exception)
        {
            await _database.UpdateReceiptStateAsync(
                request.ClientId,
                request.CommandId,
                CommandReceiptState.Rejected,
                exception.Code,
                cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is PiRpcException or TimeoutException or InvalidOperationException)
        {
            var errorCode = exception is PiRpcTimeoutException
                ? ProtocolErrorCodes.PiCommandTimedOut
                : ProtocolErrorCodes.PiCommandRejected;
            await _database.UpdateReceiptStateAsync(
                request.ClientId,
                request.CommandId,
                CommandReceiptState.Failed,
                errorCode,
                cancellationToken).ConfigureAwait(false);
            throw new HostOperationException(errorCode, exception.Message);
        }
        catch (OperationCanceledException)
        {
            await _database.UpdateReceiptStateAsync(
                request.ClientId,
                request.CommandId,
                CommandReceiptState.DispatchUncertain,
                ProtocolErrorCodes.DispatchUncertain,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await _database.UpdateReceiptStateAsync(
                request.ClientId,
                request.CommandId,
                CommandReceiptState.Failed,
                ProtocolErrorCodes.PiLaunchFailed,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw new HostOperationException(ProtocolErrorCodes.PiLaunchFailed, exception.Message);
        }

        return (await _database.GetReceiptAsync(request.ClientId, request.CommandId, cancellationToken)
            .ConfigureAwait(false))!.Receipt;
    }

    public async Task<CommandReceipt?> GetCommandReceiptAsync(
        ClientId clientId,
        CommandId commandId,
        CancellationToken cancellationToken = default) =>
        (await _database.GetReceiptAsync(clientId, commandId, cancellationToken).ConfigureAwait(false))?.Receipt;

    public async IAsyncEnumerable<ThreadEnvelope> SubscribeThreadAsync(
        ThreadId threadId,
        ThreadCursor? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var controller = await _threads.GetAsync(threadId, cancellationToken).ConfigureAwait(false);
        await controller.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var envelope in controller.SubscribeAsync(cursor, cancellationToken).ConfigureAwait(false))
        {
            yield return envelope;
        }
    }

    public async IAsyncEnumerable<ThreadEnvelope> SubscribeThreadPassiveAsync(
        ThreadId threadId,
        ThreadCursor? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var controller = await _threads.GetAsync(threadId, cancellationToken).ConfigureAwait(false);
        await controller.HydratePersistedSessionAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var envelope in controller.SubscribeAsync(cursor, cancellationToken).ConfigureAwait(false))
        {
            yield return envelope;
        }
    }

    public async IAsyncEnumerable<TerminalEnvelope> SubscribeTerminalAsync(
        TerminalSessionId terminalSessionId,
        TerminalCursor? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var envelope in _terminals.SubscribeAsync(
                           terminalSessionId,
                           cursor,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return envelope;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _settlementShutdown.CancelAsync().ConfigureAwait(false);
        if (_settlementWorker is not null) await _settlementWorker.ConfigureAwait(false);
        Updates.Dispose();
        Task[] catalogReaders;
        lock (_catalogGate)
        {
            if (_catalogDisposed) return;
            _catalogDisposed = true;
            catalogReaders = _catalogReaders.Select(reader => reader.Task).ToArray();
        }
        await _catalogStopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(catalogReaders).ConfigureAwait(false);
        _catalogStopping.Dispose();
        _preview.Dispose();
        PreviewLeases.Dispose();
        await BrowserAutomation.DisposeAsync().ConfigureAwait(false);
        await _terminals.DisposeAsync().ConfigureAwait(false);
        await _threads.DisposeAsync().ConfigureAwait(false);
        _sourceControl.Dispose();
    }

    private void ValidateRequest(ExecuteThreadCommandRequest request)
    {
        if (request.ProtocolVersion != ProtocolVersion.Current)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ProtocolIncompatible,
                $"Protocol {request.ProtocolVersion} is unsupported; expected {ProtocolVersion.Current}.");
        }

        if (request.EnvironmentId != _environment.EnvironmentId)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ProtocolIncompatible,
                "The command targets a different environment.");
        }
    }

    private void ValidateUploadRequest(UploadDraftAttachmentRequest request)
    {
        if (request.ProtocolVersion != ProtocolVersion.Current)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ProtocolIncompatible,
                $"Protocol {request.ProtocolVersion} is unsupported; expected {ProtocolVersion.Current}.");
        }

        if (request.EnvironmentId != _environment.EnvironmentId)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ProtocolIncompatible,
                "The upload targets a different environment.");
        }

        if (request.ExpectedDraftRevision < 0)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.DraftConflict,
                "A draft revision cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(request.ClientId.Value) ||
            string.IsNullOrWhiteSpace(request.CommandId.Value) ||
            string.IsNullOrWhiteSpace(request.ThreadId.Value) ||
            string.IsNullOrWhiteSpace(request.DraftId.Value) ||
            string.IsNullOrWhiteSpace(request.AttachmentId.Value))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.AttachmentInvalid,
                "The upload metadata contains a missing identifier.");
        }
    }

    private static void ValidateAttachmentMetadata(ThreadAddDraftAttachmentCommand attachment)
    {
        if (attachment.ExpectedRevision < 0 ||
            attachment.ByteLength < 0 ||
            string.IsNullOrWhiteSpace(attachment.FileName) ||
            string.IsNullOrWhiteSpace(attachment.MediaType) ||
            attachment.Sha256.Length != 64 ||
            string.IsNullOrWhiteSpace(attachment.ServerPath))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.AttachmentInvalid,
                "The stored attachment metadata is invalid.");
        }
    }

    private async Task<IReadOnlyList<PiPromptAttachment>> ResolveTurnAttachmentsAsync(
        ThreadId threadId,
        string prompt,
        DraftId? draftId,
        long? draftRevision,
        IReadOnlyList<AttachmentId>? requestedAttachmentIds,
        CancellationToken cancellationToken)
    {
        var attachmentIds = requestedAttachmentIds ?? [];
        if (attachmentIds.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                throw new HostOperationException(
                    ProtocolErrorCodes.PiCommandRejected,
                    "A turn must contain text or at least one attachment.");
            }

            return [];
        }

        if (attachmentIds.Count > _options.MaximumAttachmentsPerDraft ||
            attachmentIds.Distinct().Count() != attachmentIds.Count)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.AttachmentInvalid,
                "The turn contains too many attachments or repeats an attachment ID.");
        }

        if (draftId is null || draftRevision is null || draftRevision < 0)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.AttachmentInvalid,
                "Attachment turns must identify the exact draft revision being sent.");
        }

        var draft = await GetThreadDraftAsync(threadId, cancellationToken).ConfigureAwait(false);
        if (draft.DraftId != draftId)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.DraftNotFound,
                "The attachment turn targets a different draft.");
        }

        if (draft.Revision != draftRevision)
        {
            throw DraftConflict(draftRevision.Value, draft.Revision);
        }

        var byId = draft.Attachments.ToDictionary(static attachment => attachment.AttachmentId);
        var resolved = new List<PiPromptAttachment>(attachmentIds.Count);
        foreach (var attachmentId in attachmentIds)
        {
            if (!byId.TryGetValue(attachmentId, out var attachment))
            {
                throw new HostOperationException(
                    ProtocolErrorCodes.AttachmentNotFound,
                    $"Attachment '{attachmentId}' is not associated with this draft.");
            }

            await _attachmentStorage.ValidateForPromptAsync(attachment, cancellationToken).ConfigureAwait(false);
            resolved.Add(new PiPromptAttachment(
                attachment.AttachmentId.Value,
                attachment.FileName,
                attachment.MediaType,
                attachment.ServerPath,
                attachment.Sha256));
        }

        return resolved;
    }

    private async Task DeleteUnreferencedAttachmentAsync(DraftAttachment attachment)
    {
        if (!await _database.IsAttachmentReferencedAsync(attachment.ServerPath, CancellationToken.None).ConfigureAwait(false))
            _attachmentStorage.Delete(attachment);
    }

    private static void ThrowForAttachmentMutation(
        DraftAttachmentMutationResult result,
        long expectedRevision)
    {
        switch (result.State)
        {
            case DraftAttachmentMutationState.Updated:
                return;
            case DraftAttachmentMutationState.DraftNotFound:
                throw new HostOperationException(
                    ProtocolErrorCodes.DraftNotFound,
                    "The draft identity is not valid for this thread.");
            case DraftAttachmentMutationState.DraftConflict:
                throw DraftConflict(expectedRevision, result.Draft?.Revision);
            case DraftAttachmentMutationState.AttachmentConflict:
                throw new HostOperationException(
                    ProtocolErrorCodes.AttachmentConflict,
                    "The attachment ID is already in use.");
            case DraftAttachmentMutationState.AttachmentLimitExceeded:
                throw new HostOperationException(
                    ProtocolErrorCodes.AttachmentLimitExceeded,
                    "The draft already contains the maximum number of attachments.");
            case DraftAttachmentMutationState.AttachmentNotFound:
                throw new HostOperationException(
                    ProtocolErrorCodes.AttachmentNotFound,
                    "The attachment is not associated with this draft.");
            default:
                throw new ArgumentOutOfRangeException(nameof(result));
        }
    }

    private static void ThrowForDraftClear(DraftClearResult result, long expectedRevision)
    {
        switch (result.State)
        {
            case DraftAttachmentMutationState.Updated:
                return;
            case DraftAttachmentMutationState.DraftNotFound:
                throw new HostOperationException(
                    ProtocolErrorCodes.DraftNotFound,
                    "The draft identity is not valid for this thread.");
            case DraftAttachmentMutationState.DraftConflict:
                throw DraftConflict(expectedRevision, result.Draft?.Revision);
            default:
                throw new HostOperationException(
                    ProtocolErrorCodes.DraftConflict,
                    "The draft could not be cleared because its attachments changed.");
        }
    }

    private static HostOperationException DraftConflict(long expectedRevision, long? actualRevision) => new(
        ProtocolErrorCodes.DraftConflict,
        $"The draft changed from expected revision {expectedRevision} to " +
        $"revision {actualRevision?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; " +
        "reload it before changing attachments.");

    private async Task<HostThreadRecord> UpdateThreadMetadataAsync(
        ThreadId threadId,
        long expectedRevision,
        string? title,
        bool? isArchived,
        bool? isPinned,
        CancellationToken cancellationToken)
    {
        if (expectedRevision < 0)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ThreadConflict,
                "A thread metadata revision cannot be negative.");
        }

        var result = await _database.UpdateThreadMetadataAsync(
            threadId,
            expectedRevision,
            title,
            isArchived,
            isPinned,
            cancellationToken).ConfigureAwait(false);
        if (result.Thread is null)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ThreadNotFound,
                $"Thread '{threadId}' was not found.");
        }

        if (!result.WasUpdated)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ThreadConflict,
                $"The thread metadata changed from expected revision {expectedRevision} to " +
                $"revision {result.Thread.Revision}; reload it before updating it.");
        }

        return result.Thread;
    }

    private async Task UpdateThreadInboxAsync(
        ThreadId threadId,
        long expectedRevision,
        bool? isSettled = null,
        DateTimeOffset? snoozedUntilUtc = null,
        bool updateSnooze = false,
        long? pinnedOrder = null,
        bool updatePinnedOrder = false,
        CancellationToken cancellationToken = default)
    {
        if (expectedRevision < 0)
        {
            throw new HostOperationException(ProtocolErrorCodes.ThreadConflict, "A thread metadata revision cannot be negative.");
        }
        var result = await _database.UpdateThreadInboxAsync(
            threadId,
            expectedRevision,
            isSettled,
            snoozedUntilUtc,
            updateSnooze,
            pinnedOrder,
            updatePinnedOrder,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Thread is null)
        {
            throw new HostOperationException(ProtocolErrorCodes.ThreadNotFound, $"Thread '{threadId}' was not found.");
        }
        if (!result.WasUpdated)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ThreadConflict,
                $"The thread metadata changed from expected revision {expectedRevision} to revision {result.Thread.Revision}; reload it before updating it.");
        }
    }

    private static void ValidateExpectations(
        ExecuteThreadCommandRequest request,
        PiThreadController controller)
    {
        var projection = controller.Journal.Projection;
        if (request.ExpectedProjectionEpoch is not null &&
            request.ExpectedProjectionEpoch.Value != projection.ProjectionEpoch)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ResyncRequired,
                "The thread projection epoch changed; resubscribe before mutating the thread.");
        }

        if (request.ExpectedTurnId is not null && request.ExpectedTurnId != projection.CurrentTurnId)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ThreadBusy,
                "The expected turn is no longer active.");
        }
    }

    private static string ComputeBodyHash(ExecuteThreadCommandRequest request)
    {
        var commandBytes = JsonSerializer.SerializeToUtf8Bytes(
            request.Command,
            ProtocolJsonContext.Default.ThreadCommand);
        var prefix = Encoding.UTF8.GetBytes(string.Join(
            "|",
            request.ProtocolVersion,
            request.EnvironmentId.Value,
            request.ThreadId.Value,
            request.ExpectedProjectionEpoch?.Value ?? string.Empty,
            request.ExpectedTurnId?.Value ?? string.Empty));
        var body = new byte[prefix.Length + 1 + commandBytes.Length];
        prefix.CopyTo(body, 0);
        body[prefix.Length] = 0;
        commandBytes.CopyTo(body, prefix.Length + 1);
        return Convert.ToHexString(SHA256.HashData(body));
    }

    private Task<CommandReceipt> CompleteReceiptAsync(
        ExecuteThreadCommandRequest request,
        CancellationToken cancellationToken) => _database.UpdateReceiptStateAsync(
        request.ClientId,
        request.CommandId,
        CommandReceiptState.Completed,
        cancellationToken: cancellationToken);
}
