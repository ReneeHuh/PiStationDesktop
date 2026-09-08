using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PiStation.ClientRuntime;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Receipts;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private IEnvironmentClient? _client;
    private ThreadSubscription? _subscription;
    private readonly ConcurrentDictionary<TerminalSessionId, TerminalSubscription> _terminalSubscriptions = new();
    private readonly SemaphoreSlim _terminalSubscriptionGate = new(1, 1);
    private bool _commandPending;
    private CancellationTokenSource? _piConfigurationLoadCancellation;
    private CancellationTokenSource? _fileMentionSearchCancellation;
    private CancellationTokenSource? _workbenchFileSearchCancellation;
    private CancellationTokenSource? _workbenchFileReadCancellation;
    private CancellationTokenSource? _workbenchChangesLoadCancellation;
    private CancellationTokenSource? _workbenchDiffLoadCancellation;
    private CancellationTokenSource? _workbenchPreviewDiscoveryCancellation;
    private CancellationTokenSource? _workbenchTerminalCancellation;
    private readonly ConcurrentDictionary<TerminalSessionId, CancellationTokenSource> _workbenchTerminalResizeCancellations = new();
    private readonly SemaphoreSlim _workbenchTerminalOperationGate = new(1, 1);
    private ProjectId? _fileMentionProjectId;
    private string? _fileMentionQuery;
    private CancellationTokenSource? _threadSearchCancellation;
    private long _threadSearchVersion;
    private readonly bool _uiTestFaultControlsEnabled;
    private readonly string _previewCaptureRoot;
    private readonly EditingRecoveryStore? _editingRecovery;

    public ShellViewModel(
        DispatcherQueue dispatcherQueue,
        bool enableUiTestFaultControls = false,
        string? layoutSettingsPath = null,
        string? previewCaptureRoot = null)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _uiTestFaultControlsEnabled = enableUiTestFaultControls;
        _previewCaptureRoot = Path.GetFullPath(previewCaptureRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PiStationDesktop",
            "preview-captures"));
        Workspace = new WorkspaceViewModel();
        Layout = new ShellLayoutViewModel(layoutSettingsPath);
        PiConfiguration = new PiConfigurationViewModel();
        Connection = new ConnectionViewModel();
        FileMentions = new FileMentionViewModel();
        _editingRecovery = layoutSettingsPath is null ? null : new EditingRecoveryStore(
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(layoutSettingsPath))!, "editing-recovery"));
        WorkbenchFiles = new WorkbenchFilesViewModel(_editingRecovery);
        WorkbenchChanges = new WorkbenchChangesViewModel();
        WorkbenchTerminal = new WorkbenchTerminalViewModel();
        WorkbenchPreview = new WorkbenchPreviewViewModel();
        Composer = new ComposerViewModel(
            _dispatcherQueue,
            LoadDraftAsync,
            SaveDraftAsync,
            UploadDraftAttachmentAsync,
            RemoveDraftAttachmentAsync,
            ClearDraftAsync,
            _editingRecovery);
        Composer.PropertyChanged += OnComposerPropertyChanged;
        Composer.SaveFailed += OnComposerSaveFailed;
    }

    public ThreadViewModel Thread { get; } = new();

    public bool IsRemote { get; private set; }
    public string EnvironmentLabel { get; private set; } = "Local";
    private bool _runtimeStopped;
    public bool CanOperate => !_runtimeStopped && (!IsRemote || _client?.Descriptor?.Capabilities.Contains("thread.operate") == true);
    public bool IsReadOnly => !CanOperate;

    private PiStation.ClientRuntime.Ssh.SshConnectionProfile? _remoteEditorProfile;

    public void ConfigureRemote(string name, PiStation.ClientRuntime.Ssh.SshConnectionProfile? editorProfile = null)
    {
        IsRemote = true;
        _remoteEditorProfile = editorProfile;
        EnvironmentLabel = $"{name} (Remote)";
        Connection.Status = $"{EnvironmentLabel} • Disconnected";
        WorkbenchChanges.AllowOperations = false;
        WorkbenchTerminal.AllowOperations = false;
    }

    public ComposerViewModel Composer { get; }

    public WorkspaceViewModel Workspace { get; }

    public ShellLayoutViewModel Layout { get; }

    public PiConfigurationViewModel PiConfiguration { get; }

    public ConnectionViewModel Connection { get; }

    public FileMentionViewModel FileMentions { get; }

    public WorkbenchFilesViewModel WorkbenchFiles { get; }

    public WorkbenchChangesViewModel WorkbenchChanges { get; }

    public WorkbenchTerminalViewModel WorkbenchTerminal { get; }

    public WorkbenchPreviewViewModel WorkbenchPreview { get; }

    internal string PreviewCaptureRoot => _previewCaptureRoot;

    public Visibility UiTestFaultControlsVisibility =>
        _uiTestFaultControlsEnabled ? Visibility.Visible : Visibility.Collapsed;

    private ObservableCollection<ProjectDescriptor> Projects => Workspace.Projects;

    private ObservableCollection<ThreadDescriptor> Threads => Workspace.Threads;

    private ObservableCollection<ProjectFileMatch> FileMentionSuggestions => FileMentions.Suggestions;

    private string ThreadSearchQuery
    {
        get => Workspace.ThreadSearchQuery;
        set => Workspace.ThreadSearchQuery = value;
    }

    private bool IsShowingArchivedThreads
    {
        get => Workspace.IsShowingArchivedThreads;
        set => Workspace.IsShowingArchivedThreads = value;
    }

    private string ThreadListStatus
    {
        get => Workspace.ThreadListStatus;
        set => Workspace.ThreadListStatus = value;
    }

    private string ThreadLifecycleStatus
    {
        get => Workspace.ThreadLifecycleStatus;
        set => Workspace.ThreadLifecycleStatus = value;
    }

    private string FileMentionStatus
    {
        get => FileMentions.Status;
        set => FileMentions.Status = value;
    }

    private int SelectedFileMentionIndex
    {
        get => FileMentions.SelectedIndex;
        set => FileMentions.SelectedIndex = value;
    }

    private ProjectDescriptor? SelectedProject
    {
        get => Workspace.SelectedProject;
        set
        {
            if (!Equals(Workspace.SelectedProject, value))
            {
                Workspace.SelectedProject = value;
                ResetWorkbenchFiles(value);
                ResetWorkbenchChanges(value);
                ResetWorkbenchTerminal(value);
                ResetWorkbenchPreview(value);
                OnPropertyChanged(nameof(CanManageThreads));
            }
        }
    }

    private ThreadDescriptor? SelectedThread
    {
        get => Workspace.SelectedThread;
        set
        {
            var previousThreadId = Workspace.SelectedThread?.ThreadId;
            if (!Equals(Workspace.SelectedThread, value))
            {
                Workspace.SelectedThread = value;
                if (previousThreadId != value?.ThreadId)
                {
                    ClearPiConfiguration(value is null ? string.Empty : "Loading Pi settings…");
                    ResetWorkbenchFiles(SelectedProject);
                    ResetWorkbenchChanges(SelectedProject);
                    ResetWorkbenchTerminal(SelectedProject);
                    ResetWorkbenchPreview(SelectedProject);
                }

                RaiseCommandStateChanged();
            }
        }
    }

    public string PromptText
    {
        get => Composer.Text;
        set => Composer.Text = value;
    }

    public bool CanManageThreads =>
        CanOperate &&
        Workspace.SelectedProject is not null &&
        _client?.ConnectionState == EnvironmentConnectionState.Connected &&
        !_commandPending;

    public bool IsConnected => _client?.ConnectionState == EnvironmentConnectionState.Connected;

    public bool CanAddProject =>
        CanOperate &&
        _client?.ConnectionState == EnvironmentConnectionState.Connected &&
        !_commandPending;

    public bool IsComposerReadOnly => IsReadOnly || Composer.HasRecoveryConflict || !Composer.HasDraft;

    public bool CanSend =>
        CanOperate &&
        !Composer.HasRecoveryConflict &&
        Composer.HasDraft &&
        Workspace.SelectedThread is not null &&
        Thread.Projection?.RuntimeState == ThreadRuntimeState.Ready &&
        _client?.ConnectionState == EnvironmentConnectionState.Connected &&
        !_commandPending &&
        (Composer.HasAttachments || !string.IsNullOrWhiteSpace(PromptText));

    public bool CanStop =>
        CanOperate &&
        Workspace.SelectedThread is not null &&
        Thread.Projection?.RuntimeState == ThreadRuntimeState.Running &&
        _client?.ConnectionState == EnvironmentConnectionState.Connected &&
        !_commandPending;

    public bool CanAttachFiles =>
        CanOperate &&
        Workspace.SelectedThread is not null &&
        Thread.Projection?.RuntimeState == ThreadRuntimeState.Ready &&
        _client?.ConnectionState == EnvironmentConnectionState.Connected &&
        !_commandPending &&
        !Connection.HasUncertainCommand &&
        Composer.CanAttach;

    public bool CanRestartPi =>
        CanOperate &&
        Workspace.SelectedThread is not null &&
        Thread.Projection?.RuntimeState == ThreadRuntimeState.Crashed &&
        _client?.ConnectionState == EnvironmentConnectionState.Connected &&
        !_commandPending;

    public bool CanConfigurePi =>
        CanOperate &&
        Workspace.SelectedThread is not null &&
        PiConfiguration.Snapshot?.Configuration.ThreadId == Workspace.SelectedThread.ThreadId &&
        Thread.Projection?.RuntimeState == ThreadRuntimeState.Ready &&
        _client?.ConnectionState == EnvironmentConnectionState.Connected &&
        !_commandPending &&
        !PiConfiguration.IsPending &&
        !Connection.HasUncertainCommand;

    public void Attach(IEnvironmentClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (_client is not null)
        {
            throw new InvalidOperationException("A client is already attached.");
        }

        _client = client;
        _client.ConnectionStateChanged += OnConnectionStateChanged;
        if (_client.Catalog is { } catalog) catalog.Changed += OnCatalogChanged;
        _client.PiConfigurations.Changed += OnPiConfigurationChanged;
    }

    public async Task LoadProjectsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var projects = await RequireClient().ListProjectsAsync(cancellationToken).ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                Replace(Projects, projects);
                ClearError();
            });
        }
        catch (Exception exception)
        {
            ReportRuntimeError(exception);
        }
    }

    public async Task AddProjectAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            ReportRuntimeError("Enter an absolute project path.");
            return;
        }

        try
        {
            var project = await RequireClient()
                .AddProjectAsync(new AddProjectRequest(path), cancellationToken)
                .ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                if (Projects.All(item => item.ProjectId != project.ProjectId))
                {
                    Projects.Add(project);
                }

                ClearError();
            });
            await SelectProjectAsync(project, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportRuntimeError(exception);
        }
    }

    private readonly SemaphoreSlim _selectionGate = new(1, 1);
    private bool _selectionClosed;

    public async Task SelectProjectAsync(ProjectDescriptor? project, CancellationToken cancellationToken = default)
    {
        await _selectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { if (!_selectionClosed) await SelectProjectCoreAsync(project, cancellationToken).ConfigureAwait(false); }
        finally { _selectionGate.Release(); }
    }

    private async Task SelectProjectCoreAsync(
        ProjectDescriptor? project,
        CancellationToken cancellationToken = default)
    {
        CloseFileMentionSuggestions();
        CancelThreadSearch();
        CancelPiConfigurationLoad();
        try
        {
            await Composer.SelectThreadAsync(null, cancellationToken).ConfigureAwait(false);
            if (_subscription is { } previous)
            {
                _subscription = null;
                previous.Store.Changed -= OnProjectionChanged;
                previous.Store.SynchronizationChanged -= OnThreadSynchronizationChanged;
                await previous.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            ReportRuntimeError(exception);
            return;
        }

        RunOnUiThread(() =>
        {
            SelectedProject = project;
            SelectedThread = null;
            ThreadSearchQuery = string.Empty;
            IsShowingArchivedThreads = false;
            ThreadLifecycleStatus = string.Empty;
            Replace(Threads, []);
            ThreadListStatus = project is null
                ? "Select a workspace to see its threads"
                : "Loading threads…";
            Thread.Clear(hasSelectedThread: false);
            RaiseCommandStateChanged();
        });
        if (project is null)
        {
            return;
        }

        try
        {
            var threads = await RequireClient().ListThreadsAsync(project.ProjectId, cancellationToken)
                .ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                Replace(Threads, threads);
                ThreadListStatus = threads.Count == 0 ? "No threads yet" : string.Empty;
                ClearError();
            });
        }
        catch (Exception exception)
        {
            RunOnUiThread(() => ThreadListStatus = "Threads are unavailable");
            ReportRuntimeError(exception);
        }
    }

    public async Task CreateThreadAsync(CancellationToken cancellationToken = default)
    {
        await CreateThreadInWorkspaceAsync(null, startFromOrigin: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateThreadInWorkspaceAsync(
        ThreadWorkspaceMode? workspaceMode,
        bool startFromOrigin = false,
        CancellationToken cancellationToken = default)
    {
        var project = SelectedProject;
        if (project is null)
        {
            return;
        }

        try
        {
            var client = RequireClient();
            var thread = await client
                .CreateThreadAsync(
                    new CreateThreadRequest(
                        project.ProjectId,
                        WorkspaceMode: workspaceMode,
                        StartFromOrigin: startFromOrigin),
                    cancellationToken)
                .ConfigureAwait(false);
            CancelThreadSearch();
            RunOnUiThread(() =>
            {
                ThreadSearchQuery = string.Empty;
                IsShowingArchivedThreads = false;
                Replace(Threads, client.ThreadMetadata.GetProjectThreads(project.ProjectId));
                ThreadListStatus = string.Empty;
                ThreadLifecycleStatus = string.IsNullOrWhiteSpace(thread.SetupScriptMessage)
                    ? $"Created {thread.Title}"
                    : $"Created {thread.Title} • {thread.SetupScriptMessage}";
                ClearError();
            });
            await SelectThreadAsync(thread, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportRuntimeError(exception);
        }
    }

    public async Task TrustSelectedProjectScriptsAsync(CancellationToken cancellationToken = default)
    {
        var project = SelectedProject;
        if (project is null || project.AreRepositoryScriptsTrusted)
        {
            return;
        }

        var updated = await RequireClient().SetProjectScriptsTrustAsync(
            new SetProjectScriptsTrustRequest(project.ProjectId, IsTrusted: true),
            cancellationToken).ConfigureAwait(false);
        RunOnUiThread(() =>
        {
            var index = Projects.ToList().FindIndex(candidate => candidate.ProjectId == updated.ProjectId);
            if (index >= 0)
            {
                Projects[index] = updated;
            }

            SelectedProject = updated;
        });
    }

    public async Task SelectThreadAsync(ThreadDescriptor? thread, CancellationToken cancellationToken = default)
    {
        await _selectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { if (!_selectionClosed) await SelectThreadCoreAsync(thread, cancellationToken).ConfigureAwait(false); }
        finally { _selectionGate.Release(); }
    }

    private async Task SelectThreadCoreAsync(
        ThreadDescriptor? thread,
        CancellationToken cancellationToken = default)
    {
        CloseFileMentionSuggestions();
        CancelPiConfigurationLoad();
        try
        {
            await Composer.SelectThreadAsync(thread?.ThreadId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportRuntimeError(exception);
            return;
        }

        RunOnUiThread(() => SelectedThread = thread);
        if (_subscription is not null)
        {
            _subscription.Store.Changed -= OnProjectionChanged;
            _subscription.Store.SynchronizationChanged -= OnThreadSynchronizationChanged;
            await _subscription.DisposeAsync().ConfigureAwait(false);
            _subscription = null;
        }

        if (thread is null)
        {
            RunOnUiThread(() =>
            {
                Thread.Clear(hasSelectedThread: false);
                RaiseCommandStateChanged();
            });
            return;
        }

        try
        {
            _subscription = RequireClient().SubscribeThread(thread.ThreadId);
            _subscription.Store.Changed += OnProjectionChanged;
            _subscription.Store.SynchronizationChanged += OnThreadSynchronizationChanged;
            var projection = _subscription.Store.Current;
            RunOnUiThread(() => { ApplyThreadProjection(projection); UpdateThreadSynchronizationStatus(); });
        }
        catch (Exception exception)
        {
            ReportRuntimeError(exception);
        }

        await LoadPiConfigurationAsync(thread, cancellationToken).ConfigureAwait(false);
    }

    public void UpdateThreadSearchQuery(string? query)
    {
        var normalized = (query ?? string.Empty).Trim();
        if (normalized.Length > ThreadLifecycleDefaults.MaximumSearchQueryLength)
        {
            normalized = normalized[..ThreadLifecycleDefaults.MaximumSearchQueryLength];
        }

        if (string.Equals(ThreadSearchQuery, normalized, StringComparison.Ordinal))
        {
            return;
        }

        RunOnUiThread(() =>
        {
            ThreadSearchQuery = normalized;
            ThreadLifecycleStatus = string.Empty;
            ThreadListStatus = SelectedProject is null
                ? "Select a workspace to search threads"
                : "Searching threads…";
        });
        _ = QueueThreadListRefreshAsync(debounce: !string.IsNullOrEmpty(normalized));
    }

    public void ClearThreadSearch() => UpdateThreadSearchQuery(string.Empty);

    public Task<GlobalSearchResult> SearchGlobalAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var normalized = (query ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return Task.FromResult(new GlobalSearchResult([], false));
        }

        return RequireClient().SearchGlobalAsync(
            new GlobalSearchRequest(normalized),
            cancellationToken);
    }

    public async Task ActivateGlobalSearchItemAsync(
        GlobalSearchItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var project = Projects.FirstOrDefault(candidate => candidate.ProjectId == item.ProjectId);
        if (project is null)
        {
            var projects = await RequireClient().ListProjectsAsync(cancellationToken).ConfigureAwait(false);
            project = projects.FirstOrDefault(candidate => candidate.ProjectId == item.ProjectId);
        }

        if (project is null)
        {
            return;
        }

        if (SelectedProject?.ProjectId != project.ProjectId)
        {
            await SelectProjectAsync(project, cancellationToken).ConfigureAwait(false);
        }

        if (item.ThreadId is { } threadId)
        {
            var thread = await RequireClient().GetThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
            await SelectThreadAsync(thread, cancellationToken).ConfigureAwait(false);
        }

        if (item.Kind == GlobalSearchResultKind.Branch)
        {
            RunOnUiThread(() =>
            {
                Layout.IsRightPanelOpen = true;
                Layout.SelectedPanel = WorkbenchPanelKind.Changes;
            });
            await ActivateWorkbenchChangesAsync().ConfigureAwait(false);
            RunOnUiThread(() => WorkbenchChanges.SelectedBranch = WorkbenchChanges.Branches.FirstOrDefault(branch =>
                string.Equals(branch.Name, item.BranchName, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public Task ActivateWorkbenchFilesAsync()
    {
        if (SelectedProject is null)
        {
            RunOnUiThread(() => WorkbenchFiles.SwitchContext(null, hasProject: false));
            return Task.CompletedTask;
        }

        return QueueWorkbenchFileSearchAsync(debounce: false);
    }

    public Task ActivateWorkbenchChangesAsync()
    {
        if (SelectedProject is null)
        {
            RunOnUiThread(() => WorkbenchChanges.Reset(hasProject: false));
            return Task.CompletedTask;
        }

        return RefreshWorkbenchChangesAsync();
    }

    public Task ActivateWorkbenchTerminalAsync() => RefreshWorkbenchTerminalsAsync();

    public Task ActivateWorkbenchPreviewAsync()
    {
        if (SelectedProject is null)
        {
            RunOnUiThread(() => WorkbenchPreview.Reset(hasProject: false));
            return Task.CompletedTask;
        }

        return string.IsNullOrWhiteSpace(WorkbenchPreview.CurrentUrl)
            ? RefreshWorkbenchPreviewServersAsync()
            : Task.CompletedTask;
    }

    public async Task RefreshWorkbenchPreviewServersAsync(CancellationToken cancellationToken = default)
    {
        if (IsRemote && !CanOperate)
        {
            RunOnUiThread(() => WorkbenchPreview.FailDiscovery("Opening host-local previews requires operate access. You can enter a URL reachable from this computer."));
            return;
        }
        var project = SelectedProject;
        if (project is null)
        {
            CancelWorkbenchPreview();
            await RunOnUiThreadAsync(() => WorkbenchPreview.Reset(hasProject: false)).ConfigureAwait(false);
            return;
        }

        var discoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Interlocked.Exchange(ref _workbenchPreviewDiscoveryCancellation, discoveryCancellation)?.Cancel();
        RunOnUiThread(WorkbenchPreview.BeginDiscovery);
        try
        {
            var result = await RequireClient().DiscoverProjectPreviewServersAsync(
                new DiscoverProjectPreviewServersRequest(project.ProjectId, SelectedThread?.ThreadId),
                discoveryCancellation.Token).ConfigureAwait(false);
            await RunOnUiThreadAsync(() =>
            {
                if (SelectedProject?.ProjectId == project.ProjectId)
                {
                    WorkbenchPreview.ApplyDiscovery(result);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (discoveryCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RunOnUiThread(() =>
            {
                if (SelectedProject?.ProjectId == project.ProjectId)
                {
                    WorkbenchPreview.FailDiscovery(exception.Message);
                }
            });
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _workbenchPreviewDiscoveryCancellation,
                null,
                discoveryCancellation);
            discoveryCancellation.Dispose();
        }
    }

    public Uri? PrepareWorkbenchPreviewNavigation(string? address)
    {
        if (SelectedProject is not { } project ||
            !WorkbenchPreview.PrepareNavigation(address, out _, out var uri) ||
            uri is null)
        {
            return null;
        }

        PersistWorkbenchPreview();
        return uri;
    }

    internal async Task<RemotePreviewProxy?> OpenPreviewRouteAsync(Uri address)
    {
        if (!IsRemote || !address.IsLoopback) return null;
        if (!CanOperate || SelectedProject is not { } project || _client is not EnvironmentClient client)
            throw new InvalidOperationException("Host-local previews require a connected environment with operate access.");
        return await client.OpenRemotePreviewAsync(new(project.ProjectId, address)).ConfigureAwait(false);
    }

    public WorkbenchPreviewTabViewModel AddWorkbenchPreviewTab()
    {
        var tab = WorkbenchPreview.AddTab();
        PersistWorkbenchPreview();
        return tab;
    }

    public void SelectWorkbenchPreviewTab(WorkbenchPreviewTabViewModel? tab)
    {
        if (tab is not null && WorkbenchPreview.Tabs.Contains(tab))
        {
            WorkbenchPreview.ActiveTab = tab;
            PersistWorkbenchPreview();
        }
    }

    public void CloseWorkbenchPreviewTab(WorkbenchPreviewTabViewModel tab)
    {
        WorkbenchPreview.CloseTab(tab);
        PersistWorkbenchPreview();
    }

    public void SetWorkbenchPreviewViewport(int presetIndex)
    {
        WorkbenchPreview.ApplyViewportPreset(presetIndex);
        PersistWorkbenchPreview();
    }

    public void RotateWorkbenchPreviewViewport()
    {
        WorkbenchPreview.RotateViewport();
        PersistWorkbenchPreview();
    }

    public void UpdateWorkbenchPreviewAddress(string? address) =>
        WorkbenchPreview.AddressText = address ?? string.Empty;

    public void RestoreWorkbenchPreviewAddress() => WorkbenchPreview.RestoreAddressDraft();

    public void ReportWorkbenchPreviewNavigationStarted(string? tabId, Uri uri) =>
        WorkbenchPreview.ReportNavigationStarted(tabId, uri);

    public void ReportWorkbenchPreviewBrowserState(
        string? tabId,
        string? source,
        string? title,
        bool canGoBack,
        bool canGoForward)
    {
        WorkbenchPreview.ReportBrowserState(tabId, source, title, canGoBack, canGoForward);
        PersistWorkbenchPreview();
    }

    public void ReportWorkbenchPreviewNavigationCompleted(
        string? tabId,
        bool succeeded,
        string? message) =>
        WorkbenchPreview.ReportNavigationCompleted(tabId, succeeded, message);

    public void ReportWorkbenchPreviewBrowserFailure(
        string? tabId,
        PreviewFailureKind kind,
        string message) =>
        WorkbenchPreview.ReportBrowserFailure(tabId, kind, message);

    public void SetWorkbenchPreviewCaptureStatus(
        string? tabId,
        string status,
        string? capturePath = null) =>
        WorkbenchPreview.SetCaptureStatus(tabId, status, capturePath);

    public void ReturnWorkbenchPreviewToServers()
    {
        WorkbenchPreview.ReturnToServers();
        PersistWorkbenchPreview();
    }

    public async Task AddWorkbenchPreviewAnnotationAsync(
        PreviewElementAnnotation annotation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var lines = new List<string>
        {
            "<preview_annotation>",
            "Preview annotation:",
            $"Page: {EscapePreviewAnnotation(annotation.PageTitle)}",
            $"URL: {EscapePreviewAnnotation(annotation.PageUrl)}",
            $"Element: {EscapePreviewAnnotation(annotation.ElementLabel)}",
            $"Selector: {EscapePreviewAnnotation(annotation.Selector)}",
        };
        if (!string.IsNullOrWhiteSpace(annotation.Text))
        {
            lines.Add($"Text: {EscapePreviewAnnotation(annotation.Text)}");
        }

        lines.Add(
            $"Bounds: x={annotation.X:0.#}, y={annotation.Y:0.#}, width={annotation.Width:0.#}, height={annotation.Height:0.#}");
        if (!string.IsNullOrWhiteSpace(annotation.OuterHtml))
        {
            lines.Add($"HTML: {EscapePreviewAnnotation(annotation.OuterHtml)}");
        }

        if (annotation.Screenshot.Length > 0)
        {
            lines.Add("The attached screenshot shows the selected preview element.");
        }

        lines.Add("</preview_annotation>");
        var block = string.Join(Environment.NewLine, lines);
        PromptText = string.IsNullOrWhiteSpace(PromptText)
            ? block
            : PromptText.TrimEnd() + Environment.NewLine + Environment.NewLine + block;

        if (annotation.Screenshot.Length > 0 && CanAttachFiles)
        {
            await using var content = new MemoryStream(annotation.Screenshot, writable: false);
            await AddAttachmentAsync(
                $"preview-annotation-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.png",
                "image/png",
                content,
                annotation.Screenshot.LongLength,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RefreshWorkbenchTerminalsAsync(CancellationToken cancellationToken = default)
    {
        var project = SelectedProject;
        var threadId = SelectedThread?.ThreadId;
        if (project is null)
        {
            await DetachAllTerminalSubscriptionsAsync().ConfigureAwait(false);
            await RunOnUiThreadAsync(() => WorkbenchTerminal.Reset(hasProject: false)).ConfigureAwait(false);
            return;
        }

        var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Interlocked.Exchange(ref _workbenchTerminalCancellation, loadCancellation)?.Cancel();
        var selectedId = WorkbenchTerminal.SelectedSession?.Descriptor.TerminalSessionId;
        var savedLayout = Layout.GetTerminalPaneLayout(project.ProjectId.Value);
        RunOnUiThread(() =>
        {
            WorkbenchTerminal.IsBusy = true;
            WorkbenchTerminal.Status = "Loading terminal sessions…";
        });
        try
        {
            var allSessions = await RequireClient().ListTerminalSessionsAsync(
                project.ProjectId,
                loadCancellation.Token).ConfigureAwait(false);
            var sessions = FilterTerminalSessions(allSessions, threadId);
            var selected = selectedId is not null
                ? sessions.FirstOrDefault(item => item.TerminalSessionId == selectedId.Value)
                : sessions.Length > 0 ? sessions[0] : null;
            await RunOnUiThreadAsync(() =>
            {
                if (SelectedProject?.ProjectId != project.ProjectId || SelectedThread?.ThreadId != threadId)
                {
                    return;
                }

                WorkbenchTerminal.ApplySessions(sessions, selected?.TerminalSessionId);
                WorkbenchTerminal.RestoreLayout(savedLayout);
                WorkbenchTerminal.IsBusy = false;
                SaveWorkbenchTerminalLayout();
            }).ConfigureAwait(false);
            await SynchronizeTerminalSubscriptionsAsync(loadCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (loadCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RunOnUiThread(() =>
            {
                WorkbenchTerminal.IsBusy = false;
                WorkbenchTerminal.Status = $"Terminals unavailable: {exception.Message}";
            });
        }
        finally
        {
            Interlocked.CompareExchange(ref _workbenchTerminalCancellation, null, loadCancellation);
            loadCancellation.Dispose();
        }
    }

    public async Task SelectWorkbenchTerminalAsync(
        TerminalSessionItemViewModel? session,
        CancellationToken cancellationToken = default)
    {
        if (session is null)
        {
            return;
        }

        await RunOnUiThreadAsync(() =>
        {
            WorkbenchTerminal.Select(session.Descriptor.TerminalSessionId);
            SaveWorkbenchTerminalLayout();
        })
            .ConfigureAwait(false);
        await SynchronizeTerminalSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StartWorkbenchTerminalAsync(CancellationToken cancellationToken = default)
    {
        var project = SelectedProject;
        if (project is null)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            WorkbenchTerminal.IsBusy = true;
            WorkbenchTerminal.Status = "Starting terminal…";
        });
        try
        {
            var descriptor = await RequireClient().StartTerminalSessionAsync(
                new StartTerminalSessionRequest(
                    project.ProjectId,
                    WorkbenchTerminal.SelectedShell.Kind,
                    ThreadId: SelectedThread?.ThreadId),
                cancellationToken).ConfigureAwait(false);
            var sessions = FilterTerminalSessions(
                await RequireClient().ListTerminalSessionsAsync(project.ProjectId, cancellationToken)
                    .ConfigureAwait(false),
                SelectedThread?.ThreadId);
            await RunOnUiThreadAsync(() =>
            {
                WorkbenchTerminal.ApplySessions(sessions, descriptor.TerminalSessionId);
                WorkbenchTerminal.IsBusy = false;
                SaveWorkbenchTerminalLayout();
            }).ConfigureAwait(false);
            await SynchronizeTerminalSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RunOnUiThread(() =>
            {
                WorkbenchTerminal.IsBusy = false;
                WorkbenchTerminal.Status = $"Terminal could not start: {exception.Message}";
            });
        }
    }

    public async Task SplitWorkbenchTerminalAsync(
        TerminalSplitOrientation orientation,
        CancellationToken cancellationToken = default)
    {
        var project = SelectedProject;
        var activeSessionId = WorkbenchTerminal.GetPaneSession(
            WorkbenchTerminal.ActivePaneIndex)?.TerminalSessionId;
        if (project is null || activeSessionId is null || !WorkbenchTerminal.CanSplit)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            WorkbenchTerminal.IsBusy = true;
            WorkbenchTerminal.Status = orientation == TerminalSplitOrientation.Right
                ? "Splitting terminal to the right…"
                : "Splitting terminal below…";
        });
        try
        {
            var visibleSessionIds = WorkbenchTerminal.VisibleSessionIds.ToHashSet();
            var descriptor = WorkbenchTerminal.Sessions
                .Select(item => item.Descriptor)
                .FirstOrDefault(item => !visibleSessionIds.Contains(item.TerminalSessionId));
            IReadOnlyList<TerminalSessionDescriptor> sessions;
            if (descriptor is null)
            {
                descriptor = await RequireClient().StartTerminalSessionAsync(
                    new StartTerminalSessionRequest(
                        project.ProjectId,
                        WorkbenchTerminal.SelectedShell.Kind,
                        ThreadId: SelectedThread?.ThreadId),
                    cancellationToken).ConfigureAwait(false);
                sessions = FilterTerminalSessions(
                    await RequireClient().ListTerminalSessionsAsync(project.ProjectId, cancellationToken)
                        .ConfigureAwait(false),
                    SelectedThread?.ThreadId);
            }
            else
            {
                sessions = WorkbenchTerminal.Sessions.Select(item => item.Descriptor).ToArray();
            }

            await RunOnUiThreadAsync(() =>
            {
                WorkbenchTerminal.ApplySessions(sessions, activeSessionId);
                WorkbenchTerminal.Split(orientation, descriptor.TerminalSessionId);
                SaveWorkbenchTerminalLayout();
            }).ConfigureAwait(false);
            await SynchronizeTerminalSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
            await RunOnUiThreadAsync(() =>
            {
                WorkbenchTerminal.IsBusy = false;
                WorkbenchTerminal.Status = "Terminal pane created";
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RunOnUiThread(() =>
            {
                WorkbenchTerminal.IsBusy = false;
                WorkbenchTerminal.Status = $"Terminal pane could not be created: {exception.Message}";
            });
        }
    }

    public void ActivateWorkbenchTerminalPane(int paneIndex)
    {
        WorkbenchTerminal.ActivatePane(paneIndex);
        SaveWorkbenchTerminalLayout();
    }

    public async Task CloseWorkbenchTerminalPaneAsync(CancellationToken cancellationToken = default)
    {
        if (!WorkbenchTerminal.CanClosePane)
        {
            return;
        }

        WorkbenchTerminal.CloseActivePane();
        SaveWorkbenchTerminalLayout();
        await SynchronizeTerminalSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
    }

    public void ResizeWorkbenchTerminalSplit(double ratio) => WorkbenchTerminal.ResizeSplit(ratio);

    public void ResizeWorkbenchTerminalSplit(string splitNodeId, double ratio) =>
        WorkbenchTerminal.ResizeSplit(splitNodeId, ratio);

    public void CommitWorkbenchTerminalSplit()
    {
        SaveWorkbenchTerminalLayout();
    }

    public async Task SendWorkbenchTerminalInputAsync(CancellationToken cancellationToken = default)
    {
        var session = WorkbenchTerminal.SelectedSession?.Descriptor;
        var input = WorkbenchTerminal.InputText;
        if (session is null || session.State != TerminalSessionState.Running || string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        RunOnUiThread(() => WorkbenchTerminal.InputText = string.Empty);
        try
        {
            await WriteWorkbenchTerminalDataAsync(
                session.TerminalSessionId,
                input + "\r",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RunOnUiThread(() =>
            {
                WorkbenchTerminal.InputText = input;
                WorkbenchTerminal.Status = $"Input was not sent: {exception.Message}";
            });
        }
    }

    public async Task SendWorkbenchTerminalDataAsync(
        string data,
        CancellationToken cancellationToken = default)
    {
        if (!CanOperate) return;
        var session = WorkbenchTerminal.SelectedSession?.Descriptor;
        if (session is null || session.State != TerminalSessionState.Running || string.IsNullOrEmpty(data))
        {
            return;
        }

        try
        {
            await WriteWorkbenchTerminalDataAsync(
                session.TerminalSessionId,
                data,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RunOnUiThread(() => WorkbenchTerminal.Status = $"Input was not sent: {exception.Message}");
        }
    }

    public async Task StopWorkbenchTerminalAsync(CancellationToken cancellationToken = default)
    {
        var session = WorkbenchTerminal.SelectedSession?.Descriptor;
        if (session is null || session.State != TerminalSessionState.Running)
        {
            return;
        }

        RunOnUiThread(() => WorkbenchTerminal.IsBusy = true);
        try
        {
            var descriptor = await RequireClient().StopTerminalSessionAsync(
                new StopTerminalSessionRequest(session.TerminalSessionId),
                cancellationToken).ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                WorkbenchTerminal.Apply(
                    descriptor.TerminalSessionId,
                    descriptor,
                    GetTerminalOutput(descriptor.TerminalSessionId));
                WorkbenchTerminal.IsBusy = false;
            });
        }
        catch (Exception exception)
        {
            RunOnUiThread(() =>
            {
                WorkbenchTerminal.IsBusy = false;
                WorkbenchTerminal.Status = $"Terminal could not stop: {exception.Message}";
            });
        }
    }

    public async Task RestartWorkbenchTerminalAsync(CancellationToken cancellationToken = default)
    {
        var session = WorkbenchTerminal.SelectedSession?.Descriptor;
        var project = SelectedProject;
        if (session is null || project is null)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            WorkbenchTerminal.IsBusy = true;
            WorkbenchTerminal.Status = "Restarting terminal…";
        });
        try
        {
            await RequireClient().CloseTerminalSessionAsync(
                new CloseTerminalSessionRequest(session.TerminalSessionId),
                cancellationToken).ConfigureAwait(false);
            await DetachTerminalSubscriptionAsync(session.TerminalSessionId).ConfigureAwait(false);
            var replacement = await RequireClient().StartTerminalSessionAsync(
                new StartTerminalSessionRequest(
                    project.ProjectId,
                    session.ShellKind,
                    session.Columns,
                    session.Rows,
                    SelectedThread?.ThreadId),
                cancellationToken).ConfigureAwait(false);
            var sessions = FilterTerminalSessions(
                await RequireClient().ListTerminalSessionsAsync(project.ProjectId, cancellationToken)
                    .ConfigureAwait(false),
                SelectedThread?.ThreadId);
            await RunOnUiThreadAsync(() =>
            {
                WorkbenchTerminal.ApplySessions(sessions, replacement.TerminalSessionId);
                WorkbenchTerminal.IsBusy = false;
                SaveWorkbenchTerminalLayout();
            }).ConfigureAwait(false);
            await SynchronizeTerminalSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RunOnUiThread(() =>
            {
                WorkbenchTerminal.IsBusy = false;
                WorkbenchTerminal.Status = $"Terminal could not restart: {exception.Message}";
            });
        }
    }

    public async Task CloseWorkbenchTerminalAsync(CancellationToken cancellationToken = default)
    {
        var session = WorkbenchTerminal.SelectedSession?.Descriptor;
        if (session is null)
        {
            return;
        }

        RunOnUiThread(() => WorkbenchTerminal.IsBusy = true);
        try
        {
            await RequireClient().CloseTerminalSessionAsync(
                new CloseTerminalSessionRequest(session.TerminalSessionId),
                cancellationToken).ConfigureAwait(false);
            await DetachTerminalSubscriptionAsync(session.TerminalSessionId).ConfigureAwait(false);
            await RefreshWorkbenchTerminalsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RunOnUiThread(() =>
            {
                WorkbenchTerminal.IsBusy = false;
                WorkbenchTerminal.Status = $"Terminal could not close: {exception.Message}";
            });
        }
    }

    public void ClearWorkbenchTerminalOutput(int? paneIndex = null) =>
        RunOnUiThread(() => WorkbenchTerminal.ClearOutput(paneIndex));

    public void ResizeWorkbenchTerminalGrid(int paneIndex, int columns, int rows)
    {
        if (!CanOperate) return;
        if (Layout.SelectedPanel != WorkbenchPanelKind.Terminal ||
            WorkbenchTerminal.GetPaneSession(paneIndex) is not { State: TerminalSessionState.Running } session)
        {
            return;
        }

        QueueWorkbenchTerminalResize(
            session,
            Math.Clamp(columns, TerminalDefaults.MinimumColumns, TerminalDefaults.MaximumColumns),
            Math.Clamp(rows, TerminalDefaults.MinimumRows, TerminalDefaults.MaximumRows));
    }

    private void QueueWorkbenchTerminalResize(TerminalSessionDescriptor session, int columns, int rows)
    {
        if (session.Columns == columns && session.Rows == rows)
        {
            return;
        }

        var resizeCancellation = new CancellationTokenSource();
        _workbenchTerminalResizeCancellations.AddOrUpdate(
            session.TerminalSessionId,
            resizeCancellation,
            (_, previous) =>
            {
                previous.Cancel();
                return resizeCancellation;
            });
        _ = ResizeWorkbenchTerminalAsync(session.TerminalSessionId, columns, rows, resizeCancellation);
    }

    private void SaveWorkbenchTerminalLayout()
    {
        if (SelectedProject is { } project)
        {
            Layout.SaveTerminalPaneLayout(
                project.ProjectId.Value,
                WorkbenchTerminal.CaptureLayout());
        }
    }

    public Task RefreshWorkbenchChangesAsync()
    {
        var project = SelectedProject;
        if (project is null)
        {
            CancelWorkbenchChanges();
            RunOnUiThread(() => WorkbenchChanges.Reset(hasProject: false));
            return Task.CompletedTask;
        }

        var loadCancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _workbenchChangesLoadCancellation, loadCancellation)?.Cancel();
        Interlocked.Exchange(ref _workbenchDiffLoadCancellation, null)?.Cancel();
        RunOnUiThread(() =>
        {
            WorkbenchChanges.Changes.Clear();
            WorkbenchChanges.ClearDiff();
            WorkbenchChanges.Status = "Loading changes…";
        });
        return LoadWorkbenchChangesAsync(project.ProjectId, SelectedThread?.ThreadId, loadCancellation);
    }

    public Task InitializeGitRepositoryAsync(CancellationToken cancellationToken = default) =>
        ExecuteWorkbenchGitCommandAsync(new GitInitCommand(), "Initializing Git repository…", cancellationToken);

    public Task PullGitBranchAsync(CancellationToken cancellationToken = default) =>
        ExecuteWorkbenchGitCommandAsync(new GitPullCommand(), "Pulling branch…", cancellationToken);

    public Task SwitchGitBranchAsync(CancellationToken cancellationToken = default)
    {
        var branch = WorkbenchChanges.SelectedBranch?.Name;
        return string.IsNullOrWhiteSpace(branch)
            ? Task.CompletedTask
            : ExecuteWorkbenchGitCommandAsync(
                new GitSwitchBranchCommand(branch),
                $"Switching to {branch}…",
                cancellationToken);
    }

    public Task CreateGitBranchAsync(CancellationToken cancellationToken = default)
    {
        var branch = WorkbenchChanges.NewBranchName.Trim();
        return string.IsNullOrWhiteSpace(branch)
            ? Task.CompletedTask
            : ExecuteWorkbenchGitCommandAsync(
                new GitCreateBranchCommand(branch, SwitchToBranch: true),
                $"Creating {branch}…",
                cancellationToken);
    }

    public Task CommitGitChangesAsync(CancellationToken cancellationToken = default) =>
        ExecuteWorkbenchGitCommandAsync(
            new GitRunActionCommand(
                GitActionKind.Commit,
                WorkbenchChanges.CommitMessage,
                WorkbenchChanges.Changes.Select(static change => change.RelativePath).ToArray()),
            "Committing changes…",
            cancellationToken);

    public Task PushGitBranchAsync(CancellationToken cancellationToken = default) =>
        ExecuteWorkbenchGitCommandAsync(
            new GitRunActionCommand(GitActionKind.Push),
            "Pushing branch…",
            cancellationToken);

    public Task CommitAndPushGitChangesAsync(CancellationToken cancellationToken = default) =>
        ExecuteWorkbenchGitCommandAsync(
            new GitRunActionCommand(
                GitActionKind.CommitPush,
                WorkbenchChanges.CommitMessage,
                WorkbenchChanges.Changes.Select(static change => change.RelativePath).ToArray()),
            "Committing and pushing changes…",
            cancellationToken);

    public async Task RemoveSelectedThreadWorktreeAsync(CancellationToken cancellationToken = default)
    {
        var project = SelectedProject;
        var thread = SelectedThread;
        var path = WorkbenchChanges.WorktreePath;
        if (project is null || thread is null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await ExecuteWorkbenchGitCommandAsync(
            new GitRemoveWorktreeCommand(path, Force: true, ConfirmationToken: path),
            "Removing thread worktree…",
            cancellationToken).ConfigureAwait(false);
        try
        {
            var refreshed = await RequireClient().GetThreadAsync(thread.ThreadId, cancellationToken)
                .ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                var index = Threads.ToList().FindIndex(candidate => candidate.ThreadId == refreshed.ThreadId);
                if (index >= 0)
                {
                    Threads[index] = refreshed;
                }

                SelectedThread = refreshed;
            });
        }
        catch (Exception exception)
        {
            ReportRuntimeError(exception);
        }
    }

    private async Task ExecuteWorkbenchGitCommandAsync(
        WorkspaceGitCommand command,
        string progressMessage,
        CancellationToken cancellationToken)
    {
        var project = SelectedProject;
        var threadId = SelectedThread?.ThreadId;
        if (project is null || WorkbenchChanges.IsBusy)
        {
            return;
        }

        RunOnUiThread(() => WorkbenchChanges.BeginOperation(progressMessage));
        try
        {
            var result = await RequireClient().ExecuteWorkspaceGitCommandAsync(
                new WorkspaceTarget(project.ProjectId, threadId),
                command,
                WorkbenchChanges.HeadSha,
                string.Equals(WorkbenchChanges.BranchName, "No repository", StringComparison.Ordinal) ||
                string.Equals(WorkbenchChanges.BranchName, "Not a Git repository", StringComparison.Ordinal)
                    ? null
                    : WorkbenchChanges.BranchName,
                string.IsNullOrWhiteSpace(WorkbenchChanges.StatusToken) ? null : WorkbenchChanges.StatusToken,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var completed = result.Receipt.State == CommandReceiptState.Completed;
            var message = completed
                ? result.Result?.Message ?? FormatGitOperationResult(result.Result)
                : result.Result?.Message ?? result.Receipt.ErrorCode ?? "The Git command failed.";
            await RefreshWorkbenchChangesAsync().ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                if (SelectedProject?.ProjectId == project.ProjectId && SelectedThread?.ThreadId == threadId)
                {
                    if (completed && command is GitRunActionCommand { Action: GitActionKind.Commit or GitActionKind.CommitPush })
                    {
                        WorkbenchChanges.CommitMessage = string.Empty;
                    }

                    if (completed && command is GitCreateBranchCommand)
                    {
                        WorkbenchChanges.NewBranchName = string.Empty;
                    }

                    WorkbenchChanges.CompleteOperation(message);
                }
            });
        }
        catch (Exception exception)
        {
            RunOnUiThread(() => WorkbenchChanges.CompleteOperation($"Git command failed: {exception.Message}"));
        }
    }

    private static string FormatGitOperationResult(WorkspaceGitOperationResult? result) => result?.Status switch
    {
        "initialized" => "Git repository initialized.",
        "pulled" => "Branch updated with a fast-forward pull.",
        "created" => result.BranchName is null ? "Worktree created." : $"Created {result.BranchName}.",
        "switched" => $"Switched to {result.BranchName}.",
        "committed" => "Changes committed.",
        "pushed" => "Branch pushed.",
        "committed_and_pushed" => "Changes committed and pushed.",
        "skipped_up_to_date" => "Already up to date.",
        "skipped_already_repository" => "This workspace is already a Git repository.",
        _ => "Git command completed.",
    };

    public async Task SelectWorkbenchChangeAsync(WorkbenchChangeItemViewModel? change)
    {
        var project = SelectedProject;
        Interlocked.Exchange(ref _workbenchDiffLoadCancellation, null)?.Cancel();
        if (project is null || change is null)
        {
            RunOnUiThread(WorkbenchChanges.ClearDiff);
            return;
        }

        var loadCancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _workbenchDiffLoadCancellation, loadCancellation)?.Cancel();
        RunOnUiThread(() =>
        {
            WorkbenchChanges.SelectedChange = change;
            WorkbenchChanges.DiffPath = change.RelativePath;
            WorkbenchChanges.DiffContent = string.Empty;
            WorkbenchChanges.DiffMessage = "Loading diff…";
        });
        try
        {
            var result = await RequireClient().GetProjectChangeDiffAsync(
                new GetProjectChangeDiffRequest(
                    project.ProjectId,
                    change.RelativePath,
                    ThreadId: SelectedThread?.ThreadId),
                loadCancellation.Token).ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                if (SelectedProject?.ProjectId != project.ProjectId ||
                    WorkbenchChanges.SelectedChange?.RelativePath != change.RelativePath)
                {
                    return;
                }

                WorkbenchChanges.DiffContent = result.DiffContent;
                var areas = new List<string>();
                if (result.HasStagedChanges)
                {
                    areas.Add("staged");
                }

                if (result.HasWorkingTreeChanges)
                {
                    areas.Add("working tree");
                }

                if (result.IsUntracked)
                {
                    areas.Add("untracked");
                }

                var summary = areas.Count == 0 ? "No textual diff" : string.Join(" + ", areas);
                WorkbenchChanges.DiffMessage = result.IsTruncated
                    ? summary + " • preview truncated"
                    : summary;
            });
        }
        catch (OperationCanceledException) when (loadCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RunOnUiThread(() =>
            {
                if (SelectedProject?.ProjectId == project.ProjectId &&
                    WorkbenchChanges.SelectedChange?.RelativePath == change.RelativePath)
                {
                    WorkbenchChanges.DiffContent = string.Empty;
                    WorkbenchChanges.DiffMessage = $"Diff unavailable: {exception.Message}";
                }
            });
        }
        finally
        {
            Interlocked.CompareExchange(ref _workbenchDiffLoadCancellation, null, loadCancellation);
            loadCancellation.Dispose();
        }
    }

    public async Task OpenCheckpointDiffAsync(
        int turnCount,
        CheckpointDiffScope scope,
        string? relativePath = null)
    {
        var thread = SelectedThread;
        if (thread is null || turnCount < 1)
        {
            return;
        }

        var loadCancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _workbenchDiffLoadCancellation, loadCancellation)?.Cancel();
        RunOnUiThread(() =>
        {
            Layout.SelectedPanel = WorkbenchPanelKind.Changes;
            Layout.IsRightPanelOpen = true;
            WorkbenchChanges.BeginCheckpointDiff(turnCount, scope, relativePath);
        });
        try
        {
            var result = await RequireClient().GetThreadCheckpointDiffAsync(
                new GetThreadCheckpointDiffRequest(thread.ThreadId, turnCount, scope, relativePath),
                loadCancellation.Token).ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                if (!loadCancellation.IsCancellationRequested &&
                    SelectedThread?.ThreadId == thread.ThreadId)
                {
                    WorkbenchChanges.ApplyCheckpointDiff(result);
                }
            });
        }
        catch (OperationCanceledException) when (loadCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RunOnUiThread(() =>
            {
                if (SelectedThread?.ThreadId == thread.ThreadId)
                {
                    WorkbenchChanges.DiffContent = string.Empty;
                    WorkbenchChanges.DiffMessage = $"Checkpoint diff unavailable: {exception.Message}";
                }
            });
        }
        finally
        {
            Interlocked.CompareExchange(ref _workbenchDiffLoadCancellation, null, loadCancellation);
            loadCancellation.Dispose();
        }
    }

    public void UpdateWorkbenchFileQuery(string? query)
    {
        var normalized = (query ?? string.Empty).Replace('\\', '/');
        if (normalized.Length > FileSearchDefaults.MaximumQueryLength)
        {
            normalized = normalized[..FileSearchDefaults.MaximumQueryLength];
        }

        if (string.Equals(WorkbenchFiles.SearchQuery, normalized, StringComparison.Ordinal))
        {
            return;
        }

        RunOnUiThread(() =>
        {
            WorkbenchFiles.SearchQuery = normalized;
            WorkbenchFiles.Status = SelectedProject is null
                ? "Select a workspace to browse files"
                : "Searching project files…";
        });
        _ = QueueWorkbenchFileSearchAsync(debounce: true);
    }

    public void UpdateWorkbenchFileSearchMode(int selectedIndex)
    {
        RunOnUiThread(() => WorkbenchFiles.SetSearchMode(selectedIndex));
        _ = QueueWorkbenchFileSearchAsync(debounce: false);
    }

    public void ToggleWorkbenchContentSearchOption(string option)
    {
        RunOnUiThread(() =>
        {
            switch (option)
            {
                case "case":
                    WorkbenchFiles.CaseSensitive = !WorkbenchFiles.CaseSensitive;
                    break;
                case "word":
                    WorkbenchFiles.WholeWord = !WorkbenchFiles.WholeWord;
                    break;
                case "regex":
                    WorkbenchFiles.UseRegularExpression = !WorkbenchFiles.UseRegularExpression;
                    break;
            }
        });
        _ = QueueWorkbenchFileSearchAsync(debounce: true);
    }

    public Task RefreshWorkbenchFilesAsync() => QueueWorkbenchFileSearchAsync(debounce: false);

    public async Task SelectWorkbenchFileAsync(ProjectFileMatch? file)
    {
        if (file is null)
        {
            return;
        }

        await OpenWorkbenchFileAsync(file.RelativePath).ConfigureAwait(false);
    }

    public async Task SelectWorkbenchContentMatchAsync(ProjectContentMatch? match)
    {
        if (match is null)
        {
            return;
        }

        await OpenWorkbenchFileAsync(match.RelativePath, match.LineNumber).ConfigureAwait(false);
    }

    public Task OpenWorkbenchTreeItemAsync(WorkspaceTreeItemViewModel? item) =>
        item is null || item.IsDirectory
            ? Task.CompletedTask
            : OpenWorkbenchFileAsync(item.RelativePath);

    public async Task OpenWorkbenchFileAsync(
        string relativePath,
        int? revealLine = null,
        bool forceReload = false)
    {
        var project = SelectedProject;
        if (project is null || string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        WorkbenchFileDocumentViewModel document = null!;
        await RunOnUiThreadAsync(() =>
        {
            document = WorkbenchFiles.OpenDocument(relativePath, revealLine);
            document.HasWriteAccess = CanOperate;
        })
            .ConfigureAwait(false);
        if (!forceReload && !document.IsLoading && document.Revision.Length != 0)
        {
            return;
        }

        var readCancellation = new CancellationTokenSource();
        var editVersion = document.EditVersion;
        Interlocked.Exchange(ref _workbenchFileReadCancellation, readCancellation)?.Cancel();
        try
        {
            if (document.IsImage)
            {
                var asset = await RequireClient().ReadProjectFileAssetAsync(
                    new ReadProjectFileAssetRequest(
                        project.ProjectId,
                        relativePath,
                        ThreadId: SelectedThread?.ThreadId),
                    readCancellation.Token).ConfigureAwait(false);
                await RunOnUiThreadAsync(() =>
                {
                    if (SelectedProject?.ProjectId == project.ProjectId &&
                        WorkbenchFiles.OpenDocuments.Contains(document))
                    {
                        document.ApplyAsset(asset);
                    }
                }).ConfigureAwait(false);
            }
            else
            {
                var result = await RequireClient().ReadProjectFileAsync(
                    new ReadProjectFileRequest(
                        project.ProjectId,
                        relativePath,
                        ThreadId: SelectedThread?.ThreadId),
                    readCancellation.Token).ConfigureAwait(false);
                await RunOnUiThreadAsync(() =>
                {
                    if (SelectedProject?.ProjectId == project.ProjectId &&
                        WorkbenchFiles.OpenDocuments.Contains(document))
                    {
                        document.ApplyTextIfUnchanged(result, editVersion);
                    }
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (readCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await RunOnUiThreadAsync(() =>
            {
                if (SelectedProject?.ProjectId == project.ProjectId &&
                    WorkbenchFiles.OpenDocuments.Contains(document))
                {
                    document.ApplyLoadFailure($"Unable to open file: {exception.Message}");
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(ref _workbenchFileReadCancellation, null, readCancellation);
            readCancellation.Dispose();
        }
    }

    public async Task SaveWorkbenchFileAsync(WorkbenchFileDocumentViewModel? document = null)
    {
        var project = SelectedProject;
        document ??= WorkbenchFiles.ActiveDocument;
        if (project is null || document is null || !document.CanSave)
        {
            return;
        }

        document.IsSaving = true;
        document.Status = "Saving…";
        var content = document.Content;
        var revision = document.Revision;
        try
        {
            var result = await RequireClient().SaveProjectFileAsync(
                new SaveProjectFileRequest(
                    project.ProjectId,
                    document.RelativePath,
                    content,
                    revision,
                    SelectedThread?.ThreadId)).ConfigureAwait(false);
            await RunOnUiThreadAsync(() => document.ApplySaved(result, content)).ConfigureAwait(false);
            _ = QueueWorkbenchFileSearchAsync(debounce: false);
        }
        catch (Exception exception)
        {
            await RunOnUiThreadAsync(() =>
            {
                document.IsSaving = false;
                document.Status = $"Save failed: {exception.Message}";
            }).ConfigureAwait(false);
        }
    }

    public Task ReloadWorkbenchFileAsync(WorkbenchFileDocumentViewModel? document = null)
    {
        document ??= WorkbenchFiles.ActiveDocument;
        return document is null
            ? Task.CompletedTask
            : OpenWorkbenchFileAsync(document.RelativePath, document.RevealLine, forceReload: true);
    }

    public async Task OpenWorkbenchFileInEditorAsync(WorkbenchFileDocumentViewModel? document = null)
    {
        var project = SelectedProject;
        document ??= WorkbenchFiles.ActiveDocument;
        if (project is null || document is null)
        {
            return;
        }

        document.Status = "Opening external editor…";
        try
        {
            if (IsRemote)
            {
                if (_remoteEditorProfile is null)
                    throw new InvalidOperationException("Open this environment through a saved SSH connection to launch VS Code on this device.");
                var workspace = await RequireClient().GetProjectChangesAsync(new(project.ProjectId, ThreadId: SelectedThread?.ThreadId));
                var link = PiStation.ClientRuntime.Ssh.RemoteEditorLink.Create(_remoteEditorProfile, workspace.WorkspacePath, document.RelativePath);
                var opened = await Windows.System.Launcher.LaunchUriAsync(link);
                document.Status = opened ? "Opened in local VS Code over SSH • PiStation's unsaved edits stay here"
                    : "Install VS Code with Remote SSH to open this workspace externally.";
                return;
            }
            var result = await RequireClient().OpenProjectFileInEditorAsync(
                new OpenProjectFileInEditorRequest(
                    project.ProjectId,
                    document.RelativePath,
                    document.RevealLine,
                    document.RevealLine is null ? null : 1,
                    SelectedThread?.ThreadId)).ConfigureAwait(false);
            RunOnUiThread(() => document.Status = $"Opened in {result.EditorName}");
        }
        catch (Exception exception)
        {
            RunOnUiThread(() => document.Status = $"Editor unavailable: {exception.Message}");
        }
    }

    public async Task SetShowingArchivedThreadsAsync(
        bool showArchived,
        CancellationToken cancellationToken = default)
    {
        if (IsShowingArchivedThreads == showArchived)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            IsShowingArchivedThreads = showArchived;
            ThreadLifecycleStatus = string.Empty;
            ThreadListStatus = showArchived ? "Loading archived threads…" : "Loading active threads…";
        });
        await QueueThreadListRefreshAsync(debounce: false, cancellationToken).ConfigureAwait(false);
    }

    public ThreadDescriptor? FindThread(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        var parsed = ThreadId.Parse(threadId);
        return Threads.FirstOrDefault(thread => thread.ThreadId == parsed) ??
            (SelectedThread?.ThreadId == parsed ? SelectedThread : null) ??
            _client?.ThreadMetadata.GetCurrent(parsed);
    }

    public async Task<bool> RenameThreadAsync(
        ThreadDescriptor thread,
        string title,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        var normalized = title.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            ReportRuntimeError("A thread title cannot be empty.");
            return false;
        }

        if (normalized.Length > ThreadLifecycleDefaults.MaximumTitleLength)
        {
            ReportRuntimeError($"A thread title cannot exceed {ThreadLifecycleDefaults.MaximumTitleLength} characters.");
            return false;
        }

        if (string.Equals(thread.Title, normalized, StringComparison.Ordinal))
        {
            return true;
        }

        var updated = await ExecuteThreadLifecycleAsync(
            thread,
            (client, current, token) => client.RenameThreadAsync(
                current.ThreadId,
                current.Revision,
                normalized,
                token),
            "The host could not confirm whether the thread was renamed. Reload metadata before trying again.",
            cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            return false;
        }

        ApplySelectedThreadMetadata(updated);
        await QueueThreadListRefreshAsync(debounce: false, cancellationToken).ConfigureAwait(false);
        RunOnUiThread(() => ThreadLifecycleStatus = $"Renamed thread to {updated.Title}");
        return true;
    }

    public async Task<bool> SetThreadPinnedAsync(
        ThreadDescriptor thread,
        bool isPinned,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        if (thread.IsPinned == isPinned)
        {
            return true;
        }

        var updated = await ExecuteThreadLifecycleAsync(
            thread,
            (client, current, token) => client.SetThreadPinnedAsync(
                current.ThreadId,
                current.Revision,
                isPinned,
                token),
            "The host could not confirm whether the thread pin changed. Reload metadata before trying again.",
            cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            return false;
        }

        ApplySelectedThreadMetadata(updated);
        await QueueThreadListRefreshAsync(debounce: false, cancellationToken).ConfigureAwait(false);
        RunOnUiThread(() => ThreadLifecycleStatus = isPinned
            ? $"Pinned {updated.Title}"
            : $"Unpinned {updated.Title}");
        return true;
    }

    public async Task<bool> SetThreadArchivedAsync(
        ThreadDescriptor thread,
        bool isArchived,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        if (thread.IsArchived == isArchived)
        {
            return true;
        }

        var updated = await ExecuteThreadLifecycleAsync(
            thread,
            (client, current, token) => client.SetThreadArchivedAsync(
                current.ThreadId,
                current.Revision,
                isArchived,
                token),
            "The host could not confirm whether the thread archive state changed. Reload metadata before trying again.",
            cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            return false;
        }

        if (isArchived && SelectedThread?.ThreadId == updated.ThreadId)
        {
            await SelectThreadAsync(null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplySelectedThreadMetadata(updated);
        }

        await QueueThreadListRefreshAsync(debounce: false, cancellationToken).ConfigureAwait(false);
        RunOnUiThread(() => ThreadLifecycleStatus = isArchived
            ? $"Archived {updated.Title}"
            : $"Restored {updated.Title}");
        return true;
    }

    public Task SelectPiModelAsync(
        PiModelOptionViewModel? model,
        CancellationToken cancellationToken = default)
    {
        var snapshot = PiConfiguration.Snapshot;
        if (model is null || snapshot is null || !CanConfigurePi ||
            Matches(snapshot.ActiveModel, model))
        {
            return Task.CompletedTask;
        }

        var thinkingLevel = model.SupportsReasoning
            ? PiConfiguration.SelectedThinkingLevel?.Value ?? snapshot.ActiveThinkingLevel
            : PiThinkingLevel.Off;
        return UpdatePiConfigurationAsync(
            model.Selection,
            thinkingLevel,
            cancellationToken);
    }

    public Task SelectPiThinkingLevelAsync(
        PiThinkingLevelOptionViewModel? thinkingLevel,
        CancellationToken cancellationToken = default)
    {
        var snapshot = PiConfiguration.Snapshot;
        if (thinkingLevel is null || snapshot is null || !CanConfigurePi ||
            snapshot.ActiveThinkingLevel == thinkingLevel.Value)
        {
            return Task.CompletedTask;
        }

        return UpdatePiConfigurationAsync(
            PiConfiguration.SelectedModel?.Selection ?? snapshot.ActiveModel,
            thinkingLevel.Value,
            cancellationToken);
    }

    public async Task SendPromptAsync(CancellationToken cancellationToken = default)
    {
        CloseFileMentionSuggestions();
        var thread = SelectedThread;
        var projection = Thread.Projection;
        var prompt = PromptText.Trim();
        if (thread is null || projection is null || !CanSend)
        {
            return;
        }

        SetCommandPending(true);
        try
        {
            var sentDraft = await Composer.PrepareTurnAsync(cancellationToken).ConfigureAwait(false) ??
                throw new InvalidOperationException("The active draft is not ready yet.");
            var attachmentIds = sentDraft.Attachments
                .Select(static attachment => attachment.AttachmentId)
                .ToArray();
            var receipt = await RequireClient()
                .StartTurnAsync(
                    thread.ThreadId,
                    prompt,
                    projection.ProjectionEpoch,
                    draftId: sentDraft.DraftId,
                    draftRevision: sentDraft.Revision,
                    attachmentIds: attachmentIds,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            RunOnUiThread(() => HandleCommandReceipt(receipt));
            if (receipt.State is CommandReceiptState.Accepted or CommandReceiptState.Completed)
            {
                try
                {
                    await Composer.ClearAcceptedTurnAsync(sentDraft, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    ReportRuntimeError($"The turn started, but its draft could not be cleared: {exception.Message}");
                }
            }
        }
        catch (CommandDispatchUncertainException exception)
        {
            ShowUncertainCommand(exception.CommandId, exception.Message);
        }
        catch (Exception exception)
        {
            ReportRuntimeError(exception);
        }
        finally
        {
            SetCommandPending(false);
        }
    }

    public Task AddAttachmentAsync(
        string fileName,
        string? mediaType,
        Stream content,
        long byteLength,
        CancellationToken cancellationToken = default) =>
        Composer.AddAttachmentAsync(fileName, mediaType, content, byteLength, cancellationToken);

    public Task RemoveAttachmentAsync(
        DraftAttachmentViewModel attachment,
        CancellationToken cancellationToken = default) =>
        Composer.RemoveAttachmentAsync(attachment, cancellationToken);

    public void UpdateFileMentionQuery(string? query)
    {
        var project = SelectedProject;
        if (project is null || query is null || query.Length > FileSearchDefaults.MaximumQueryLength)
        {
            CloseFileMentionSuggestions();
            return;
        }

        query = query.Replace('\\', '/');
        if (FileMentions.IsOpen &&
            _fileMentionProjectId == project.ProjectId &&
            string.Equals(_fileMentionQuery, query, StringComparison.Ordinal))
        {
            return;
        }

        var searchCancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _fileMentionSearchCancellation, searchCancellation);
        previous?.Cancel();
        _fileMentionProjectId = project.ProjectId;
        _fileMentionQuery = query;
        RunOnUiThread(() =>
        {
            SetFileMentionSuggestionsOpen(true);
            Replace(FileMentionSuggestions, []);
            SelectedFileMentionIndex = -1;
            FileMentionStatus = "Searching files…";
        });
        _ = SearchFileMentionsAsync(project.ProjectId, query, searchCancellation);
    }

    public void CloseFileMentionSuggestions()
    {
        var cancellation = Interlocked.Exchange(ref _fileMentionSearchCancellation, null);
        cancellation?.Cancel();
        _fileMentionProjectId = null;
        _fileMentionQuery = null;
        RunOnUiThread(() =>
        {
            SetFileMentionSuggestionsOpen(false);
            Replace(FileMentionSuggestions, []);
            SelectedFileMentionIndex = -1;
            FileMentionStatus = string.Empty;
        });
    }

    public void MoveFileMentionSelection(int delta)
    {
        if (!FileMentions.IsOpen || FileMentionSuggestions.Count == 0 || delta == 0)
        {
            return;
        }

        var start = SelectedFileMentionIndex < 0 ? (delta > 0 ? -1 : 0) : SelectedFileMentionIndex;
        SelectedFileMentionIndex = (start + delta + FileMentionSuggestions.Count) %
            FileMentionSuggestions.Count;
    }

    public async Task StopTurnAsync(CancellationToken cancellationToken = default)
    {
        var thread = SelectedThread;
        var projection = Thread.Projection;
        if (thread is null || projection is null || !CanStop)
        {
            return;
        }

        SetCommandPending(true);
        try
        {
            var receipt = await RequireClient().StopTurnAsync(
                thread.ThreadId,
                projection.ProjectionEpoch,
                projection.CurrentTurnId,
                cancellationToken).ConfigureAwait(false);
            RunOnUiThread(() => HandleCommandReceipt(receipt));
        }
        catch (CommandDispatchUncertainException exception)
        {
            ShowUncertainCommand(exception.CommandId, exception.Message);
        }
        catch (Exception exception)
        {
            ReportRuntimeError(exception);
        }
        finally
        {
            SetCommandPending(false);
        }
    }

    public async Task RestartPiAsync(CancellationToken cancellationToken = default)
    {
        var thread = SelectedThread;
        var projection = Thread.Projection;
        if (thread is null || projection is null || !CanRestartPi)
        {
            return;
        }

        SetCommandPending(true);
        try
        {
            var receipt = await RequireClient()
                .RestartThreadAsync(thread.ThreadId, projection.ProjectionEpoch, cancellationToken)
                .ConfigureAwait(false);
            RunOnUiThread(() => HandleCommandReceipt(receipt));
        }
        catch (CommandDispatchUncertainException exception)
        {
            ShowUncertainCommand(exception.CommandId, exception.Message);
        }
        catch (Exception exception)
        {
            ReportRuntimeError(exception);
        }
        finally
        {
            SetCommandPending(false);
        }
    }

    public async Task RevertCheckpointAsync(
        int targetTurnCount,
        CancellationToken cancellationToken = default)
    {
        var thread = SelectedThread;
        var projection = Thread.Projection;
        if (thread is null ||
            projection?.RuntimeState != ThreadRuntimeState.Ready ||
            _client?.ConnectionState != EnvironmentConnectionState.Connected ||
            _commandPending)
        {
            return;
        }

        SetCommandPending(true);
        try
        {
            var receipt = await RequireClient().RevertThreadToCheckpointAsync(
                thread.ThreadId,
                targetTurnCount,
                projection.ProjectionEpoch,
                cancellationToken).ConfigureAwait(false);
            RunOnUiThread(() => HandleCommandReceipt(receipt));
            if (receipt.State == CommandReceiptState.Completed)
            {
                await RefreshWorkbenchChangesAsync().ConfigureAwait(false);
            }
        }
        catch (CommandDispatchUncertainException exception)
        {
            ShowUncertainCommand(exception.CommandId, exception.Message);
        }
        catch (Exception exception)
        {
            ReportRuntimeError(exception);
        }
        finally
        {
            SetCommandPending(false);
        }
    }

    public async Task RespondToApprovalAsync(
        InteractionId interactionId,
        ApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        var thread = SelectedThread;
        var projection = Thread.Projection;
        if (thread is null || projection is null || _commandPending)
        {
            return;
        }

        SetCommandPending(true);
        try
        {
            var receipt = await RequireClient().RespondToApprovalAsync(
                thread.ThreadId,
                interactionId,
                decision,
                projection.ProjectionEpoch,
                projection.CurrentTurnId,
                cancellationToken).ConfigureAwait(false);
            RunOnUiThread(() => HandleCommandReceipt(receipt));
        }
        catch (CommandDispatchUncertainException exception)
        {
            ShowUncertainCommand(exception.CommandId, exception.Message);
        }
        catch (Exception exception)
        {
            ReportRuntimeError(exception);
        }
        finally
        {
            SetCommandPending(false);
        }
    }

    public async Task AnswerQuestionAsync(
        InteractionId interactionId,
        string answer,
        CancellationToken cancellationToken = default)
    {
        var thread = SelectedThread;
        var projection = Thread.Projection;
        if (thread is null || projection is null || _commandPending)
        {
            return;
        }

        SetCommandPending(true);
        try
        {
            var receipt = await RequireClient().AnswerQuestionAsync(
                thread.ThreadId,
                interactionId,
                answer,
                projection.ProjectionEpoch,
                projection.CurrentTurnId,
                cancellationToken).ConfigureAwait(false);
            RunOnUiThread(() => HandleCommandReceipt(receipt));
        }
        catch (CommandDispatchUncertainException exception)
        {
            ShowUncertainCommand(exception.CommandId, exception.Message);
        }
        catch (Exception exception)
        {
            ReportRuntimeError(exception);
        }
        finally
        {
            SetCommandPending(false);
        }
    }

    public async Task CancelInteractionAsync(
        InteractionId interactionId,
        CancellationToken cancellationToken = default)
    {
        var thread = SelectedThread;
        var projection = Thread.Projection;
        if (thread is null || projection is null || _commandPending)
        {
            return;
        }

        SetCommandPending(true);
        try
        {
            var receipt = await RequireClient().CancelInteractionAsync(
                thread.ThreadId,
                interactionId,
                projection.ProjectionEpoch,
                projection.CurrentTurnId,
                cancellationToken).ConfigureAwait(false);
            RunOnUiThread(() => HandleCommandReceipt(receipt));
        }
        catch (CommandDispatchUncertainException exception)
        {
            ShowUncertainCommand(exception.CommandId, exception.Message);
        }
        catch (Exception exception)
        {
            ReportRuntimeError(exception);
        }
        finally
        {
            SetCommandPending(false);
        }
    }

    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await RequireClient().ConnectAsync(cancellationToken).ConfigureAwait(false);
            RunOnUiThread(ClearTransportError);
        }
        catch (Exception exception)
        {
            ShowTransportError(exception.Message);
        }
    }

    public Task DisconnectForUiTestAsync(CancellationToken cancellationToken = default)
    {
        if (!_uiTestFaultControlsEnabled)
        {
            throw new InvalidOperationException("UI-test fault controls are not enabled for this launch.");
        }

        return RequireClient().DisconnectAsync(cancellationToken);
    }

    public async Task ResolveUncertainCommandAsync(CancellationToken cancellationToken = default)
    {
        var commandId = Connection.UncertainCommandId;
        if (commandId is null)
        {
            return;
        }

        try
        {
            var receipt = await RequireClient().GetCommandReceiptAsync(commandId.Value, cancellationToken)
                .ConfigureAwait(false);
            var configurationThreadId = Connection.UncertainPiConfigurationThreadId;
            RunOnUiThread(() =>
            {
                if (receipt is null)
                {
                    Connection.UncertainCommandMessage =
                        "The host has no receipt yet. Do not resend automatically; reconnect and check again.";
                    return;
                }

                HandleCommandReceipt(receipt);
            });
            if (configurationThreadId is not null &&
                receipt?.State is CommandReceiptState.Completed or
                    CommandReceiptState.Rejected or
                    CommandReceiptState.Failed)
            {
                await RefreshPiConfigurationAsync(
                    configurationThreadId.Value,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            ShowTransportError(exception.Message);
        }
    }

    public Task FlushDraftAsync(CancellationToken cancellationToken = default) =>
        Composer.FlushAsync(cancellationToken);

    internal bool HasUnsavedChanges => WorkbenchFiles.HasUnsavedChanges || Composer.HasUnsavedChanges;

    internal Task PreserveEditsAsync(CancellationToken cancellationToken = default) =>
        _editingRecovery?.FlushAsync(cancellationToken) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await RunOnUiThreadAsync(() =>
        {
            _runtimeStopped = true;
            SetWindowActive(false);
            WorkbenchFiles.SetWriteAccess(false);
            OnPropertyChanged(nameof(CanOperate));
            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(IsComposerReadOnly));
            RaiseCommandStateChanged();
        }).ConfigureAwait(false);
        _selectionClosed = true;
        Composer.CancelPendingOperations();
        CancelPiConfigurationLoad();
        await _selectionGate.WaitAsync().ConfigureAwait(false);
        try { await DisposeCoreAsync().ConfigureAwait(false); }
        finally { _selectionGate.Release(); }
    }

    private async ValueTask DisposeCoreAsync()
    {
        // Cancel debounced and in-flight draft work before asynchronous subscription cleanup.
        Composer.CancelPendingOperations();
        CloseFileMentionSuggestions();
        CancelWorkbenchFiles();
        CancelWorkbenchChanges();
        CancelWorkbenchPreview();
        CancelWorkbenchTerminal();
        CancelThreadSearch();
        CancelPiConfigurationLoad();
        if (_subscription is not null)
        {
            _subscription.Store.Changed -= OnProjectionChanged;
            _subscription.Store.SynchronizationChanged -= OnThreadSynchronizationChanged;
            await _subscription.DisposeAsync().ConfigureAwait(false);
            _subscription = null;
        }

        await DetachAllTerminalSubscriptionsAsync().ConfigureAwait(false);

        if (_client is not null)
        {
            _client.ConnectionStateChanged -= OnConnectionStateChanged;
            if (_client.Catalog is { } catalog) catalog.Changed -= OnCatalogChanged;
            _client.PiConfigurations.Changed -= OnPiConfigurationChanged;
        }

        Composer.PropertyChanged -= OnComposerPropertyChanged;
        Composer.SaveFailed -= OnComposerSaveFailed;
        await Composer.DisposeAsync().ConfigureAwait(false);
    }

    public void ReportRuntimeError(Exception exception) => ReportRuntimeError(exception.Message);
    public void ReportConnectionError(Exception exception) => ShowTransportError(exception.Message);

    public void ReportRuntimeError(string message) => RunOnUiThread(() =>
    {
        if (_client is null)
        {
            Connection.Status = $"{EnvironmentLabel} • Unavailable";
        }

        Connection.ShowRuntimeError(message);
    });

    public bool IsRefreshingCatalog { get; private set; }

    private void OnCatalogChanged(object? sender, EventArgs args) => RunOnUiThread(() =>
    {
        IsRefreshingCatalog = true;
        try { ApplyCatalog(); }
        finally { IsRefreshingCatalog = false; }
    });

    private void ApplyCatalog()
    {
        if (_client?.Catalog is not { } catalog) return;
        UpdateCatalogCollection(Projects, catalog.Projects, project => project.ProjectId);
        if (SelectedProject is not { } selected) return;
        var project = Projects.FirstOrDefault(p => p.ProjectId == selected.ProjectId);
        if (project is null)
        {
            _ = SelectProjectAsync(null);
            return;
        }
        SelectedProject = project;
        var threads = _client.ThreadMetadata.GetProjectThreads(project.ProjectId, IsShowingArchivedThreads);
        UpdateCatalogCollection(Threads, threads.Where(t => t.IsArchived == IsShowingArchivedThreads &&
            (string.IsNullOrWhiteSpace(ThreadSearchQuery) ||
             t.Title.Contains(ThreadSearchQuery.Trim(), StringComparison.OrdinalIgnoreCase))).ToArray(), thread => thread.ThreadId);
        ThreadListStatus = BuildThreadListStatus(Threads.Count, ThreadSearchQuery, IsShowingArchivedThreads, isTruncated: false);
        if (SelectedThread is { } current)
        {
            var updated = _client.ThreadMetadata.GetCurrent(current.ThreadId);
            if (updated is null || updated.IsArchived != IsShowingArchivedThreads) _ = SelectThreadAsync(null);
            else SelectedThread = updated;
        }
        RaiseCommandStateChanged();
    }

    private static void UpdateCatalogCollection<T, TKey>(ObservableCollection<T> target, IReadOnlyList<T> incoming,
        Func<T, TKey> key) where TKey : notnull
    {
        var keys = incoming.Select(key).ToHashSet();
        for (var index = target.Count - 1; index >= 0; index--)
            if (!keys.Contains(key(target[index]))) target.RemoveAt(index);
        for (var index = 0; index < incoming.Count; index++)
        {
            var wanted = key(incoming[index]);
            if (index >= target.Count || !EqualityComparer<TKey>.Default.Equals(key(target[index]), wanted))
            {
                var existing = -1;
                for (var search = index + 1; search < target.Count; search++)
                    if (EqualityComparer<TKey>.Default.Equals(key(target[search]), wanted)) { existing = search; break; }
                if (existing >= 0) target.Move(existing, index);
                else target.Insert(index, incoming[index]);
            }
            if (!EqualityComparer<T>.Default.Equals(target[index], incoming[index])) target[index] = incoming[index];
        }
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs args) =>
        RunOnUiThread(() =>
        {
            var state = args.State switch
            {
                EnvironmentConnectionState.Connected => CanOperate ? "Ready" : "Read only",
                EnvironmentConnectionState.Connecting => "Connecting",
                EnvironmentConnectionState.Authenticating => "Authenticating",
                EnvironmentConnectionState.Synchronizing => "Synchronizing",
                EnvironmentConnectionState.Retrying => "Reconnecting",
                EnvironmentConnectionState.AuthenticationRequired => "Authentication required",
                EnvironmentConnectionState.Incompatible => "Protocol incompatible",
                EnvironmentConnectionState.TrustRequired => "Verify host identity",
                _ => "Disconnected",
            };
            Connection.Status = $"{EnvironmentLabel} • {state}";
            if (args.Diagnostics is { State: EnvironmentConnectionState.Retrying } diagnostics)
                Connection.Status += $" · attempt {diagnostics.Attempt} · {diagnostics.Failure}";
            OnPropertyChanged(nameof(CanOperate));
            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(IsComposerReadOnly));
            WorkbenchChanges.AllowOperations = CanOperate;
            WorkbenchTerminal.AllowOperations = CanOperate;
            WorkbenchFiles.SetWriteAccess(CanOperate);
            if (args.State == EnvironmentConnectionState.Connected)
            {
                ClearTransportError();
                UpdateThreadSynchronizationStatus();
                _ = RefreshRemoteWorkspaceAsync();
            }
            else if (IsRemote && args.State == EnvironmentConnectionState.AuthenticationRequired && args.Error is PiStation.ClientRuntime.ConnectionValidationException)
            {
                ShowTransportError(args.Error.Message);
            }
            else if (IsRemote && args.State == EnvironmentConnectionState.AuthenticationRequired)
            {
                ShowTransportError("Remote access was rejected. It may have expired or been revoked. In Settings → Connections, pair again using a fresh host link, then select Open; you do not need to forget the saved environment.");
            }
            else if (args.State is EnvironmentConnectionState.Retrying or EnvironmentConnectionState.Disconnected)
            {
                ShowTransportError(args.Error?.Message ??
                    "The desktop lost its connection to the environment. Work already accepted by the host may still be running.");
            }
            else if (args.Error is not null)
            {
                ShowTransportError(args.Error.Message);
            }

            RaiseCommandStateChanged();
        });

    private void OnProjectionChanged(object? sender, ProjectionChangedEventArgs args) =>
        RunOnUiThread(() => ApplyThreadProjection(args.Projection));

    private void OnThreadSynchronizationChanged(object? sender, EventArgs args) => RunOnUiThread(UpdateThreadSynchronizationStatus);

    private void UpdateThreadSynchronizationStatus()
    {
        if (!IsRemote || _client?.ConnectionState != EnvironmentConnectionState.Connected) return;
        var stage = _subscription?.Store.IsSynchronized == false ? "Synchronizing thread" : CanOperate ? "Ready" : "Read only";
        Connection.Status = $"{EnvironmentLabel} • {stage}";
    }

    private void OnTerminalChanged(object? sender, TerminalChangedEventArgs args)
    {
        if (sender is not TerminalStore store)
        {
            return;
        }

        RunOnUiThread(() => WorkbenchTerminal.Apply(
            store.TerminalSessionId,
            args.Descriptor,
            args.Output,
            args.AppendedOutput,
            args.OutputWasReset));
        if (args.Descriptor is { State: not TerminalSessionState.Running, ThreadId: { } threadId } &&
            SelectedThread is { SetupScriptState: SetupScriptState.Running } selectedThread &&
            selectedThread.ThreadId == threadId)
        {
            _ = RefreshThreadMetadataAsync(threadId);
        }
    }

    private async Task RefreshThreadMetadataAsync(ThreadId threadId)
    {
        try
        {
            var refreshed = await RequireClient().GetThreadAsync(threadId).ConfigureAwait(false);
            for (var attempt = 0; attempt < 20 && refreshed.SetupScriptState == SetupScriptState.Running; attempt++)
            {
                await Task.Delay(50).ConfigureAwait(false);
                refreshed = await RequireClient().GetThreadAsync(threadId).ConfigureAwait(false);
            }
            RunOnUiThread(() =>
            {
                var index = Threads.ToList().FindIndex(candidate => candidate.ThreadId == threadId);
                if (index >= 0)
                {
                    Threads[index] = refreshed;
                }

                if (SelectedThread?.ThreadId == threadId)
                {
                    SelectedThread = refreshed;
                    ThreadLifecycleStatus = refreshed.SetupScriptMessage ?? string.Empty;
                }
            });
        }
        catch (Exception exception)
        {
            ReportRuntimeError($"Could not refresh setup-script status: {exception.Message}");
        }
    }

    private async Task WriteWorkbenchTerminalDataAsync(
        TerminalSessionId terminalSessionId,
        string data,
        CancellationToken cancellationToken)
    {
        await _workbenchTerminalOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RequireClient().WriteTerminalInputAsync(
                new WriteTerminalInputRequest(terminalSessionId, data),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _workbenchTerminalOperationGate.Release();
        }
    }

    private void OnPiConfigurationChanged(object? sender, PiConfigurationChangedEventArgs args) =>
        RunOnUiThread(() =>
        {
            if (SelectedThread?.ThreadId != args.ThreadId)
            {
                return;
            }

            if (args.Snapshot is null)
            {
                ClearPiConfiguration("Pi settings unavailable");
            }
            else
            {
                ApplyPiConfiguration(args.Snapshot);
            }
        });

    private void OnComposerPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ComposerViewModel.Text))
        {
            OnPropertyChanged(nameof(PromptText));
            OnPropertyChanged(nameof(CanSend));
        }
        else if (args.PropertyName is nameof(ComposerViewModel.HasAttachments) or nameof(ComposerViewModel.HasRecoveryConflict) or nameof(ComposerViewModel.HasDraft))
        {
            OnPropertyChanged(nameof(CanSend));
            OnPropertyChanged(nameof(IsComposerReadOnly));
        }
        else if (args.PropertyName == nameof(ComposerViewModel.CanAttach))
        {
            OnPropertyChanged(nameof(CanAttachFiles));
        }
    }

    private void OnComposerSaveFailed(object? sender, ComposerSaveFailedEventArgs args) =>
        ReportRuntimeError($"Draft update failed: {args.Exception.Message}");

    private void ApplyThreadProjection(ThreadProjection? projection)
    {
        Thread.ApplyProjection(projection, SelectedThread is not null);
        RaiseCommandStateChanged();
    }

    private void ClearError()
    {
        Connection.ClearRuntimeError();
    }

    private void SetCommandPending(bool value) => RunOnUiThread(() =>
    {
        _commandPending = value;
        RaiseCommandStateChanged();
    });

    private void RaiseCommandStateChanged()
    {
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanRestartPi));
        OnPropertyChanged(nameof(CanManageThreads));
        OnPropertyChanged(nameof(CanAddProject));
        OnPropertyChanged(nameof(CanAttachFiles));
        RaisePiConfigurationStateChanged();
    }

    private Task QueueThreadListRefreshAsync(
        bool debounce,
        CancellationToken cancellationToken = default)
    {
        var project = SelectedProject;
        if (project is null)
        {
            RunOnUiThread(() =>
            {
                Replace(Threads, []);
                ThreadListStatus = "Select a workspace to see its threads";
            });
            return Task.CompletedTask;
        }

        var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previous = Interlocked.Exchange(ref _threadSearchCancellation, refreshCancellation);
        previous?.Cancel();
        var refreshVersion = Interlocked.Increment(ref _threadSearchVersion);
        return RefreshThreadListAsync(
            project.ProjectId,
            ThreadSearchQuery,
            IsShowingArchivedThreads,
            debounce,
            refreshVersion,
            refreshCancellation);
    }

    private async Task RefreshThreadListAsync(
        ProjectId projectId,
        string query,
        bool showArchived,
        bool debounce,
        long refreshVersion,
        CancellationTokenSource refreshCancellation)
    {
        try
        {
            if (debounce)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(225), refreshCancellation.Token)
                    .ConfigureAwait(false);
            }

            IReadOnlyList<ThreadDescriptor> threads;
            var isTruncated = false;
            if (!showArchived && string.IsNullOrEmpty(query))
            {
                threads = await RequireClient().ListThreadsAsync(projectId, refreshCancellation.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                var result = await RequireClient().SearchThreadsAsync(
                    new SearchThreadsRequest(
                        projectId,
                        query,
                        IncludeArchived: showArchived,
                        Limit: ThreadLifecycleDefaults.MaximumSearchLimit),
                    refreshCancellation.Token).ConfigureAwait(false);
                threads = showArchived
                    ? result.Threads.Where(static thread => thread.IsArchived).ToArray()
                    : result.Threads;
                isTruncated = result.IsTruncated;
            }

            RunOnUiThread(() =>
            {
                if (SelectedProject?.ProjectId != projectId ||
                    !string.Equals(ThreadSearchQuery, query, StringComparison.Ordinal) ||
                    IsShowingArchivedThreads != showArchived ||
                    Volatile.Read(ref _threadSearchVersion) != refreshVersion)
                {
                    return;
                }

                Replace(Threads, threads);
                if (SelectedThread is not null &&
                    threads.FirstOrDefault(item => item.ThreadId == SelectedThread.ThreadId) is { } visibleSelected)
                {
                    Workspace.SelectedThread = visibleSelected;
                    OnPropertyChanged(nameof(SelectedThread));
                }

                ThreadListStatus = BuildThreadListStatus(threads.Count, query, showArchived, isTruncated);
                ClearError();
            });
        }
        catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested)
        {
        }
        catch (EnvironmentConnectionException exception)
        {
            RunOnUiThread(() => ThreadListStatus = "Threads are unavailable while disconnected");
            ShowTransportError(exception.Message);
        }
        catch (ThreadSearchException exception)
        {
            RunOnUiThread(() => ThreadListStatus = "Thread search is unavailable");
            ReportRuntimeError($"Could not search threads: {exception.Message}");
        }
        catch (Exception exception)
        {
            RunOnUiThread(() => ThreadListStatus = "Threads are unavailable");
            ReportRuntimeError(exception);
        }
        finally
        {
            Interlocked.CompareExchange(ref _threadSearchCancellation, null, refreshCancellation);
            refreshCancellation.Dispose();
        }
    }

    private async Task<ThreadDescriptor?> ExecuteThreadLifecycleAsync(
        ThreadDescriptor requestedThread,
        Func<IEnvironmentClient, ThreadDescriptor, CancellationToken, Task<ThreadLifecycleUpdateResult>> operation,
        string uncertainMessage,
        CancellationToken cancellationToken)
    {
        if (!CanManageThreads)
        {
            return null;
        }

        var client = RequireClient();
        var current = client.ThreadMetadata.GetCurrent(requestedThread.ThreadId) ?? requestedThread;
        SetCommandPending(true);
        try
        {
            var result = await operation(client, current, cancellationToken).ConfigureAwait(false);
            RunOnUiThread(() => HandleCommandReceipt(result.Receipt));
            if (result.Thread is null)
            {
                ReportRuntimeError($"Thread update ended in state {result.Receipt.State}.");
                return null;
            }

            return result.Thread;
        }
        catch (ThreadLifecycleConflictException exception)
        {
            await QueueThreadListRefreshAsync(debounce: false, CancellationToken.None).ConfigureAwait(false);
            ReportRuntimeError($"The thread changed in another client. The latest metadata was reloaded. {exception.Message}");
        }
        catch (ThreadLifecycleInvalidException exception)
        {
            ReportRuntimeError($"That thread update is invalid. {exception.Message}");
        }
        catch (ThreadLifecycleNotFoundException exception)
        {
            await QueueThreadListRefreshAsync(debounce: false, CancellationToken.None).ConfigureAwait(false);
            ReportRuntimeError($"That thread no longer exists. {exception.Message}");
        }
        catch (CommandDispatchUncertainException exception)
        {
            ShowUncertainCommand(exception.CommandId, uncertainMessage);
        }
        catch (EnvironmentConnectionException exception)
        {
            ShowTransportError(exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportRuntimeError(exception);
        }
        finally
        {
            SetCommandPending(false);
        }

        return null;
    }

    private void ApplySelectedThreadMetadata(ThreadDescriptor thread) => RunOnUiThread(() =>
    {
        if (SelectedThread?.ThreadId == thread.ThreadId)
        {
            SelectedThread = thread;
        }
    });

    private static string BuildThreadListStatus(
        int count,
        string query,
        bool showArchived,
        bool isTruncated)
    {
        if (count == 0)
        {
            if (!string.IsNullOrEmpty(query))
            {
                return showArchived
                    ? $"No archived threads match “{query}”"
                    : $"No threads match “{query}”";
            }

            return showArchived ? "No archived threads" : "No threads yet";
        }

        return isTruncated ? $"Showing the first {count} matches" : string.Empty;
    }

    private void CancelThreadSearch()
    {
        Interlocked.Increment(ref _threadSearchVersion);
        var cancellation = Interlocked.Exchange(ref _threadSearchCancellation, null);
        cancellation?.Cancel();
    }

    private void RaisePiConfigurationStateChanged() =>
        OnPropertyChanged(nameof(CanConfigurePi));

    private async Task LoadPiConfigurationAsync(
        ThreadDescriptor thread,
        CancellationToken cancellationToken)
    {
        var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previous = Interlocked.Exchange(
            ref _piConfigurationLoadCancellation,
            loadCancellation);
        previous?.Cancel();
        try
        {
            var snapshot = await RequireClient()
                .GetThreadPiConfigurationAsync(thread.ThreadId, loadCancellation.Token)
                .ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                if (SelectedThread?.ThreadId == thread.ThreadId)
                {
                    ApplyPiConfiguration(snapshot);
                }
            });
        }
        catch (OperationCanceledException) when (loadCancellation.IsCancellationRequested)
        {
        }
        catch (EnvironmentConnectionException exception)
        {
            RunOnUiThread(() =>
            {
                if (SelectedThread?.ThreadId == thread.ThreadId)
                {
                    ClearPiConfiguration("Pi settings unavailable while disconnected");
                }
            });
            ShowTransportError(exception.Message);
        }
        catch (Exception exception)
        {
            RunOnUiThread(() =>
            {
                if (SelectedThread?.ThreadId == thread.ThreadId)
                {
                    ClearPiConfiguration("Pi settings unavailable");
                }
            });
            ReportRuntimeError($"Could not load Pi settings: {exception.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _piConfigurationLoadCancellation,
                null,
                loadCancellation);
            loadCancellation.Dispose();
        }
    }

    private async Task UpdatePiConfigurationAsync(
        PiModelSelection? model,
        PiThinkingLevel? thinkingLevel,
        CancellationToken cancellationToken)
    {
        var thread = SelectedThread;
        var snapshot = PiConfiguration.Snapshot;
        if (thread is null || snapshot is null || !CanConfigurePi)
        {
            return;
        }

        SetPiConfigurationPending(true);
        try
        {
            var result = await RequireClient().UpdateThreadPiConfigurationAsync(
                thread.ThreadId,
                snapshot.Configuration.Revision,
                model,
                thinkingLevel,
                snapshot.Configuration.RuntimeModeId,
                cancellationToken).ConfigureAwait(false);
            if (result.Snapshot is null)
            {
                throw new InvalidOperationException(
                    $"Pi configuration update ended in state {result.Receipt.State}.");
            }

            RunOnUiThread(() =>
            {
                if (SelectedThread?.ThreadId == thread.ThreadId)
                {
                    ApplyPiConfiguration(result.Snapshot);
                }

                HandleCommandReceipt(result.Receipt);
            });
        }
        catch (PiConfigurationConflictException exception)
        {
            await ReloadPiConfigurationAfterFailureAsync(thread.ThreadId).ConfigureAwait(false);
            ReportRuntimeError(
                $"Pi settings changed in another client. The latest settings were reloaded. " +
                exception.Message);
        }
        catch (PiConfigurationUnsupportedException exception)
        {
            await ReloadPiConfigurationAfterFailureAsync(thread.ThreadId).ConfigureAwait(false);
            ReportRuntimeError(
                $"Pi does not support that setting. The previous settings were restored. " +
                exception.Message);
        }
        catch (PiConfigurationInvalidException exception)
        {
            await ReloadPiConfigurationAfterFailureAsync(thread.ThreadId).ConfigureAwait(false);
            ReportRuntimeError($"That Pi setting is invalid. {exception.Message}");
        }
        catch (CommandDispatchUncertainException exception)
        {
            RestorePiConfigurationControls(thread.ThreadId);
            ShowUncertainCommand(
                exception.CommandId,
                "The host could not confirm whether the Pi settings were saved. Check the receipt before changing them again.",
                thread.ThreadId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RestorePiConfigurationControls(thread.ThreadId);
        }
        catch (EnvironmentConnectionException exception)
        {
            RestorePiConfigurationControls(thread.ThreadId);
            ShowTransportError(exception.Message);
        }
        catch (Exception exception)
        {
            RestorePiConfigurationControls(thread.ThreadId);
            ReportRuntimeError($"Could not update Pi settings: {exception.Message}");
        }
        finally
        {
            SetPiConfigurationPending(false);
        }
    }

    private async Task ReloadPiConfigurationAfterFailureAsync(ThreadId threadId)
    {
        try
        {
            await RefreshPiConfigurationAsync(threadId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            RestorePiConfigurationControls(threadId);
        }
    }

    private async Task RefreshPiConfigurationAsync(
        ThreadId threadId,
        CancellationToken cancellationToken)
    {
        var snapshot = await RequireClient()
            .GetThreadPiConfigurationAsync(threadId, cancellationToken)
            .ConfigureAwait(false);
        RunOnUiThread(() =>
        {
            if (SelectedThread?.ThreadId == threadId)
            {
                ApplyPiConfiguration(snapshot);
            }
        });
    }

    private void ApplyPiConfiguration(ThreadPiConfigurationSnapshot snapshot)
    {
        if (SelectedThread?.ThreadId != snapshot.Configuration.ThreadId)
        {
            return;
        }

        PiConfiguration.Apply(snapshot);
        RaisePiConfigurationStateChanged();
    }

    private void ClearPiConfiguration(string status)
    {
        PiConfiguration.Clear(status);
        RaisePiConfigurationStateChanged();
    }

    private void RestorePiConfigurationControls(ThreadId threadId) => RunOnUiThread(() =>
    {
        if (SelectedThread?.ThreadId == threadId && PiConfiguration.Snapshot is not null)
        {
            ApplyPiConfiguration(PiConfiguration.Snapshot);
        }
    });

    private void SetPiConfigurationPending(bool value) => RunOnUiThread(() =>
    {
        PiConfiguration.IsPending = value;
        if (value)
        {
            PiConfiguration.Status = "Saving Pi settings…";
        }

        RaisePiConfigurationStateChanged();
    });

    private void CancelPiConfigurationLoad()
    {
        var cancellation = Interlocked.Exchange(ref _piConfigurationLoadCancellation, null);
        cancellation?.Cancel();
    }

    private static bool Matches(PiModelSelection? model, PiModelOptionViewModel option) =>
        model is not null &&
        string.Equals(model.ProviderId, option.ProviderId, StringComparison.Ordinal) &&
        string.Equals(model.ModelId, option.ModelId, StringComparison.Ordinal);

    private async Task SearchFileMentionsAsync(
        ProjectId projectId,
        string query,
        CancellationTokenSource searchCancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), searchCancellation.Token).ConfigureAwait(false);
            var result = await RequireClient().SearchProjectFilesAsync(
                new SearchProjectFilesRequest(projectId, query, 12, SelectedThread?.ThreadId),
                searchCancellation.Token).ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                if (!FileMentions.IsOpen ||
                    _fileMentionProjectId != projectId ||
                    !string.Equals(_fileMentionQuery, query, StringComparison.Ordinal))
                {
                    return;
                }

                Replace(FileMentionSuggestions, result.Matches);
                SelectedFileMentionIndex = FileMentionSuggestions.Count == 0 ? -1 : 0;
                FileMentionStatus = FileMentionSuggestions.Count == 0
                    ? "No matching project files"
                    : result.IsTruncated
                        ? "Showing the best 12 matches"
                        : $"{FileMentionSuggestions.Count} project file" +
                          (FileMentionSuggestions.Count == 1 ? string.Empty : "s");
            });
        }
        catch (OperationCanceledException) when (searchCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RunOnUiThread(() =>
            {
                if (_fileMentionProjectId != projectId ||
                    !string.Equals(_fileMentionQuery, query, StringComparison.Ordinal))
                {
                    return;
                }

                Replace(FileMentionSuggestions, []);
                SelectedFileMentionIndex = -1;
                FileMentionStatus = $"File search unavailable: {exception.Message}";
            });
        }
        finally
        {
            Interlocked.CompareExchange(ref _fileMentionSearchCancellation, null, searchCancellation);
            searchCancellation.Dispose();
        }
    }

    private async Task SynchronizeTerminalSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var desiredIds = WorkbenchTerminal.VisibleSessionIds.ToHashSet();
        var descriptors = WorkbenchTerminal.Sessions
            .Select(item => item.Descriptor)
            .Where(descriptor => desiredIds.Contains(descriptor.TerminalSessionId))
            .ToDictionary(descriptor => descriptor.TerminalSessionId);
        var removed = new List<TerminalSubscription>();
        var current = new List<(
            TerminalSessionDescriptor Descriptor,
            TerminalSubscription Subscription,
            bool IsNew)>();

        await _terminalSubscriptionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var pair in _terminalSubscriptions)
            {
                if (!desiredIds.Contains(pair.Key) && _terminalSubscriptions.TryRemove(pair.Key, out var subscription))
                {
                    subscription.Store.Changed -= OnTerminalChanged;
                    removed.Add(subscription);
                }
            }

            foreach (var descriptor in descriptors.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isNew = false;
                if (!_terminalSubscriptions.TryGetValue(descriptor.TerminalSessionId, out var subscription))
                {
                    subscription = RequireClient().SubscribeTerminal(descriptor.TerminalSessionId);
                    subscription.Store.Changed += OnTerminalChanged;
                    _terminalSubscriptions[descriptor.TerminalSessionId] = subscription;
                    isNew = true;
                }

                current.Add((descriptor, subscription, isNew));
            }
        }
        finally
        {
            _terminalSubscriptionGate.Release();
        }

        foreach (var subscription in removed)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }

        await RunOnUiThreadAsync(() =>
        {
            foreach (var (descriptor, subscription, isNew) in current)
            {
                if (isNew)
                {
                    WorkbenchTerminal.Apply(
                        descriptor.TerminalSessionId,
                        subscription.Store.Descriptor ?? descriptor,
                        subscription.Store.Output,
                        outputWasReset: true);
                }
                else
                {
                    WorkbenchTerminal.ApplyDescriptor(subscription.Store.Descriptor ?? descriptor);
                }
            }
        }).ConfigureAwait(false);
    }

    private async Task DetachTerminalSubscriptionAsync(TerminalSessionId terminalSessionId)
    {
        TerminalSubscription? subscription = null;
        await _terminalSubscriptionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_terminalSubscriptions.TryRemove(terminalSessionId, out subscription))
            {
                subscription.Store.Changed -= OnTerminalChanged;
            }
        }
        finally
        {
            _terminalSubscriptionGate.Release();
        }

        if (subscription is not null)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DetachAllTerminalSubscriptionsAsync()
    {
        var subscriptions = new List<TerminalSubscription>();
        await _terminalSubscriptionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var pair in _terminalSubscriptions)
            {
                if (_terminalSubscriptions.TryRemove(pair.Key, out var subscription))
                {
                    subscription.Store.Changed -= OnTerminalChanged;
                    subscriptions.Add(subscription);
                }
            }
        }
        finally
        {
            _terminalSubscriptionGate.Release();
        }

        foreach (var subscription in subscriptions)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    private string GetTerminalOutput(TerminalSessionId terminalSessionId) =>
        _terminalSubscriptions.TryGetValue(terminalSessionId, out var subscription)
            ? subscription.Store.Output
            : string.Empty;

    private async Task ResizeWorkbenchTerminalAsync(
        TerminalSessionId terminalSessionId,
        int columns,
        int rows,
        CancellationTokenSource resizeCancellation)
    {
        try
        {
            await Task.Delay(150, resizeCancellation.Token).ConfigureAwait(false);
            TerminalSessionDescriptor descriptor;
            await _workbenchTerminalOperationGate.WaitAsync(resizeCancellation.Token).ConfigureAwait(false);
            try
            {
                descriptor = await RequireClient().ResizeTerminalSessionAsync(
                    new ResizeTerminalSessionRequest(terminalSessionId, columns, rows),
                    resizeCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                _workbenchTerminalOperationGate.Release();
            }

            RunOnUiThread(() => WorkbenchTerminal.ApplyDescriptor(descriptor));
        }
        catch (OperationCanceledException) when (resizeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RunOnUiThread(() => WorkbenchTerminal.Status = $"Terminal resize failed: {exception.Message}");
        }
        finally
        {
            ((ICollection<KeyValuePair<TerminalSessionId, CancellationTokenSource>>)
                _workbenchTerminalResizeCancellations).Remove(
                new KeyValuePair<TerminalSessionId, CancellationTokenSource>(
                    terminalSessionId,
                    resizeCancellation));
            resizeCancellation.Dispose();
        }
    }

    private Task QueueWorkbenchFileSearchAsync(bool debounce)
    {
        var project = SelectedProject;
        if (project is null)
        {
            CancelWorkbenchFiles();
            RunOnUiThread(() => WorkbenchFiles.SwitchContext(null, hasProject: false));
            return Task.CompletedTask;
        }

        var searchCancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _workbenchFileSearchCancellation, searchCancellation)?.Cancel();
        var query = WorkbenchFiles.SearchQuery;
        RunOnUiThread(() =>
        {
            WorkbenchFiles.SelectedFile = null;
            WorkbenchFiles.SelectedContentMatch = null;
            if (WorkbenchFiles.SearchMode == WorkspaceFileSearchMode.Contents)
            {
                WorkbenchFiles.ContentMatches.Clear();
            }
            else if (!string.IsNullOrEmpty(query))
            {
                WorkbenchFiles.Files.Clear();
            }
            WorkbenchFiles.Status = WorkbenchFiles.SearchMode == WorkspaceFileSearchMode.Contents
                ? string.IsNullOrEmpty(query) ? "Type to search across the workspace" : "Searching file contents…"
                : string.IsNullOrEmpty(query) ? "Loading workspace tree…" : "Searching project files…";
        });
        return SearchWorkbenchFilesAsync(
            project.ProjectId,
            SelectedThread?.ThreadId,
            query,
            WorkbenchFiles.SearchMode,
            WorkbenchFiles.CaseSensitive,
            WorkbenchFiles.WholeWord,
            WorkbenchFiles.UseRegularExpression,
            debounce,
            searchCancellation);
    }

    private async Task SearchWorkbenchFilesAsync(
        ProjectId projectId,
        ThreadId? threadId,
        string query,
        WorkspaceFileSearchMode mode,
        bool caseSensitive,
        bool wholeWord,
        bool useRegularExpression,
        bool debounce,
        CancellationTokenSource searchCancellation)
    {
        try
        {
            if (debounce)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150), searchCancellation.Token).ConfigureAwait(false);
            }

            if (mode == WorkspaceFileSearchMode.Contents)
            {
                if (string.IsNullOrEmpty(query))
                {
                    return;
                }

                var result = await RequireClient().SearchProjectContentsAsync(
                    new SearchProjectContentsRequest(
                        projectId,
                        query,
                        ContentSearchDefaults.MaximumResults,
                        caseSensitive,
                        wholeWord,
                        useRegularExpression,
                        ThreadId: threadId),
                    searchCancellation.Token).ConfigureAwait(false);
                RunOnUiThread(() =>
                {
                    if (!IsCurrentFileSearch(
                            projectId,
                            threadId,
                            query,
                            mode,
                            caseSensitive,
                            wholeWord,
                            useRegularExpression))
                    {
                        return;
                    }

                    WorkbenchFiles.ContentMatches.Clear();
                    foreach (var match in result.Matches)
                    {
                        WorkbenchFiles.ContentMatches.Add(match);
                    }

                    var fileCount = result.Matches.Select(static match => match.RelativePath).Distinct().Count();
                    WorkbenchFiles.Status = result.Matches.Count == 0
                        ? "No content matches"
                        : $"{result.Matches.Count}{(result.IsTruncated ? "+" : string.Empty)} matches in {fileCount} files";
                });
                return;
            }

            if (string.IsNullOrEmpty(query))
            {
                var result = await RequireClient().ListProjectEntriesAsync(
                    new ListProjectEntriesRequest(projectId, ThreadId: threadId),
                    searchCancellation.Token).ConfigureAwait(false);
                RunOnUiThread(() =>
                {
                    if (!IsCurrentFileSearch(
                            projectId,
                            threadId,
                            query,
                            mode,
                            caseSensitive,
                            wholeWord,
                            useRegularExpression))
                    {
                        return;
                    }

                    WorkbenchFiles.ApplyEntries(result.Entries);
                    var fileCount = result.Entries.Count(static entry => !entry.IsDirectory);
                    var directoryCount = result.Entries.Count - fileCount;
                    WorkbenchFiles.Status = result.Entries.Count == 0
                        ? "Workspace has no visible files"
                        : result.IsTruncated
                            ? $"Showing the first {result.Entries.Count:N0} workspace entries"
                            : $"{fileCount:N0} file{(fileCount == 1 ? string.Empty : "s")} • " +
                              $"{directoryCount:N0} folder{(directoryCount == 1 ? string.Empty : "s")}";
                });
                return;
            }

            var pathResult = await RequireClient().SearchProjectFilesAsync(
                new SearchProjectFilesRequest(
                    projectId,
                    query,
                    FileSearchDefaults.MaximumResults,
                    threadId),
                searchCancellation.Token).ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                if (!IsCurrentFileSearch(
                        projectId,
                        threadId,
                        query,
                        mode,
                        caseSensitive,
                        wholeWord,
                        useRegularExpression))
                {
                    return;
                }

                Replace(WorkbenchFiles.Files, pathResult.Matches);
                WorkbenchFiles.Status = pathResult.Matches.Count == 0
                    ? "No matching project files"
                    : pathResult.IsTruncated
                        ? $"Showing the first {pathResult.Matches.Count} project files"
                        : $"{pathResult.Matches.Count} project file" + (pathResult.Matches.Count == 1 ? string.Empty : "s");
            });
        }
        catch (OperationCanceledException) when (searchCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RunOnUiThread(() =>
            {
                if (IsCurrentFileSearch(
                        projectId,
                        threadId,
                        query,
                        mode,
                        caseSensitive,
                        wholeWord,
                        useRegularExpression))
                {
                    WorkbenchFiles.Files.Clear();
                    WorkbenchFiles.ContentMatches.Clear();
                    WorkbenchFiles.Status = $"File browser unavailable: {exception.Message}";
                }
            });
        }
        finally
        {
            Interlocked.CompareExchange(ref _workbenchFileSearchCancellation, null, searchCancellation);
            searchCancellation.Dispose();
        }
    }

    private void ResetWorkbenchFiles(ProjectDescriptor? project)
    {
        CancelWorkbenchFiles();
        var contextKey = project is null
            ? null
            : $"{project.ProjectId.Value}:{SelectedThread?.ThreadId.Value ?? "local"}";
        WorkbenchFiles.SwitchContext(contextKey, project is not null);
        if (project is not null && Layout.SelectedPanel == WorkbenchPanelKind.Files)
        {
            _ = QueueWorkbenchFileSearchAsync(debounce: false);
        }
    }

    private bool IsCurrentFileSearch(
        ProjectId projectId,
        ThreadId? threadId,
        string query,
        WorkspaceFileSearchMode mode,
        bool caseSensitive,
        bool wholeWord,
        bool useRegularExpression) =>
        SelectedProject?.ProjectId == projectId &&
        SelectedThread?.ThreadId == threadId &&
        WorkbenchFiles.SearchMode == mode &&
        WorkbenchFiles.CaseSensitive == caseSensitive &&
        WorkbenchFiles.WholeWord == wholeWord &&
        WorkbenchFiles.UseRegularExpression == useRegularExpression &&
        string.Equals(WorkbenchFiles.SearchQuery, query, StringComparison.Ordinal);

    private async Task LoadWorkbenchChangesAsync(
        ProjectId projectId,
        ThreadId? threadId,
        CancellationTokenSource loadCancellation)
    {
        try
        {
            var result = await RequireClient().GetProjectChangesAsync(
                new GetProjectChangesRequest(projectId, ThreadId: threadId),
                loadCancellation.Token).ConfigureAwait(false);
            var refs = result.IsRepository
                ? await RequireClient().ListGitRefsAsync(
                    new ListGitRefsRequest(new WorkspaceTarget(projectId, threadId)),
                    loadCancellation.Token).ConfigureAwait(false)
                : new ListGitRefsResult([], false, false, null, 0);
            RunOnUiThread(() =>
            {
                if (SelectedProject?.ProjectId == projectId && SelectedThread?.ThreadId == threadId)
                {
                    WorkbenchChanges.Apply(result);
                    WorkbenchChanges.ApplyRefs(refs);
                }
            });
        }
        catch (OperationCanceledException) when (loadCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RunOnUiThread(() =>
            {
                if (SelectedProject?.ProjectId == projectId)
                {
                    WorkbenchChanges.Changes.Clear();
                    WorkbenchChanges.BranchName = "Git unavailable";
                    WorkbenchChanges.BranchDetail = "The repository could not be inspected";
                    WorkbenchChanges.Status = $"Changes unavailable: {exception.Message}";
                    WorkbenchChanges.SourceControlSummary = "Source control unavailable";
                    WorkbenchChanges.ClearDiff();
                }
            });
        }
        finally
        {
            Interlocked.CompareExchange(ref _workbenchChangesLoadCancellation, null, loadCancellation);
            loadCancellation.Dispose();
        }
    }

    private void ResetWorkbenchChanges(ProjectDescriptor? project)
    {
        CancelWorkbenchChanges();
        WorkbenchChanges.Reset(project is not null);
        if (project is not null && Layout.SelectedPanel == WorkbenchPanelKind.Changes)
        {
            _ = RefreshWorkbenchChangesAsync();
        }
    }

    private void ResetWorkbenchTerminal(ProjectDescriptor? project)
    {
        CancelWorkbenchTerminal();
        _ = DetachAllTerminalSubscriptionsAsync();

        WorkbenchTerminal.Reset(project is not null);
        if (project is not null && Layout.SelectedPanel == WorkbenchPanelKind.Terminal)
        {
            _ = RefreshWorkbenchTerminalsAsync();
        }
    }

    private static TerminalSessionDescriptor[] FilterTerminalSessions(
        IReadOnlyList<TerminalSessionDescriptor> sessions,
        ThreadId? threadId) =>
        sessions.Where(session => session.ThreadId == threadId).ToArray();

    private void ResetWorkbenchPreview(ProjectDescriptor? project)
    {
        CancelWorkbenchPreview();
        var contextKey = GetWorkbenchPreviewContextKey();
        WorkbenchPreview.Reset(
            project is not null,
            contextKey is null ? null : Layout.GetPreviewWorkspace(contextKey),
            project is null ? null : Layout.GetPreviewUrl(project.ProjectId.Value));
        if (project is not null &&
            Layout.SelectedPanel == WorkbenchPanelKind.Preview &&
            string.IsNullOrWhiteSpace(WorkbenchPreview.CurrentUrl))
        {
            _ = RefreshWorkbenchPreviewServersAsync();
        }
    }

    private void CancelWorkbenchChanges()
    {
        Interlocked.Exchange(ref _workbenchChangesLoadCancellation, null)?.Cancel();
        Interlocked.Exchange(ref _workbenchDiffLoadCancellation, null)?.Cancel();
    }

    private void CancelWorkbenchFiles()
    {
        Interlocked.Exchange(ref _workbenchFileSearchCancellation, null)?.Cancel();
        Interlocked.Exchange(ref _workbenchFileReadCancellation, null)?.Cancel();
    }

    private void CancelWorkbenchTerminal()
    {
        Interlocked.Exchange(ref _workbenchTerminalCancellation, null)?.Cancel();
        foreach (var cancellation in _workbenchTerminalResizeCancellations.Values)
        {
            cancellation.Cancel();
        }
    }

    private void CancelWorkbenchPreview() =>
        Interlocked.Exchange(ref _workbenchPreviewDiscoveryCancellation, null)?.Cancel();

    private string? GetWorkbenchPreviewContextKey()
    {
        if (SelectedProject is not { } project)
        {
            return null;
        }

        return $"{project.ProjectId.Value}:{SelectedThread?.ThreadId.Value ?? "project"}";
    }

    private void PersistWorkbenchPreview()
    {
        if (GetWorkbenchPreviewContextKey() is { } contextKey)
        {
            Layout.SavePreviewWorkspace(contextKey, WorkbenchPreview.CreatePreference());
        }
    }

    private static string EscapePreviewAnnotation(string? value) =>
        (value ?? string.Empty)
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

    private static string FormatByteLength(long byteLength) => byteLength switch
    {
        < 1024 => $"{byteLength} B",
        < 1024 * 1024 => $"{byteLength / 1024d:0.#} KB",
        _ => $"{byteLength / (1024d * 1024d):0.#} MB",
    };

    private void SetFileMentionSuggestionsOpen(bool value)
    {
        if (FileMentions.IsOpen != value)
        {
            FileMentions.IsOpen = value;
        }
    }

    private void HandleCommandReceipt(CommandReceipt receipt)
    {
        if (receipt.State == CommandReceiptState.DispatchUncertain)
        {
            ShowUncertainCommand(
                receipt.CommandId,
                "The host could not prove whether this command reached Pi. It will not be retried automatically.");
            return;
        }

        if (receipt.State is CommandReceiptState.Failed or CommandReceiptState.Rejected)
        {
            ClearUncertainCommand();
            ReportRuntimeError($"Command {receipt.State}: {receipt.ErrorCode ?? "Unknown error"}.");
            return;
        }

        if (receipt.State == CommandReceiptState.Completed)
        {
            ClearUncertainCommand();
        }

        ClearError();
    }

    private void ShowUncertainCommand(
        CommandId commandId,
        string message,
        ThreadId? piConfigurationThreadId = null) => RunOnUiThread(() =>
    {
        Connection.ShowUncertainCommand(commandId, message, piConfigurationThreadId);
        RaisePiConfigurationStateChanged();
    });

    private void ClearUncertainCommand()
    {
        Connection.ClearUncertainCommand();
        RaisePiConfigurationStateChanged();
    }

    private void ShowTransportError(string message) => RunOnUiThread(() =>
    {
        Connection.ShowTransportError(message);
    });

    private void ClearTransportError()
    {
        Connection.ClearTransportError();
    }

    private IEnvironmentClient RequireClient() =>
        _client ?? throw new InvalidOperationException("The local environment has not started.");

    private Task<ThreadDraft> LoadDraftAsync(ThreadId threadId, CancellationToken cancellationToken) =>
        RequireClient().GetThreadDraftAsync(threadId, cancellationToken);

    private async Task<ThreadDraft> SaveDraftAsync(
        ThreadDraft draft,
        string text,
        CancellationToken cancellationToken)
    {
        var result = await RequireClient().SaveThreadDraftAsync(
            draft.ThreadId,
            draft.DraftId,
            draft.Revision,
            text,
            cancellationToken).ConfigureAwait(false);
        if (result.Receipt.State == CommandReceiptState.DispatchUncertain)
        {
            ShowUncertainCommand(
                result.Receipt.CommandId,
                "The host could not prove whether the draft save completed. Reload the draft before editing further.");
            throw new InvalidOperationException("Draft save status is uncertain.");
        }

        return result.Draft ?? throw new InvalidOperationException(
            $"Draft save ended in state {result.Receipt.State}.");
    }

    private async Task<ThreadDraft> UploadDraftAttachmentAsync(
        ThreadDraft draft,
        string fileName,
        string? mediaType,
        Stream content,
        long byteLength,
        CancellationToken cancellationToken)
    {
        var result = await RequireClient().UploadDraftAttachmentAsync(
            draft.ThreadId,
            draft.DraftId,
            draft.Revision,
            fileName,
            mediaType,
            content,
            byteLength,
            cancellationToken).ConfigureAwait(false);
        if (result.Receipt.State == CommandReceiptState.DispatchUncertain)
        {
            ShowUncertainCommand(
                result.Receipt.CommandId,
                "The host could not prove whether the attachment was stored. Reload the draft before trying again.");
            throw new InvalidOperationException("Attachment upload status is uncertain.");
        }

        if (result.Receipt.State != CommandReceiptState.Completed || result.Attachment is null)
        {
            throw new InvalidOperationException(
                $"Attachment upload ended in state {result.Receipt.State}: " +
                $"{result.Receipt.ErrorCode ?? "the attachment was not associated"}.");
        }

        return result.Draft ?? throw new InvalidOperationException(
            $"Attachment upload ended in state {result.Receipt.State}.");
    }

    private async Task<ThreadDraft> RemoveDraftAttachmentAsync(
        ThreadDraft draft,
        AttachmentId attachmentId,
        CancellationToken cancellationToken)
    {
        var result = await RequireClient().RemoveDraftAttachmentAsync(
            draft.ThreadId,
            draft.DraftId,
            attachmentId,
            draft.Revision,
            cancellationToken).ConfigureAwait(false);
        if (result.Receipt.State == CommandReceiptState.DispatchUncertain)
        {
            ShowUncertainCommand(
                result.Receipt.CommandId,
                "The host could not prove whether the attachment was removed. Reload the draft before trying again.");
            throw new InvalidOperationException("Attachment removal status is uncertain.");
        }

        return result.Draft ?? throw new InvalidOperationException(
            $"Attachment removal ended in state {result.Receipt.State}.");
    }

    private async Task<ThreadDraft> ClearDraftAsync(
        ThreadDraft draft,
        CancellationToken cancellationToken)
    {
        var attachmentIds = draft.Attachments
            .Select(static attachment => attachment.AttachmentId)
            .ToArray();
        var result = await RequireClient().ClearThreadDraftAsync(
            draft.ThreadId,
            draft.DraftId,
            draft.Revision,
            attachmentIds,
            cancellationToken).ConfigureAwait(false);
        if (result.Receipt.State == CommandReceiptState.DispatchUncertain)
        {
            ShowUncertainCommand(
                result.Receipt.CommandId,
                "The host could not prove whether the sent draft was cleared. Reload the draft before editing further.");
            throw new InvalidOperationException("Draft clear status is uncertain.");
        }

        return result.Draft ?? throw new InvalidOperationException(
            $"Draft clear ended in state {result.Receipt.State}: " +
            $"{result.Receipt.ErrorCode ?? "the sent draft was preserved"}.");
    }

    private void RunOnUiThread(Action action)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcherQueue.TryEnqueue(() => action());
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
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            }))
        {
            completion.SetException(new InvalidOperationException("The UI dispatcher is unavailable."));
        }

        return completion.Task;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var replacement = items as IReadOnlyList<T> ?? items.ToArray();
        if (target.SequenceEqual(replacement))
        {
            return;
        }

        target.Clear();
        foreach (var item in replacement)
        {
            target.Add(item);
        }
    }
}
