using System.ComponentModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PiStation.App.ViewModels;
using PiStation.ClientRuntime;
using PiStation.Protocol.Models;
using PreviewAutomationAccess = PiStation.App.ViewModels.PreviewAutomationAccess;

namespace PiStation.App.Views.Controls;

public sealed partial class RightPanelHost
{
    private sealed class BrowserController(PreviewAutomationAccess permission)
    {
        public PreviewAutomationAccess Permission { get; } = permission;
        public string RecordingOwner { get; } = Guid.NewGuid().ToString("N");
        public BrowserAutomationSession? Session { get; set; }
        public DateTimeOffset RetryAfter { get; set; }
        public bool Busy { get; set; }
        public string? TargetTabId { get; set; }
    }

    private void OnBrowserShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.WorkbenchPreview))
        {
            ObserveSelectedBrowser();
            SynchronizeBrowserSurfaces();
        }
        if (e.PropertyName is nameof(ShellViewModel.IsConnected) or nameof(ShellViewModel.CanOperate))
        {
            SynchronizeBrowserSurfaces();
            if (!ViewModel.IsConnected || !ViewModel.CanOperate)
                foreach (var controller in _browserControllers.Values) controller.Session?.Cancel();
            _ = SynchronizeBrowserAutomationPermissionAsync();
        }
    }

    private void ObserveSelectedBrowser()
    {
        if (_observedPreview is not null) _observedPreview.PropertyChanged -= OnWorkbenchPreviewPropertyChanged;
        _observedPreview = ViewModel.WorkbenchPreview;
        _observedPreview.PropertyChanged += OnWorkbenchPreviewPropertyChanged;
    }

    private void OnBrowserWorkspacesChanged(object? sender, EventArgs e)
    {
        foreach (var pair in _browserControllers)
            if (!ViewModel.Browsers.Contains(pair.Key) || pair.Key.Model.AutomationPermission != pair.Value.Permission)
                pair.Value.Session?.Cancel();
        SynchronizeBrowserSurfaces();
        _ = SynchronizeBrowserAutomationPermissionAsync();
    }

    private void SynchronizeBrowserSurfaces()
    {
        if (!_browserAutomationTimer.IsRunning) return;
        var tabs = ViewModel.Browsers.Workspaces.SelectMany(workspace => workspace.Model.Tabs).ToArray();
        foreach (var pair in _previewSurfaces.ToArray())
            if (!tabs.Any(tab => tab.TabId == pair.Key)) ReleasePreviewSurface(pair.Key, pair.Value);
        foreach (var tab in tabs)
        {
            var workspace = ViewModel.Browsers.Find(tab.TabId)!;
            if (!_previewSurfaces.TryGetValue(tab.TabId, out var surface))
            {
                surface = new PreviewWebViewSurface { DataContext = tab };
                surface.SetNavigationContext(tab.TabId);
                surface.Configure(PreviewProfileDataPath(tab.ProfileId),
                    ViewModel.Layout.PreviewDevToolsPolicy == PreviewDevToolsPolicy.UserInitiated,
                    tab.ZoomFactor, tab.ColorScheme, tab.ProfileId == "incognito");
                surface.RemoteRouteFactory = address => ViewModel.OpenPreviewRouteAsync(workspace, address);
                surface.NavigationStarted += OnPreviewNavigationStarted;
                surface.NavigationFinished += OnPreviewNavigationFinished;
                surface.BrowserStateChanged += OnPreviewBrowserStateChanged;
                surface.BrowserFailed += OnPreviewBrowserFailed;
                surface.Loaded += OnRuntimeSurfaceLoaded;
                tab.PropertyChanged += OnRuntimeTabChanged;
                _previewSurfaces.Add(tab.TabId, surface);
            }
            surface.Width = tab.SurfaceWidth;
            surface.Height = tab.SurfaceHeight;
            surface.SetAutomationEnabled(ViewModel.IsConnected && ViewModel.CanOperate && workspace.Model.AutomationPermission != PreviewAutomationAccess.Off);
            var visible = ReferenceEquals(workspace.Model, ViewModel.WorkbenchPreview) && tab.IsActive &&
                !string.IsNullOrWhiteSpace(tab.CurrentUrl) && ViewModel.Layout.IsRightPanelOpen &&
                ViewModel.Layout.SelectedPanel == WorkbenchPanelKind.Preview;
            if (visible)
            {
                if (!ReferenceEquals(PreviewSurfacePresenter.Child, surface))
                {
                    if (PreviewSurfacePresenter.Child is PreviewWebViewSurface previous)
                    {
                        PreviewSurfacePresenter.Child = null;
                        _browserRuntimeHost.Children.Add(previous);
                    }
                    DetachPreviewSurface(surface);
                    PreviewSurfacePresenter.Child = surface;
                }
                PreviewSurfacePresenter.Width = tab.SurfaceWidth + 2;
                PreviewSurfacePresenter.Height = tab.SurfaceHeight + 2;
            }
            else if (!_browserRuntimeHost.Children.Contains(surface))
            {
                DetachPreviewSurface(surface);
                _browserRuntimeHost.Children.Add(surface);
            }
            // Inactive views keep their document. Only a presentation or an operation needs rendering.
            var controller = _browserControllers.GetValueOrDefault(workspace);
            tab.IsRecording = surface.IsRecording;
            surface.Visibility = visible || surface.IsRecording || controller is { Busy: true } && controller.TargetTabId == tab.TabId
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void DetachPreviewSurface(PreviewWebViewSurface surface)
    {
        if (ReferenceEquals(PreviewSurfacePresenter.Child, surface)) PreviewSurfacePresenter.Child = null;
        _browserRuntimeHost.Children.Remove(surface);
    }

    private async void OnRuntimeSurfaceLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is PreviewWebViewSurface { DataContext: WorkbenchPreviewTabViewModel tab } surface &&
            ViewModel.WorkbenchPreview.ActiveTab == tab && ViewModel.Layout.IsRightPanelOpen &&
            ReferenceEquals(PreviewSurfacePresenter.Child, surface) && ViewModel.Layout.SelectedPanel == WorkbenchPanelKind.Preview)
        {
            await NavigateTabIfNeededAsync(tab, surface);
            CompletePreviewLinkLoad(tab, surface);
        }
    }

    private void OnRuntimeTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not WorkbenchPreviewTabViewModel tab || !_previewSurfaces.TryGetValue(tab.TabId, out var surface)) return;
        if (e.PropertyName is nameof(WorkbenchPreviewTabViewModel.SurfaceWidth) or nameof(WorkbenchPreviewTabViewModel.SurfaceHeight)
            or nameof(WorkbenchPreviewTabViewModel.IsActive) or nameof(WorkbenchPreviewTabViewModel.CurrentUrl))
            SynchronizeBrowserSurfaces();
        if (e.PropertyName == nameof(WorkbenchPreviewTabViewModel.ZoomFactor)) surface.SetZoomFactor(tab.ZoomFactor);
        if (e.PropertyName == nameof(WorkbenchPreviewTabViewModel.ColorScheme)) _ = ApplyRuntimeAppearanceAsync(surface, tab);
        if (e.PropertyName == nameof(WorkbenchPreviewTabViewModel.ProfileId))
            surface.Configure(PreviewProfileDataPath(tab.ProfileId),
                ViewModel.Layout.PreviewDevToolsPolicy == PreviewDevToolsPolicy.UserInitiated, tab.ZoomFactor, tab.ColorScheme, tab.ProfileId == "incognito");
    }

    private static async Task ApplyRuntimeAppearanceAsync(PreviewWebViewSurface surface, WorkbenchPreviewTabViewModel tab)
    {
        try { await surface.SetColorSchemeAsync(tab.ColorScheme); }
        catch (Exception error) { tab.CaptureStatus = $"Browser appearance failed: {error.Message}"; }
    }

    private async Task SynchronizeBrowserAutomationPermissionAsync()
    {
        if (!await _browserAutomationGate.WaitAsync(0)) return;
        try
        {
            var desired = _browserAutomationTimer.IsRunning && ViewModel.IsConnected && ViewModel.CanOperate
                ? ViewModel.Browsers.Workspaces.Where(workspace => workspace.ThreadId is not null && workspace.Model.AutomationPermission != PreviewAutomationAccess.Off).ToArray()
                : [];
            foreach (var pair in _browserControllers.ToArray())
            {
                if (desired.Contains(pair.Key) && pair.Key.Model.AutomationPermission == pair.Value.Permission && (pair.Value.Session?.IsActive == true || pair.Value.Session is null && DateTimeOffset.UtcNow < pair.Value.RetryAfter)) continue;
                if (pair.Value.Session is { } previous)
                {
                    foreach (var tab in pair.Key.Model.Tabs)
                        if (_previewSurfaces.TryGetValue(tab.TabId, out var recording) && recording.RecordingOwner == pair.Value.RecordingOwner)
                            await recording.CancelVideoRecordingAsync();
                    if (ViewModel.UiTestFaultControlsVisibility == Visibility.Visible)
                    {
                        Directory.CreateDirectory(ViewModel.PreviewCaptureRoot);
                        File.AppendAllText(System.IO.Path.Combine(ViewModel.PreviewCaptureRoot, "browser-controllers.log"),
                            $"{DateTimeOffset.UtcNow:O} {pair.Key.Key} active={previous.IsActive} wanted={desired.Contains(pair.Key)} permission={pair.Key.Model.AutomationPermission}/{pair.Value.Permission} error={previous.Error}\n");
                    }
                    await previous.DisposeAsync();
                }
                _browserControllers.Remove(pair.Key);
            }
            foreach (var workspace in desired)
            {
                if (_browserControllers.ContainsKey(workspace)) continue;
                var controller = new BrowserController(workspace.Model.AutomationPermission);
                _browserControllers.Add(workspace, controller);
                try
                {
                    controller.Session = await ViewModel.OpenBrowserAutomationAsync(new(workspace.ThreadId!.Value,
                        (BrowserAutomationAccess)controller.Permission), CancellationToken.None);
                    if (!_browserAutomationTimer.IsRunning || !ViewModel.IsConnected || !ViewModel.CanOperate ||
                        !ViewModel.Browsers.Contains(workspace) || workspace.Model.AutomationPermission != controller.Permission)
                        controller.Session.Cancel();
                }
                catch (Exception error)
                {
                    controller.RetryAfter = DateTimeOffset.UtcNow.AddSeconds(5);
                    workspace.Model.SetCaptureStatus(workspace.Model.ActiveTab?.TabId, $"Browser access could not connect: {error.Message}");
                }
            }
        }
        finally { _browserAutomationGate.Release(); }
    }

    private void OnBrowserAutomationTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!ViewModel.IsConnected) return;
        _ = SynchronizeBrowserAutomationPermissionAsync();
        foreach (var pair in _browserControllers.ToArray())
        {
            if (!pair.Value.Busy && pair.Value.Session is { IsActive: true } session && session.TakeNext() is { } work)
            {
                pair.Value.Busy = true;
                SynchronizeBrowserSurfaces();
                _ = HandleBrowserAutomationRequestAsync(pair.Key, pair.Value, session, work);
            }
        }
    }

    private async Task HandleBrowserAutomationRequestAsync(BrowserWorkspace workspace, BrowserController controller,
        BrowserAutomationSession session, BrowserAutomationWork work)
    {
        var model = workspace.Model;
        WorkbenchPreviewTabViewModel? created = null;
        PreviewWebViewSurface? actionSurface = null;
        var startedRecording = false;
        var actionStatus = "failed";
        string? actionError = null;
        try
        {
            var command = BrowserAutomationCommand.Parse(work.Request.Operation, work.Request.Input);
            void ValidateOwner()
            {
                work.CancellationToken.ThrowIfCancellationRequested();
                if (!ViewModel.IsConnected || !ViewModel.CanOperate || !session.IsActive || !ViewModel.Browsers.Contains(workspace) ||
                    _browserControllers.GetValueOrDefault(workspace) != controller)
                    throw new InvalidOperationException("The browser controller is no longer available.");
                if (model.AutomationPermission == PreviewAutomationAccess.Off || command.RequiresInteraction && model.AutomationPermission != PreviewAutomationAccess.Interact)
                    throw new InvalidOperationException("Browser permission was revoked or does not permit interaction.");
            }
            ValidateOwner();
            var targetId = command.TabId ?? workspace.AgentTabId;
            var tab = workspace.ResolveAgentTab(command.TabId);
            if (command.Operation == "recording_stop" && command.TabId is null &&
                (tab is null || _previewSurfaces.GetValueOrDefault(tab.TabId)?.RecordingOwner != controller.RecordingOwner))
            {
                var recordings = model.Tabs.Where(candidate => _previewSurfaces.GetValueOrDefault(candidate.TabId)?.RecordingOwner == controller.RecordingOwner).ToArray();
                if (recordings.Length == 1) tab = recordings[0];
                else throw new InvalidOperationException("Specify the tabId of the recording to stop.");
            }
            if (command.Operation == "open")
            {
                if (command.Url is not null && !WorkbenchPreviewViewModel.TryNormalizeAddress(command.Url, out _, out var error)) throw new ArgumentException(error);
                if (!command.ReuseExistingTab) tab = null;
                else if (targetId is not null && tab is null) throw new InvalidOperationException("The requested tab was closed. Use open with reuseExistingTab=false to create another.");
                if (tab is null)
                {
                    var selected = model.ActiveTab;
                    tab = created = model.AddTab();
                    if (!command.Open) model.ActiveTab = selected;
                }
                if (command.Open)
                {
                    model.ActiveTab = tab;
                    if (ReferenceEquals(model, ViewModel.WorkbenchPreview))
                    {
                        ViewModel.Layout.SelectedPanel = WorkbenchPanelKind.Preview;
                        ViewModel.Layout.IsRightPanelOpen = true;
                    }
                }
                SynchronizeBrowserSurfaces();
            }
            if (tab is null)
            {
                if (command.Operation == "status" && targetId is null)
                {
                    await session.CompleteAsync(work, new(true, JsonSerializer.SerializeToElement(new { available = false, tabs = Array.Empty<object>(), permission = model.AutomationPermission.ToString() })));
                    return;
                }
                throw new InvalidOperationException("The requested browser tab is unavailable. Use open to create a tab, or status with an existing tabId.");
            }
            if (!_previewSurfaces.TryGetValue(tab.TabId, out var surface)) throw new InvalidOperationException("The browser surface is unavailable.");
            controller.TargetTabId = tab.TabId;
            SynchronizeBrowserSurfaces();
            void ValidateTarget()
            {
                ValidateOwner();
                if (!model.Tabs.Contains(tab) || _previewSurfaces.GetValueOrDefault(tab.TabId) != surface)
                    throw new InvalidOperationException("The requested browser tab was closed.");
            }
            ValidateTarget();
            await surface.AutomationGate.WaitAsync(work.CancellationToken);
            actionSurface = surface;
            ValidateTarget();
            surface.AutomationDiagnostics.StartAction(work.Request.Id, command.Operation);
            if (command.Operation is not ("status" or "recording_stop"))
            {
                tab.MarkBrowserStarted();
                await surface.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(command.TimeoutMs), work.CancellationToken);
                ValidateTarget();
                await surface.PrepareAutomationInspectionAsync(ValidateTarget, work.CancellationToken);
                await NavigateTabIfNeededAsync(tab, surface, ValidateTarget);
                ValidateTarget();
                if (tab.IsLoading && command.Operation is not ("navigate" or "open" or "wait" or "resize" or "set_appearance"))
                    throw new InvalidOperationException("The target tab is navigating. Wait for condition 'loaded' before interacting or inspecting.");
            }
            object data;
            switch (command.Operation)
            {
                case "recording_start":
                    await surface.StartVideoRecordingAsync(controller.RecordingOwner, ViewModel.PreviewCaptureRoot,
                        ViewModel.Layout.BrowserDefaults.RecordingFramesPerSecond, session.Stopping, work.CancellationToken);
                    tab.IsRecording = true;
                    startedRecording = true;
                    data = new { tabId = tab.TabId, recording = true, requestedFramesPerSecond = ViewModel.Layout.BrowserDefaults.RecordingFramesPerSecond };
                    break;
                case "recording_stop":
                    var video = await surface.StopVideoRecordingAsync(controller.RecordingOwner, work.CancellationToken);
                    tab.IsRecording = false;
                    ValidateTarget();
                    model.SetCaptureStatus(tab.TabId, $"Recording saved locally • {video.EffectiveFramesPerSecond:F1} FPS • {video.Path}", video.Path);
                    var artifact = await session.UploadRecordingAsync(work, video.Path);
                    data = new { tabId = tab.TabId, recording = false,
                        artifact = JsonSerializer.SerializeToElement(artifact, PiStation.Protocol.Serialization.ProtocolJsonContext.Default.BrowserRecordingArtifact),
                        requestedFramesPerSecond = video.RequestedFramesPerSecond, encodedFrames = video.EncodedFrames,
                        sourceFrames = video.SourceFrames, durationSeconds = video.DurationSeconds, effectiveFramesPerSecond = video.EffectiveFramesPerSecond };
                    model.SetCaptureStatus(tab.TabId, $"Recording saved • {video.EffectiveFramesPerSecond:F1} FPS • {video.Path}", video.Path);
                    break;
                case "status": data = BrowserStatus(workspace, tab, surface); break;
                case "open":
                    if (command.Url is not null) await NavigateAutomationAsync(command.Url, surface, tab, ValidateTarget);
                    data = BrowserStatus(workspace, tab, surface); break;
                case "resize": data = await ResizeAutomationAsync(command, workspace, tab, surface, ValidateTarget, work.CancellationToken); break;
                case "set_appearance": data = await SetAutomationAppearanceAsync(command, workspace, tab, surface, ValidateTarget, work.CancellationToken); break;
                case "evaluate": data = await surface.EvaluateAutomationAsync(command, ValidateTarget, work.CancellationToken); break;
                case "snapshot": data = await surface.CaptureAutomationSnapshotAsync(command.TimeoutMs, ValidateTarget, work.CancellationToken); break;
                case "screenshot": data = await CaptureAutomationScreenshotAsync(surface, tab, ValidateTarget); break;
                case "click": data = JsonSerializer.Deserialize<JsonElement>(await surface.ClickElementAsync(command.Selector!, ValidateTarget)); break;
                case "type": data = JsonSerializer.Deserialize<JsonElement>(await surface.TypeIntoElementAsync(command.Selector!, command.Value!, ValidateTarget)); break;
                case "navigate": data = await NavigateAutomationAsync(command.Url!, surface, tab, ValidateTarget); break;
                case "press_key": data = await surface.PressAutomationKeyAsync(command, ValidateTarget); break;
                case "scroll": data = await surface.ScrollAutomationAsync(command, ValidateTarget); break;
                case "wait": data = await surface.WaitAutomationAsync(command, ValidateTarget, () => tab.IsLoading); break;
                default: throw new InvalidOperationException("Unknown browser operation.");
            }
            ValidateTarget();
            workspace.AgentTabId = tab.TabId;
            ViewModel.Browsers.Persist(workspace);
            await session.CompleteAsync(work, data as BrowserAutomationResult ?? new(true, JsonSerializer.SerializeToElement(data)));
            actionStatus = "succeeded";
            created = null;
        }
        catch (OperationCanceledException) when (work.CancellationToken.IsCancellationRequested) { actionStatus = "interrupted"; }
        catch (Exception error)
        {
            actionStatus = error is OperationCanceledException ? "interrupted" : "failed";
            actionError = error.Message;
            var detail = ViewModel.UiTestFaultControlsVisibility == Visibility.Visible ? error.ToString() : error.Message;
            try { await session.CompleteAsync(work, new(false, Error: detail.Length > 2048 ? detail[..2048] : detail)); }
            catch (Exception) { session.Cancel(); }
        }
        finally
        {
            if (startedRecording && actionStatus != "succeeded" && actionSurface?.RecordingOwner == controller.RecordingOwner)
                await actionSurface.CancelVideoRecordingAsync();
            actionSurface?.AutomationDiagnostics.FinishAction(work.Request.Id, actionStatus, actionError);
            actionSurface?.AutomationGate.Release();
            if (created is not null) { model.CloseTab(created); ViewModel.Browsers.Persist(workspace); }
            controller.Busy = false;
            controller.TargetTabId = null;
            SynchronizeBrowserSurfaces();
        }
    }

    private object BrowserStatus(BrowserWorkspace workspace, WorkbenchPreviewTabViewModel tab, PreviewWebViewSurface surface) => new
    {
        available = surface.IsInitialized, tabId = tab.TabId, tab.DocumentTitle, url = tab.CurrentUrl, tab.IsLoading,
        recording = surface.IsRecording, recordingFinished = surface.RecordingFinished,
        zoomFactor = tab.ZoomFactor, colorScheme = tab.ColorScheme.ToString().ToLowerInvariant(), profileId = tab.ProfileId,
        viewport = tab.ViewportSetting, visible = ReferenceEquals(PreviewSurfacePresenter.Child, surface),
        permission = workspace.Model.AutomationPermission.ToString(),
        tabs = workspace.Model.Tabs.Select(candidate => new { tabId = candidate.TabId, url = candidate.CurrentUrl,
            title = candidate.DocumentTitle, selected = candidate == workspace.Model.ActiveTab, available = _previewSurfaces.GetValueOrDefault(candidate.TabId)?.IsInitialized == true }).ToArray(),
    };

    private async Task<object> ResizeAutomationAsync(BrowserAutomationCommand command, BrowserWorkspace workspace,
        WorkbenchPreviewTabViewModel tab, PreviewWebViewSurface surface, Action validate, CancellationToken cancellationToken)
    {
        var previous = tab.ViewportSetting;
        tab.ApplyAutomationViewport(command.Viewport!);
        var revision = tab.ViewportRevision;
        try
        {
            var measured = await surface.WaitForRenderedBrowserStateAsync(command.TimeoutMs, validate,
                () => (tab.SurfaceWidth, tab.SurfaceHeight), null, cancellationToken);
            validate();
            if (tab.ViewportRevision != revision) throw new InvalidOperationException("The viewport was changed by another action.");
            ViewModel.Browsers.Persist(workspace);
            return new { tabId = tab.TabId, setting = tab.ViewportSetting, viewport = measured };
        }
        catch
        {
            if (ViewModel.Browsers.Contains(workspace) && workspace.Model.Tabs.Contains(tab) && tab.ViewportRevision == revision)
            { tab.ApplyAutomationViewport(previous); ViewModel.Browsers.Persist(workspace); }
            throw;
        }
    }

    private async Task<object> SetAutomationAppearanceAsync(BrowserAutomationCommand command, BrowserWorkspace workspace,
        WorkbenchPreviewTabViewModel tab, PreviewWebViewSurface surface, Action validate, CancellationToken cancellationToken)
    {
        var previous = tab.ColorScheme;
        var scheme = Enum.Parse<PreviewColorScheme>(command.ColorScheme!, true);
        tab.SetColorScheme(scheme);
        var revision = tab.AppearanceRevision;
        try
        {
            await surface.SetColorSchemeAsync(scheme).WaitAsync(TimeSpan.FromMilliseconds(command.TimeoutMs), cancellationToken);
            var measured = await surface.WaitForRenderedBrowserStateAsync(command.TimeoutMs, validate, null, command.ColorScheme, cancellationToken);
            validate();
            if (tab.AppearanceRevision != revision) throw new InvalidOperationException("The appearance was changed by another action.");
            ViewModel.Browsers.Persist(workspace);
            return new { tabId = tab.TabId, colorScheme = command.ColorScheme, rendered = measured };
        }
        catch
        {
            if (ViewModel.Browsers.Contains(workspace) && workspace.Model.Tabs.Contains(tab) && tab.AppearanceRevision == revision)
            { tab.SetColorScheme(previous); ViewModel.Browsers.Persist(workspace); }
            throw;
        }
    }
}
