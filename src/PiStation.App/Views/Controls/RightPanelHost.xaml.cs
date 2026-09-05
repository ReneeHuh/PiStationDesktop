using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PiStation.App.ViewModels;
using PiStation.Protocol.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace PiStation.App.Views.Controls;

public sealed partial class RightPanelHost : UserControl
{
    private readonly HashSet<TerminalWebViewSurface> _terminalInitializationStarted = [];
    private readonly HashSet<PreviewWebViewSurface> _previewInitializationStarted = [];
    private readonly Dictionary<string, PreviewWebViewSurface> _previewSurfaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TerminalPaneVisual> _terminalPaneVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<TerminalWebViewSurface, string> _terminalSurfacePaneIds = [];
    private readonly Dictionary<TerminalPaneResizeHandle, TerminalSplitVisual> _terminalSplitVisuals = [];
    private string[] _terminalCommandGestures = [];
    private bool _isResizing;
    private bool _isTerminalPaneResizing;
    private TerminalPaneResizeHandle? _activeTerminalPaneDivider;
    private int? _pendingTerminalFocusPaneIndex;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _terminalFocusGuardTimer;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private double? _availableWidth;
    private WorkbenchFileDocumentViewModel? _observedFileDocument;
    private WorkbenchFileDocumentViewModel? _revealedFileDocument;
    private int _handledFileRevealRequestId = -1;

    internal const string WorkspaceFileDragFormat = "application/x-pistation-workspace-file";

    public RightPanelHost(ShellViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        RightPanelResizeHandle.Layout = ViewModel.Layout;
        ViewModel.Layout.PropertyChanged += OnLayoutPropertyChanged;
        ViewModel.WorkbenchTerminal.PropertyChanged += OnWorkbenchTerminalPropertyChanged;
        ViewModel.WorkbenchTerminal.SurfaceOutputChanged += OnTerminalSurfaceOutputChanged;
        ViewModel.WorkbenchFiles.PropertyChanged += OnWorkbenchFilesPropertyChanged;
        ViewModel.Workspace.PropertyChanged += OnWorkspacePropertyChanged;
        ApplyPanelWidth();
        SynchronizeTabs();
        ConfigureTerminalPaneLayout();
        UpdateWidthHelpText();
        AttachActiveFileDocument();
    }

    public ShellViewModel ViewModel { get; }

    public event EventHandler<TerminalWebShortcutEventArgs>? CommandGestureRequested;

    public async Task ActivatePanelAsync(WorkbenchPanelKind panel)
    {
        ViewModel.Layout.IsRightPanelOpen = true;
        ViewModel.Layout.SelectedPanel = panel;
        if (panel == WorkbenchPanelKind.Changes)
        {
            await ViewModel.ActivateWorkbenchChangesAsync();
        }
        else if (panel == WorkbenchPanelKind.Files)
        {
            await ViewModel.ActivateWorkbenchFilesAsync();
        }
        else if (panel == WorkbenchPanelKind.Terminal)
        {
            await ViewModel.ActivateWorkbenchTerminalAsync();
        }
        else if (panel == WorkbenchPanelKind.Preview)
        {
            await ViewModel.ActivateWorkbenchPreviewAsync();
            await NavigateToRestoredPreviewAsync();
        }

        SynchronizeTabs();
    }

    public void ReloadPreview() => ActivePreviewSurface?.Reload();

    public void FocusPreviewAddress() => PreviewAddressBox.Focus(FocusState.Programmatic);

    public void OpenTerminalSearch() =>
        TerminalSurface(ViewModel.WorkbenchTerminal.ActivePaneIndex)?.OpenSearch();

    public void SetTerminalCommandGestures(IEnumerable<string> gestures)
    {
        ArgumentNullException.ThrowIfNull(gestures);
        _terminalCommandGestures = gestures.Distinct(StringComparer.Ordinal).Take(128).ToArray();
        foreach (var visual in _terminalPaneVisuals.Values)
        {
            visual.Surface.SetCommandGestures(_terminalCommandGestures);
        }
    }

    public Task SplitTerminalRightAsync() => SplitTerminalAsync(TerminalSplitOrientation.Right);

    public Task SplitTerminalDownAsync() => SplitTerminalAsync(TerminalSplitOrientation.Down);

    public Task CloseTerminalPaneFromCommandAsync() => CloseTerminalPaneAsync();

    public Task FocusPreviousTerminalPaneAsync()
    {
        FocusAdjacentTerminalPane(-1);
        return Task.CompletedTask;
    }

    public Task FocusNextTerminalPaneAsync()
    {
        FocusAdjacentTerminalPane(1);
        return Task.CompletedTask;
    }

    public void SetAvailableWidth(double? width)
    {
        _availableWidth = width.HasValue ? Math.Max(0, width.Value) : null;
        ApplyPanelWidth();
    }

    private async void OnPanelTabClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string panelName } &&
            Enum.TryParse<WorkbenchPanelKind>(panelName, ignoreCase: false, out var panel))
        {
            await ActivatePanelAsync(panel);
        }
    }

    private async void OnPreviewBrowserLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not PreviewWebViewSurface surface ||
            surface.DataContext is not WorkbenchPreviewTabViewModel tab)
        {
            return;
        }

        surface.SetNavigationContext(tab.TabId);
        _previewSurfaces[tab.TabId] = surface;
        surface.NavigationStarted -= OnPreviewNavigationStarted;
        surface.NavigationFinished -= OnPreviewNavigationFinished;
        surface.BrowserStateChanged -= OnPreviewBrowserStateChanged;
        surface.BrowserFailed -= OnPreviewBrowserFailed;
        surface.NavigationStarted += OnPreviewNavigationStarted;
        surface.NavigationFinished += OnPreviewNavigationFinished;
        surface.BrowserStateChanged += OnPreviewBrowserStateChanged;
        surface.BrowserFailed += OnPreviewBrowserFailed;
        if (tab.IsActive && ViewModel.Layout.SelectedPanel == WorkbenchPanelKind.Preview)
        {
            await NavigateTabIfNeededAsync(tab, surface);
        }
    }

    private void OnPreviewBrowserUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not PreviewWebViewSurface surface ||
            surface.DataContext is not WorkbenchPreviewTabViewModel tab ||
            ViewModel.WorkbenchPreview.Tabs.Contains(tab))
        {
            return;
        }

        ReleasePreviewSurface(tab.TabId, surface);
    }

    private async Task EnsurePreviewBrowserInitializedAsync(
        WorkbenchPreviewTabViewModel tab,
        PreviewWebViewSurface surface)
    {
        if (_previewInitializationStarted.Contains(surface) && surface.IsInitialized)
        {
            return;
        }

        _previewInitializationStarted.Add(surface);
        try
        {
            await surface.InitializeAsync();
        }
        catch (Exception exception)
        {
            _previewInitializationStarted.Remove(surface);
            ViewModel.ReportWorkbenchPreviewBrowserFailure(
                tab.TabId,
                PreviewFailureKind.Initialization,
                $"Web preview could not start: {exception.Message}");
        }
    }

    private async Task NavigateToRestoredPreviewAsync()
    {
        var tab = ViewModel.WorkbenchPreview.ActiveTab;
        if (tab is null || !_previewSurfaces.TryGetValue(tab.TabId, out var surface))
        {
            return;
        }

        await NavigateTabIfNeededAsync(tab, surface);
    }

    private async Task NavigateTabIfNeededAsync(
        WorkbenchPreviewTabViewModel tab,
        PreviewWebViewSurface surface)
    {
        if (!WorkbenchPreviewViewModel.TryNormalizeAddress(tab.CurrentUrl, out var uri, out _))
        {
            return;
        }

        await EnsurePreviewBrowserInitializedAsync(tab, surface);
        if (!surface.IsInitialized ||
            string.Equals(surface.CurrentSource, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            surface.SetNavigationContext(tab.TabId);
            await surface.NavigateAsync(uri);
        }
        catch (Exception exception)
        {
            ViewModel.ReportWorkbenchPreviewBrowserFailure(
                tab.TabId,
                PreviewFailureKind.Navigation,
                $"The preview could not navigate: {exception.Message}");
        }
    }

    private async Task NavigatePreviewAsync(string? address)
    {
        var uri = ViewModel.PrepareWorkbenchPreviewNavigation(address);
        var tab = ViewModel.WorkbenchPreview.ActiveTab;
        if (uri is null || tab is null)
        {
            return;
        }

        // A newly-created tab's data template will perform the navigation from
        // its Loaded handler. Existing tabs can navigate immediately.
        if (!_previewSurfaces.TryGetValue(tab.TabId, out var surface))
        {
            return;
        }

        await EnsurePreviewBrowserInitializedAsync(tab, surface);
        if (!surface.IsInitialized)
        {
            return;
        }

        try
        {
            surface.SetNavigationContext(tab.TabId);
            await surface.NavigateAsync(uri);
        }
        catch (Exception exception)
        {
            ViewModel.ReportWorkbenchPreviewBrowserFailure(
                tab.TabId,
                PreviewFailureKind.Navigation,
                $"The preview could not navigate: {exception.Message}");
        }
    }

    private void OnPreviewAddressTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!string.Equals(
                PreviewAddressBox.Text,
                ViewModel.WorkbenchPreview.AddressText,
                StringComparison.Ordinal))
        {
            ViewModel.UpdateWorkbenchPreviewAddress(PreviewAddressBox.Text);
        }
    }

    private async void OnPreviewAddressKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ViewModel.RestoreWorkbenchPreviewAddress();
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await NavigatePreviewAsync(PreviewAddressBox.Text);
        }
    }

    private PreviewWebViewSurface? ActivePreviewSurface =>
        ViewModel.WorkbenchPreview.ActiveTab is { } tab &&
        _previewSurfaces.TryGetValue(tab.TabId, out var surface)
            ? surface
            : null;

    private void OnPreviewBackClicked(object sender, RoutedEventArgs e) => ActivePreviewSurface?.GoBack();

    private void OnPreviewForwardClicked(object sender, RoutedEventArgs e) => ActivePreviewSurface?.GoForward();

    private void OnPreviewReloadStopClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.WorkbenchPreview.IsLoading)
        {
            ActivePreviewSurface?.Stop();
        }
        else
        {
            ActivePreviewSurface?.Reload();
        }
    }

    private async void OnPreviewOpenExternalClicked(object sender, RoutedEventArgs e)
    {
        if (WorkbenchPreviewViewModel.TryNormalizeAddress(
                ViewModel.WorkbenchPreview.CurrentUrl,
                out var uri,
                out _))
        {
            await Launcher.LaunchUriAsync(uri);
        }
    }

    private async void OnPreviewRefreshServersClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshWorkbenchPreviewServersAsync();

    private async void OnPreviewServerClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DiscoveredPreviewServer server)
        {
            await NavigatePreviewAsync(server.Url);
        }
    }

    private async void OnAddPreviewTabClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.AddWorkbenchPreviewTab();
        if (ViewModel.WorkbenchPreview.DiscoveredServers.Count == 0)
        {
            await ViewModel.RefreshWorkbenchPreviewServersAsync();
        }
    }

    private async void OnPreviewTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PreviewTabStrip.SelectedItem is not WorkbenchPreviewTabViewModel tab)
        {
            return;
        }

        ViewModel.SelectWorkbenchPreviewTab(tab);
        await NavigateToRestoredPreviewAsync();
    }

    private async void OnClosePreviewTabClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: WorkbenchPreviewTabViewModel tab })
        {
            return;
        }

        if (_previewSurfaces.TryGetValue(tab.TabId, out var surface))
        {
            ReleasePreviewSurface(tab.TabId, surface);
        }

        ViewModel.CloseWorkbenchPreviewTab(tab);
        if (ViewModel.WorkbenchPreview.ActiveTab is null)
        {
            await ViewModel.RefreshWorkbenchPreviewServersAsync();
        }
        else
        {
            await NavigateToRestoredPreviewAsync();
        }
    }

    private void OnPreviewViewportSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PreviewViewportSelector.SelectedIndex >= 0 &&
            PreviewViewportSelector.SelectedIndex != ViewModel.WorkbenchPreview.ViewportPresetIndex)
        {
            ViewModel.SetWorkbenchPreviewViewport(PreviewViewportSelector.SelectedIndex);
        }
    }

    private void OnRotatePreviewViewportClicked(object sender, RoutedEventArgs e) =>
        ViewModel.RotateWorkbenchPreviewViewport();

    private void OnPreviewBrowserHostSizeChanged(object sender, SizeChangedEventArgs e) =>
        ViewModel.WorkbenchPreview.UpdateResponsiveViewport(
            Math.Max(240, e.NewSize.Width - 2),
            Math.Max(240, e.NewSize.Height - 2));

    private async void OnPreviewCaptureClicked(object sender, RoutedEventArgs e)
    {
        var tab = ViewModel.WorkbenchPreview.ActiveTab;
        var surface = ActivePreviewSurface;
        if (tab is null || surface is null)
        {
            return;
        }

        try
        {
            ViewModel.SetWorkbenchPreviewCaptureStatus(tab.TabId, "Capturing screenshot…");
            var bytes = await surface.CapturePreviewPngAsync();
            var path = await SavePreviewCaptureAsync(bytes, ViewModel.PreviewCaptureRoot);
            ViewModel.SetWorkbenchPreviewCaptureStatus(tab.TabId, $"Screenshot saved • {path}", path);
        }
        catch (Exception exception)
        {
            ViewModel.SetWorkbenchPreviewCaptureStatus(tab.TabId, $"Screenshot failed: {exception.Message}");
        }
    }

    private async void OnRevealPreviewCaptureClicked(object sender, RoutedEventArgs e)
    {
        var path = ViewModel.WorkbenchPreview.LastCapturePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        await Launcher.LaunchFileAsync(file);
    }

    private async void OnPreviewAnnotateClicked(object sender, RoutedEventArgs e)
    {
        var tab = ViewModel.WorkbenchPreview.ActiveTab;
        var surface = ActivePreviewSurface;
        if (tab is null || surface is null)
        {
            return;
        }

        if (tab.IsPickingElement)
        {
            surface.CancelElementPicker();
            tab.IsPickingElement = false;
            tab.CaptureStatus = "Element annotation cancelled";
            return;
        }

        try
        {
            ViewModel.WorkbenchPreview.BeginElementPick();
            var selection = await surface.PickElementAsync();
            if (selection is null)
            {
                tab.IsPickingElement = false;
                tab.CaptureStatus = "Element annotation cancelled";
                return;
            }

            var screenshot = await surface.CapturePreviewPngAsync();
            var annotation = new PreviewElementAnnotation(
                tab.CurrentUrl,
                tab.DocumentTitle,
                selection.ElementLabel,
                selection.Selector,
                selection.Text,
                selection.OuterHtml,
                selection.X,
                selection.Y,
                selection.Width,
                selection.Height,
                screenshot);
            await ViewModel.AddWorkbenchPreviewAnnotationAsync(annotation);
            tab.IsPickingElement = false;
            tab.CaptureStatus = $"Added {selection.ElementLabel} and screenshot to the composer";
        }
        catch (Exception exception)
        {
            tab.IsPickingElement = false;
            tab.CaptureStatus = $"Element annotation failed: {exception.Message}";
        }
    }

    private async void OnPreviewFailureReloadClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel.WorkbenchPreview.FailureKind is
                PreviewFailureKind.BrowserProcess or PreviewFailureKind.Initialization)
            {
                if (ActivePreviewSurface is { } surface)
                {
                    await surface.RecoverAsync();
                }
            }

            await NavigateToRestoredPreviewAsync();
        }
        catch (Exception exception)
        {
            ViewModel.ReportWorkbenchPreviewBrowserFailure(
                ViewModel.WorkbenchPreview.ActiveTab?.TabId,
                PreviewFailureKind.Initialization,
                $"Web preview could not recover: {exception.Message}");
        }
    }

    private async void OnPreviewReturnToServersClicked(object sender, RoutedEventArgs e)
    {
        ActivePreviewSurface?.Stop();
        ViewModel.ReturnWorkbenchPreviewToServers();
        await ViewModel.RefreshWorkbenchPreviewServersAsync();
    }

    private void OnPreviewNavigationStarted(object? sender, PreviewNavigationStartingEventArgs e)
    {
        if (IsCurrentPreviewContext(e.Context))
        {
            ViewModel.ReportWorkbenchPreviewNavigationStarted(e.Context, e.Uri);
        }
    }

    private void OnPreviewNavigationFinished(object? sender, PreviewNavigationCompletedEventArgs e)
    {
        if (IsCurrentPreviewContext(e.Context))
        {
            ViewModel.ReportWorkbenchPreviewNavigationCompleted(e.Context, e.Succeeded, e.Message);
        }
    }

    private void OnPreviewBrowserStateChanged(object? sender, PreviewBrowserStateEventArgs e)
    {
        if (IsCurrentPreviewContext(e.Context))
        {
            ViewModel.ReportWorkbenchPreviewBrowserState(
                e.Context,
                e.Source,
                e.Title,
                e.CanGoBack,
                e.CanGoForward);
        }
    }

    private void OnPreviewBrowserFailed(object? sender, PreviewBrowserFailureEventArgs e)
    {
        if (IsCurrentPreviewContext(e.Context))
        {
            ViewModel.ReportWorkbenchPreviewBrowserFailure(
                e.Context,
                PreviewFailureKind.BrowserProcess,
                e.Message);
        }
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.SelectedProject) &&
            ViewModel.Layout.SelectedPanel == WorkbenchPanelKind.Preview)
        {
            DispatcherQueue.TryEnqueue(async () => await NavigateToRestoredPreviewAsync());
        }
    }

    private bool IsCurrentPreviewContext(string? context) =>
        ViewModel.WorkbenchPreview.FindTab(context) is not null;

    private void ReleasePreviewSurface(string tabId, PreviewWebViewSurface surface)
    {
        surface.NavigationStarted -= OnPreviewNavigationStarted;
        surface.NavigationFinished -= OnPreviewNavigationFinished;
        surface.BrowserStateChanged -= OnPreviewBrowserStateChanged;
        surface.BrowserFailed -= OnPreviewBrowserFailed;
        surface.CancelElementPicker();
        surface.Stop();
        surface.Dispose();
        _previewInitializationStarted.Remove(surface);
        if (_previewSurfaces.TryGetValue(tabId, out var current) && ReferenceEquals(current, surface))
        {
            _previewSurfaces.Remove(tabId);
        }
    }

    private static async Task<string> SavePreviewCaptureAsync(byte[] content, string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"preview-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.png");
        await File.WriteAllBytesAsync(path, content);
        return path;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) =>
        ViewModel.Layout.IsRightPanelOpen = false;

    private void OnFileSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!string.Equals(
                WorkbenchFileSearchInput.Text,
                ViewModel.WorkbenchFiles.SearchQuery,
                StringComparison.Ordinal))
        {
            ViewModel.UpdateWorkbenchFileQuery(WorkbenchFileSearchInput.Text);
        }
    }

    private void OnFileSearchModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorkbenchFileSearchMode.SelectedIndex >= 0 &&
            WorkbenchFileSearchMode.SelectedIndex != ViewModel.WorkbenchFiles.SearchModeIndex)
        {
            ViewModel.UpdateWorkbenchFileSearchMode(WorkbenchFileSearchMode.SelectedIndex);
        }
    }

    private void OnContentSearchOptionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string option })
        {
            ViewModel.ToggleWorkbenchContentSearchOption(option);
        }
    }

    private async void OnRefreshFilesClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshWorkbenchFilesAsync();

    private async void OnWorkbenchFileClicked(object sender, ItemClickEventArgs e) =>
        await ViewModel.SelectWorkbenchFileAsync(e.ClickedItem as ProjectFileMatch);

    private async void OnWorkbenchContentMatchClicked(object sender, ItemClickEventArgs e) =>
        await ViewModel.SelectWorkbenchContentMatchAsync(e.ClickedItem as ProjectContentMatch);

    private async void OnWorkspaceTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args) =>
        await ViewModel.OpenWorkbenchTreeItemAsync(args.InvokedItem as WorkspaceTreeItemViewModel);

    private void OnWorkbenchFileTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorkbenchFileTabs.SelectedItem is WorkbenchFileDocumentViewModel document)
        {
            ViewModel.WorkbenchFiles.ActiveDocument = document;
        }
    }

    private async void OnCloseWorkbenchFileTabClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WorkbenchFileDocumentViewModel document })
        {
            return;
        }

        if (document.IsDirty)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Close {document.FileName}?",
                Content = "This file has unsaved changes.",
                PrimaryButtonText = "Close without saving",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        ViewModel.WorkbenchFiles.CloseDocument(document);
    }

    private async void OnSaveWorkbenchFileClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.SaveWorkbenchFileAsync();

    private async void OnReloadWorkbenchFileClicked(object sender, RoutedEventArgs e)
    {
        var document = ViewModel.WorkbenchFiles.ActiveDocument;
        if (document is null)
        {
            return;
        }

        if (document.IsDirty)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Reload {document.FileName}?",
                Content = "Reloading discards the unsaved changes in this tab.",
                PrimaryButtonText = "Reload",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        await ViewModel.ReloadWorkbenchFileAsync(document);
    }

    private async void OnOpenWorkbenchFileInEditorClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.OpenWorkbenchFileInEditorAsync();

    private void OnWorkspaceTreeItemDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceTreeItemViewModel item } && !item.IsDirectory)
        {
            SetWorkspaceFileDragData(args, item.RelativePath);
        }
    }

    private void OnProjectFileDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: ProjectFileMatch match })
        {
            SetWorkspaceFileDragData(args, match.RelativePath);
        }
    }

    private void OnContentMatchDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: ProjectContentMatch match })
        {
            SetWorkspaceFileDragData(args, match.RelativePath);
        }
    }

    private void SetWorkspaceFileDragData(DragStartingEventArgs args, string relativePath)
    {
        _ = ViewModel;
        args.AllowedOperations = DataPackageOperation.Copy;
        args.Data.RequestedOperation = DataPackageOperation.Copy;
        args.Data.SetData(WorkspaceFileDragFormat, relativePath);
        args.Data.SetText(FormatFileMention(relativePath));
    }

    private static string FormatFileMention(string relativePath) =>
        relativePath.Any(char.IsWhiteSpace)
            ? $"@\"{relativePath.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : $"@{relativePath}";

    private void OnWorkbenchFilesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkbenchFilesViewModel.ActiveDocument) or
            nameof(WorkbenchFilesViewModel.OpenDocuments))
        {
            AttachActiveFileDocument();
        }
    }

    private void AttachActiveFileDocument()
    {
        if (_observedFileDocument is not null)
        {
            _observedFileDocument.PropertyChanged -= OnActiveFileDocumentPropertyChanged;
        }

        _observedFileDocument = ViewModel.WorkbenchFiles.ActiveDocument;
        if (_observedFileDocument is not null)
        {
            _observedFileDocument.PropertyChanged += OnActiveFileDocumentPropertyChanged;
        }

        DispatcherQueue.TryEnqueue(async () =>
        {
            await UpdateWorkspaceImageAsync();
            RevealActiveFileLine();
        });
    }

    private void OnActiveFileDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkbenchFileDocumentViewModel.AssetContent))
        {
            DispatcherQueue.TryEnqueue(async () => await UpdateWorkspaceImageAsync());
        }

        if (e.PropertyName is nameof(WorkbenchFileDocumentViewModel.Content) or
            nameof(WorkbenchFileDocumentViewModel.RevealRequestId) or
            nameof(WorkbenchFileDocumentViewModel.IsLoading))
        {
            DispatcherQueue.TryEnqueue(RevealActiveFileLine);
        }
    }

    private void RevealActiveFileLine()
    {
        var document = ViewModel.WorkbenchFiles.ActiveDocument;
        if (document is null || document.IsLoading || document.RevealLine is not { } line ||
            (ReferenceEquals(_revealedFileDocument, document) &&
             _handledFileRevealRequestId == document.RevealRequestId))
        {
            return;
        }

        var content = WorkbenchFileEditor.Text ?? string.Empty;
        var targetLine = Math.Max(1, line);
        var start = 0;
        for (var current = 1; current < targetLine && start < content.Length; current++)
        {
            var next = content.IndexOf('\n', start);
            start = next < 0 ? content.Length : next + 1;
        }

        var end = content.IndexOf('\n', start);
        if (end < 0)
        {
            end = content.Length;
        }

        WorkbenchFileEditor.SelectionStart = start;
        WorkbenchFileEditor.SelectionLength = Math.Max(0, end - start);
        WorkbenchFileEditor.Focus(FocusState.Programmatic);
        _revealedFileDocument = document;
        _handledFileRevealRequestId = document.RevealRequestId;
    }

    private async Task UpdateWorkspaceImageAsync()
    {
        var document = ViewModel.WorkbenchFiles.ActiveDocument;
        var bytes = document?.AssetContent;
        if (document is null || !document.IsImage || bytes is null)
        {
            WorkbenchFileImage.Source = null;
            return;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            if (Path.GetExtension(document.RelativePath).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var source = new SvgImageSource();
                await source.SetSourceAsync(stream);
                WorkbenchFileImage.Source = source;
            }
            else
            {
                var source = new BitmapImage();
                await source.SetSourceAsync(stream);
                WorkbenchFileImage.Source = source;
            }
        }
        catch (Exception exception)
        {
            document.Status = $"Image preview unavailable: {exception.Message}";
            WorkbenchFileImage.Source = null;
        }
    }

    private async void OnRefreshChangesClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshWorkbenchChangesAsync();

    private async void OnInitializeGitClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.InitializeGitRepositoryAsync();

    private async void OnPullGitBranchClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.PullGitBranchAsync();

    private async void OnSwitchGitBranchClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.SwitchGitBranchAsync();

    private async void OnCreateGitBranchClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.CreateGitBranchAsync();

    private async void OnCommitGitChangesClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.CommitGitChangesAsync();

    private async void OnCommitPushGitChangesClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.CommitAndPushGitChangesAsync();

    private async void OnPushGitBranchClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.PushGitBranchAsync();

    private async void OnRemoveThreadWorktreeClicked(object sender, RoutedEventArgs e)
    {
        var path = ViewModel.WorkbenchChanges.WorktreePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Remove this thread worktree?",
            Content = $"This removes the worktree at:\n{path}\n\nUncommitted changes in that worktree will be discarded. The Git branch is retained.",
            PrimaryButtonText = "Remove worktree",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.RemoveSelectedThreadWorktreeAsync();
        }
    }

    private async void OnWorkbenchChangeSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await ViewModel.SelectWorkbenchChangeAsync(
            WorkbenchChangeList.SelectedItem as WorkbenchChangeItemViewModel);

    private void OnTerminalShellSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TerminalShellSelector.SelectedItem is TerminalShellOption shell)
        {
            ViewModel.WorkbenchTerminal.SelectedShell = shell;
        }
    }

    private async void OnNewTerminalClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.StartWorkbenchTerminalAsync();

    private async void OnTerminalSessionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ViewModel.WorkbenchTerminal.IsBusy &&
            TerminalSessionSelector.FocusState != FocusState.Unfocused &&
            TerminalSessionSelector.SelectedItem is TerminalSessionItemViewModel session &&
            session.Descriptor.TerminalSessionId !=
            ViewModel.WorkbenchTerminal.SelectedSession?.Descriptor.TerminalSessionId)
        {
            await ViewModel.SelectWorkbenchTerminalAsync(session);
        }
    }

    private async void OnSplitTerminalRightClicked(object sender, RoutedEventArgs e) =>
        await SplitTerminalAsync(TerminalSplitOrientation.Right);

    private async void OnSplitTerminalDownClicked(object sender, RoutedEventArgs e) =>
        await SplitTerminalAsync(TerminalSplitOrientation.Down);

    private async void OnCloseTerminalPaneClicked(object sender, RoutedEventArgs e) =>
        await CloseTerminalPaneAsync();

    private async Task SplitTerminalAsync(TerminalSplitOrientation orientation)
    {
        if (!ViewModel.WorkbenchTerminal.CanSplit)
        {
            return;
        }

        await ViewModel.SplitWorkbenchTerminalAsync(orientation);
        if (TerminalSurface(ViewModel.WorkbenchTerminal.ActivePaneIndex) is { } surface)
        {
            await EnsureTerminalWebSurfaceInitializedAsync(surface);
            QueueTerminalFocus(surface);
        }
    }

    private async Task CloseTerminalPaneAsync()
    {
        if (!ViewModel.WorkbenchTerminal.CanClosePane)
        {
            return;
        }

        await ViewModel.CloseWorkbenchTerminalPaneAsync();
        if (TerminalSurface(ViewModel.WorkbenchTerminal.ActivePaneIndex) is { } surface)
        {
            QueueTerminalFocus(surface);
        }
    }

    private async void OnStopTerminalClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.StopWorkbenchTerminalAsync();

    private async void OnRestartTerminalClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.RestartWorkbenchTerminalAsync();

    private void OnClearTerminalClicked(object sender, RoutedEventArgs e) =>
        ViewModel.ClearWorkbenchTerminalOutput();

    private async void OnCloseTerminalClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.CloseWorkbenchTerminalAsync();
        if (TerminalSurface(ViewModel.WorkbenchTerminal.ActivePaneIndex) is { } surface)
        {
            QueueTerminalFocus(surface);
        }
    }

    private void OnTerminalInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!string.Equals(
                TerminalInput.Text,
                ViewModel.WorkbenchTerminal.InputText,
                StringComparison.Ordinal))
        {
            ViewModel.WorkbenchTerminal.InputText = TerminalInput.Text;
        }
    }

    private async void OnTerminalInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || !ViewModel.WorkbenchTerminal.CanSend)
        {
            return;
        }

        e.Handled = true;
        await ViewModel.SendWorkbenchTerminalInputAsync();
    }

    private async void OnSendTerminalInputClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.SendWorkbenchTerminalInputAsync();

    private async void OnTerminalWebSurfaceLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TerminalWebViewSurface surface)
        {
            return;
        }

        await EnsureTerminalWebSurfaceInitializedAsync(surface);
    }

    private async Task EnsureTerminalWebSurfaceInitializedAsync(TerminalWebViewSurface surface)
    {
        if (!_terminalInitializationStarted.Add(surface))
        {
            return;
        }

        try
        {
            await surface.InitializeAsync();
        }
        catch (Exception exception)
        {
            var message = $"Ghostty terminal could not initialize: {exception.Message}";
            _terminalInitializationStarted.Remove(surface);
            surface.ShowFailure(message);
            ViewModel.WorkbenchTerminal.Status = message;
        }
    }

    private async void OnTerminalWebDataReceived(object? sender, TerminalWebDataEventArgs e)
    {
        ActivateTerminalPane(sender);
        await ViewModel.SendWorkbenchTerminalDataAsync(e.Data);
    }

    private void OnTerminalWebResizeRequested(object? sender, TerminalWebResizeEventArgs e) =>
        ViewModel.ResizeWorkbenchTerminalGrid(TerminalPaneIndex(sender), e.Columns, e.Rows);

    private async void OnTerminalWebLinkRequested(object? sender, TerminalWebLinkEventArgs e)
    {
        if (Uri.TryCreate(e.Text, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https")
        {
            await Launcher.LaunchUriAsync(uri);
        }
    }

    private void OnTerminalWebContextMenuRequested(object? sender, TerminalWebContextMenuEventArgs e)
    {
        if (sender is not TerminalWebViewSurface surface)
        {
            return;
        }

        ActivateTerminalPane(surface);
        var menu = new MenuFlyout();
        var findItem = CreateTerminalMenuItem(
            "Find",
            "TerminalFindMenuItem",
            "Ctrl+F");
        findItem.Click += (_, _) => surface.OpenSearch();

        var copyItem = CreateTerminalMenuItem(
            "Copy",
            "TerminalCopyMenuItem",
            "Ctrl+C",
            e.Selection.Length > 0);
        copyItem.Click += (_, _) => CopyTerminalSelection(e.Selection, surface);

        var pasteItem = CreateTerminalMenuItem(
            "Paste",
            "TerminalPasteMenuItem",
            "Ctrl+Shift+V",
            ClipboardContainsText());
        pasteItem.Click += async (_, _) => await PasteTerminalClipboardAsync(surface);

        var selectAllItem = CreateTerminalMenuItem(
            "Select All",
            "TerminalSelectAllMenuItem");
        selectAllItem.Click += (_, _) =>
        {
            surface.SelectAll();
            surface.FocusTerminal();
        };

        var clearItem = CreateTerminalMenuItem(
            "Clear Terminal",
            "TerminalClearMenuItem");
        clearItem.Click += (_, _) =>
        {
            ViewModel.ClearWorkbenchTerminalOutput(TerminalPaneIndex(surface));
            surface.FocusTerminal();
        };

        menu.Items.Add(findItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(copyItem);
        menu.Items.Add(pasteItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(selectAllItem);
        menu.Items.Add(clearItem);
        menu.ShowAt(
            surface,
            new FlyoutShowOptions
            {
                Position = new Point(
                    Math.Clamp(e.X, 0, Math.Max(0, surface.ActualWidth)),
                    Math.Clamp(e.Y, 0, Math.Max(0, surface.ActualHeight))),
            });
    }

    private static MenuFlyoutItem CreateTerminalMenuItem(
        string text,
        string automationId,
        string? shortcut = null,
        bool isEnabled = true)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            IsEnabled = isEnabled,
            KeyboardAcceleratorTextOverride = shortcut ?? string.Empty,
        };
        AutomationProperties.SetAutomationId(item, automationId);
        return item;
    }

    private static bool ClipboardContainsText()
    {
        try
        {
            return Clipboard.GetContent().Contains(StandardDataFormats.Text);
        }
        catch
        {
            return false;
        }
    }

    private void CopyTerminalSelection(string selection, TerminalWebViewSurface surface)
    {
        if (selection.Length == 0)
        {
            return;
        }

        try
        {
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetText(selection);
            Clipboard.SetContent(data);
            Clipboard.Flush();
            surface.FocusTerminal();
        }
        catch (Exception exception)
        {
            ViewModel.WorkbenchTerminal.Status = $"Terminal selection could not be copied: {exception.Message}";
        }
    }

    private async Task PasteTerminalClipboardAsync(TerminalWebViewSurface surface)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (text.Length > 0)
                {
                    surface.Paste(text);
                }
            }

            surface.FocusTerminal();
        }
        catch (Exception exception)
        {
            ViewModel.WorkbenchTerminal.Status = $"Clipboard text could not be pasted: {exception.Message}";
        }
    }

    private void OnTerminalWebReady(object? sender, EventArgs e)
    {
        if (sender is not TerminalWebViewSurface surface)
        {
            return;
        }

        ApplyTerminalWebTheme(surface);
        surface.SetCommandGestures(_terminalCommandGestures);
        var paneIndex = TerminalPaneIndex(surface);
        if (paneIndex >= 0)
        {
            surface.Reset(ViewModel.WorkbenchTerminal.GetPaneOutput(paneIndex));
        }
    }

    private void OnTerminalWebFailed(object? sender, TerminalWebFailureEventArgs e)
    {
        if (sender is TerminalWebViewSurface surface)
        {
            surface.ShowFailure(e.Message);
        }

        ViewModel.WorkbenchTerminal.Status = e.Message;
    }

    private void OnTerminalWebStateChanged(object? sender, TerminalWebStateEventArgs e)
    {
        if (sender is not TerminalWebViewSurface surface)
        {
            return;
        }

        surface.AutomationValue = e.Text;
        AutomationProperties.SetHelpText(
            surface,
            e.MouseTracking
                ? "Application mouse input is active. Hold Shift while selecting terminal text."
                : "Focus the terminal output to send interactive keyboard input.");
        AutomationProperties.SetItemStatus(
            surface,
            e.Text.Length == 0
                ? "No terminal output"
                : $"{e.Text.Length} terminal output characters");
    }

    private void OnTerminalWebSurfaceActualThemeChanged(FrameworkElement sender, object args)
    {
        if (sender is TerminalWebViewSurface surface)
        {
            ApplyTerminalWebTheme(surface);
        }

        UpdateActiveTerminalPaneVisuals();
    }

    private void ApplyTerminalWebTheme()
    {
        foreach (var visual in _terminalPaneVisuals.Values)
        {
            ApplyTerminalWebTheme(visual.Surface);
        }
    }

    private void ApplyTerminalWebTheme(TerminalWebViewSurface surface)
    {
        if (!surface.IsReady)
        {
            return;
        }

        surface.SetTheme(
            ResourceBrushColor("PiTextPrimaryBrush", Windows.UI.Color.FromArgb(255, 212, 212, 212)),
            ResourceBrushColor("PiCanvasBrush", Windows.UI.Color.FromArgb(255, 24, 24, 27)),
            ResourceBrushColor("PiAccentBrush", Windows.UI.Color.FromArgb(255, 124, 156, 255)),
            ResourceBrushColor("PiSelectionBrush", Windows.UI.Color.FromArgb(90, 98, 126, 234)),
            ViewModel.Layout.TerminalFontFamily,
            ViewModel.Layout.TerminalFontSize);
    }

    private void OnTerminalSurfaceOutputChanged(object? sender, TerminalSurfaceOutputEventArgs e)
    {
        var surface = TerminalSurface(e.PaneIndex);
        if (surface is null || !surface.IsReady)
        {
            return;
        }

        if (e.IsReset)
        {
            surface.Reset(e.Text);
        }
        else if (e.Text.Length > 0)
        {
            surface.Write(e.Text);
        }
    }

    private void AttachTerminalSurface(TerminalWebViewSurface surface)
    {
        surface.Loaded += OnTerminalWebSurfaceLoaded;
        surface.ActualThemeChanged += OnTerminalWebSurfaceActualThemeChanged;
        surface.GotFocus += OnTerminalPaneGotFocus;
        surface.DataReceived += OnTerminalWebDataReceived;
        surface.ResizeRequested += OnTerminalWebResizeRequested;
        surface.LinkRequested += OnTerminalWebLinkRequested;
        surface.ContextMenuRequested += OnTerminalWebContextMenuRequested;
        surface.StateChanged += OnTerminalWebStateChanged;
        surface.ShortcutRequested += OnTerminalWebShortcutRequested;
        surface.Ready += OnTerminalWebReady;
        surface.Failed += OnTerminalWebFailed;
    }

    private void DetachTerminalSurface(TerminalWebViewSurface surface)
    {
        surface.Loaded -= OnTerminalWebSurfaceLoaded;
        surface.ActualThemeChanged -= OnTerminalWebSurfaceActualThemeChanged;
        surface.GotFocus -= OnTerminalPaneGotFocus;
        surface.DataReceived -= OnTerminalWebDataReceived;
        surface.ResizeRequested -= OnTerminalWebResizeRequested;
        surface.LinkRequested -= OnTerminalWebLinkRequested;
        surface.ContextMenuRequested -= OnTerminalWebContextMenuRequested;
        surface.StateChanged -= OnTerminalWebStateChanged;
        surface.ShortcutRequested -= OnTerminalWebShortcutRequested;
        surface.Ready -= OnTerminalWebReady;
        surface.Failed -= OnTerminalWebFailed;
    }

    private void OnTerminalWebShortcutRequested(object? sender, TerminalWebShortcutEventArgs e)
    {
        ActivateTerminalPane(sender);
        CommandGestureRequested?.Invoke(this, e);
    }

    private bool FocusAdjacentTerminalPane(int direction)
    {
        var paneCount = ViewModel.WorkbenchTerminal.PaneCount;
        if (paneCount <= 1)
        {
            return false;
        }

        var current = ViewModel.WorkbenchTerminal.ActivePaneIndex;
        var next = (current + direction + paneCount) % paneCount;
        return FocusTerminalPane(next);
    }

    private bool FocusTerminalPane(int paneIndex)
    {
        if (paneIndex < 0 || paneIndex >= ViewModel.WorkbenchTerminal.PaneCount ||
            TerminalSurface(paneIndex) is not { } surface)
        {
            return false;
        }

        ViewModel.ActivateWorkbenchTerminalPane(paneIndex);
        QueueTerminalFocus(surface);
        return true;
    }

    private void OnTerminalPaneGotFocus(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.Layout.IsRightPanelOpen ||
            ViewModel.Layout.SelectedPanel != WorkbenchPanelKind.Terminal ||
            !ReferenceEquals(e.OriginalSource, sender))
        {
            return;
        }

        var paneIndex = TerminalPaneIndex(sender);
        if (paneIndex < 0)
        {
            return;
        }
        if (_pendingTerminalFocusPaneIndex is { } pendingPaneIndex && paneIndex != pendingPaneIndex)
        {
            return;
        }

        ActivateTerminalPane(sender);
    }

    private void ActivateTerminalPane(object? sender)
    {
        if (sender is not TerminalWebViewSurface surface)
        {
            return;
        }

        var paneIndex = TerminalPaneIndex(surface);
        if (paneIndex < 0)
        {
            return;
        }

        ViewModel.ActivateWorkbenchTerminalPane(paneIndex);
        UpdateActiveTerminalPaneVisuals();
    }

    private int TerminalPaneIndex(object? surface) =>
        surface is TerminalWebViewSurface terminalSurface &&
        _terminalSurfacePaneIds.TryGetValue(terminalSurface, out var paneId)
            ? ViewModel.WorkbenchTerminal.GetPaneIndex(paneId)
            : -1;

    private TerminalWebViewSurface? TerminalSurface(int paneIndex) =>
        ViewModel.WorkbenchTerminal.GetPaneId(paneIndex) is { } paneId &&
        _terminalPaneVisuals.TryGetValue(paneId, out var visual)
            ? visual.Surface
            : null;

    private void QueueTerminalFocus(TerminalWebViewSurface surface)
    {
        var paneIndex = TerminalPaneIndex(surface);
        if (paneIndex < 0)
        {
            return;
        }

        GuardTerminalPaneFocus(paneIndex);
        DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.ActivateWorkbenchTerminalPane(paneIndex);
            surface.FocusTerminal();
        });
    }

    private void GuardTerminalPaneFocus(int paneIndex)
    {
        _terminalFocusGuardTimer?.Stop();
        _pendingTerminalFocusPaneIndex = paneIndex;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(750);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (ReferenceEquals(_terminalFocusGuardTimer, timer))
            {
                _pendingTerminalFocusPaneIndex = null;
                _terminalFocusGuardTimer = null;
            }
        };
        _terminalFocusGuardTimer = timer;
        timer.Start();
    }

    private void OnWorkbenchTerminalPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkbenchTerminalViewModel.LayoutRoot))
        {
            ConfigureTerminalPaneLayout();
        }
        else if (e.PropertyName is nameof(WorkbenchTerminalViewModel.ActivePaneIndex) or
                 nameof(WorkbenchTerminalViewModel.IsSplit) or
                 nameof(WorkbenchTerminalViewModel.PaneCount))
        {
            SynchronizeTerminalCommandGestures();
            UpdateActiveTerminalPaneVisuals();
        }
    }

    private void ConfigureTerminalPaneLayout()
    {
        GuardTerminalPaneFocus(ViewModel.WorkbenchTerminal.ActivePaneIndex);
        foreach (var visual in _terminalPaneVisuals.Values)
        {
            visual.Border.Child = null;
        }

        TerminalPaneHost.Children.Clear();
        _terminalSplitVisuals.Clear();

        var layout = ViewModel.WorkbenchTerminal.LayoutRoot;
        var retainedPaneIds = new HashSet<string>(StringComparer.Ordinal);
        CollectTerminalPaneIds(layout, retainedPaneIds);
        foreach (var paneId in _terminalPaneVisuals.Keys.Where(id => !retainedPaneIds.Contains(id)).ToArray())
        {
            RemoveTerminalPaneVisual(paneId);
        }

        var splitOrdinal = 0;
        TerminalPaneHost.Children.Add(BuildTerminalPaneElement(layout, ref splitOrdinal));
        SynchronizeTerminalCommandGestures();
        UpdateActiveTerminalPaneVisuals();
    }

    private FrameworkElement BuildTerminalPaneElement(TerminalPaneLayoutNodeSnapshot node, ref int splitOrdinal)
    {
        if (node.PaneIndex is { } paneIndex)
        {
            return BuildTerminalPaneLeaf(node.NodeId, paneIndex);
        }

        var orientation = node.SplitOrientation ?? TerminalSplitOrientation.Right;
        var grid = new Grid();
        var first = BuildTerminalPaneElement(node.First!, ref splitOrdinal);
        var dividerOrdinal = splitOrdinal++;
        var divider = new TerminalPaneResizeHandle
        {
            Background = new SolidColorBrush(ResourceBrushColor(
                "PiDividerBrush",
                Windows.UI.Color.FromArgb(255, 44, 44, 48))),
            IsTabStop = true,
        };
        AutomationProperties.SetAutomationId(
            divider,
            dividerOrdinal == 0 ? "TerminalPaneResizeHandle" : $"TerminalPaneResizeHandle{dividerOrdinal + 1}");
        AutomationProperties.SetName(divider, $"Resize terminal pane group {dividerOrdinal + 1}");
        ToolTipService.SetToolTip(divider, "Resize terminal panes");
        divider.KeyDown += OnTerminalPaneDividerKeyDown;
        divider.PointerCaptureLost += OnTerminalPaneDividerPointerCaptureLost;
        divider.PointerMoved += OnTerminalPaneDividerPointerMoved;
        divider.PointerPressed += OnTerminalPaneDividerPointerPressed;
        divider.PointerReleased += OnTerminalPaneDividerPointerReleased;
        divider.RatioChanged += OnTerminalPaneRatioChanged;
        var second = BuildTerminalPaneElement(node.Second!, ref splitOrdinal);

        TerminalSplitVisual visual;
        if (orientation == TerminalSplitOrientation.Right)
        {
            var firstDefinition = new ColumnDefinition();
            var secondDefinition = new ColumnDefinition();
            grid.ColumnDefinitions.Add(firstDefinition);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            grid.ColumnDefinitions.Add(secondDefinition);
            Grid.SetColumn(first, 0);
            Grid.SetColumn(divider, 1);
            Grid.SetColumn(second, 2);
            visual = new TerminalSplitVisual(
                node.NodeId,
                orientation,
                grid,
                divider,
                firstDefinition,
                secondDefinition,
                null,
                null);
        }
        else
        {
            var firstDefinition = new RowDefinition();
            var secondDefinition = new RowDefinition();
            grid.RowDefinitions.Add(firstDefinition);
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            grid.RowDefinitions.Add(secondDefinition);
            Grid.SetRow(first, 0);
            Grid.SetRow(divider, 1);
            Grid.SetRow(second, 2);
            visual = new TerminalSplitVisual(
                node.NodeId,
                orientation,
                grid,
                divider,
                null,
                null,
                firstDefinition,
                secondDefinition);
        }

        grid.Children.Add(first);
        grid.Children.Add(divider);
        grid.Children.Add(second);
        _terminalSplitVisuals[divider] = visual;
        ApplyTerminalSplitRatio(visual, node.SplitRatio);
        UpdateTerminalPaneDividerHelpText(visual);
        return grid;
    }

    private Border BuildTerminalPaneLeaf(string paneId, int paneIndex)
    {
        if (!_terminalPaneVisuals.TryGetValue(paneId, out var visual))
        {
            var surface = new TerminalWebViewSurface
            {
                Background = new SolidColorBrush(ResourceBrushColor(
                    "PiCanvasBrush",
                    Windows.UI.Color.FromArgb(255, 24, 24, 27))),
            };
            AttachTerminalSurface(surface);
            _terminalSurfacePaneIds[surface] = paneId;
            visual = new TerminalPaneVisual(surface);
            _terminalPaneVisuals[paneId] = visual;
        }

        var paneNumber = paneIndex + 1;
        var border = new Border
        {
            MinWidth = 80,
            MinHeight = 64,
            Background = new SolidColorBrush(ResourceBrushColor(
                "PiCanvasBrush",
                Windows.UI.Color.FromArgb(255, 24, 24, 27))),
            BorderBrush = new SolidColorBrush(ResourceBrushColor(
                "PiBorderBrush",
                Windows.UI.Color.FromArgb(255, 63, 63, 70))),
            BorderThickness = new Thickness(1),
            Child = visual.Surface,
        };
        visual.Border = border;
        AutomationProperties.SetAutomationId(border, paneIndex switch
        {
            0 => "PrimaryTerminalPane",
            1 => "SecondaryTerminalPane",
            _ => $"TerminalPane{paneNumber}",
        });
        AutomationProperties.SetName(border, $"Terminal pane {paneNumber}");
        AutomationProperties.SetAutomationId(visual.Surface, paneIndex switch
        {
            0 => "TerminalOutput",
            1 => "TerminalOutputSecondary",
            _ => $"TerminalOutput{paneNumber}",
        });
        AutomationProperties.SetName(visual.Surface, $"Terminal output {paneNumber}");
        AutomationProperties.SetHelpText(
            visual.Surface,
            "Focus the terminal output to activate this pane and send interactive keyboard input.");
        return border;
    }

    private static void CollectTerminalPaneIds(
        TerminalPaneLayoutNodeSnapshot node,
        HashSet<string> paneIds)
    {
        if (node.IsLeaf)
        {
            paneIds.Add(node.NodeId);
            return;
        }

        CollectTerminalPaneIds(node.First!, paneIds);
        CollectTerminalPaneIds(node.Second!, paneIds);
    }

    private void RemoveTerminalPaneVisual(string paneId)
    {
        if (!_terminalPaneVisuals.Remove(paneId, out var visual))
        {
            return;
        }

        _terminalInitializationStarted.Remove(visual.Surface);
        _terminalSurfacePaneIds.Remove(visual.Surface);
        DetachTerminalSurface(visual.Surface);
        visual.Surface.Dispose();
    }

    private void SynchronizeTerminalCommandGestures()
    {
        foreach (var visual in _terminalPaneVisuals.Values)
        {
            visual.Surface.SetCommandGestures(_terminalCommandGestures);
        }
    }

    private void UpdateActiveTerminalPaneVisuals()
    {
        var activePane = ViewModel.WorkbenchTerminal.ActivePaneIndex;
        var accent = new SolidColorBrush(ResourceBrushColor(
            "PiAccentBrush",
            Windows.UI.Color.FromArgb(255, 124, 156, 255)));
        var border = new SolidColorBrush(ResourceBrushColor(
            "PiBorderBrush",
            Windows.UI.Color.FromArgb(255, 63, 63, 70)));
        foreach (var (paneId, visual) in _terminalPaneVisuals)
        {
            var paneIndex = ViewModel.WorkbenchTerminal.GetPaneIndex(paneId);
            visual.Border.BorderBrush = paneIndex == activePane ? accent : border;
            visual.Border.BorderThickness = new Thickness(paneIndex == activePane ? 2 : 1);
        }
    }

    private static Windows.UI.Color ResourceBrushColor(string key, Windows.UI.Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is SolidColorBrush brush
            ? brush.Color
            : fallback;

    private void OnTerminalPaneRatioChanged(object? sender, TerminalPaneRatioChangedEventArgs e)
    {
        if (sender is not TerminalPaneResizeHandle divider ||
            !_terminalSplitVisuals.TryGetValue(divider, out var visual))
        {
            return;
        }

        GuardTerminalPaneFocus(ViewModel.WorkbenchTerminal.ActivePaneIndex);
        ViewModel.ResizeWorkbenchTerminalSplit(visual.NodeId, e.Ratio);
        ApplyTerminalSplitRatio(visual, e.Ratio);
        UpdateTerminalPaneDividerHelpText(visual);
        if (e.Commit)
        {
            ViewModel.CommitWorkbenchTerminalSplit();
        }
    }

    private void OnTerminalPaneDividerPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not TerminalPaneResizeHandle divider || !_terminalSplitVisuals.ContainsKey(divider))
        {
            return;
        }

        _activeTerminalPaneDivider = divider;
        _isTerminalPaneResizing = divider.CapturePointer(e.Pointer);
        if (_isTerminalPaneResizing)
        {
            e.Handled = true;
        }
    }

    private void OnTerminalPaneDividerPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isTerminalPaneResizing)
        {
            return;
        }

        if (_activeTerminalPaneDivider is { } divider)
        {
            SetTerminalPaneRatioFromPointer(divider, e, commit: false);
        }
        e.Handled = true;
    }

    private void OnTerminalPaneDividerPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isTerminalPaneResizing)
        {
            return;
        }

        if (_activeTerminalPaneDivider is not { } divider)
        {
            return;
        }

        SetTerminalPaneRatioFromPointer(divider, e, commit: true);
        _isTerminalPaneResizing = false;
        _activeTerminalPaneDivider = null;
        divider.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnTerminalPaneDividerPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isTerminalPaneResizing)
        {
            return;
        }

        _isTerminalPaneResizing = false;
        if (sender is TerminalPaneResizeHandle divider && _terminalSplitVisuals.TryGetValue(divider, out var visual))
        {
            divider.SetRatio(ViewModel.WorkbenchTerminal.GetSplitRatio(visual.NodeId), commit: true);
        }
        _activeTerminalPaneDivider = null;
    }

    private void OnTerminalPaneDividerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TerminalPaneResizeHandle divider ||
            !_terminalSplitVisuals.TryGetValue(divider, out var visual))
        {
            return;
        }

        var current = ViewModel.WorkbenchTerminal.GetSplitRatio(visual.NodeId);
        var next = e.Key switch
        {
            VirtualKey.Left or VirtualKey.Up => current - 0.05,
            VirtualKey.Right or VirtualKey.Down => current + 0.05,
            VirtualKey.Home => WorkbenchTerminalViewModel.MinimumSplitRatio,
            VirtualKey.End => WorkbenchTerminalViewModel.MaximumSplitRatio,
            _ => current,
        };
        if (Math.Abs(next - current) < 0.0001)
        {
            return;
        }

        e.Handled = true;
        divider.SetRatio(next, commit: true);
    }

    private void SetTerminalPaneRatioFromPointer(
        TerminalPaneResizeHandle divider,
        PointerRoutedEventArgs e,
        bool commit)
    {
        if (!_terminalSplitVisuals.TryGetValue(divider, out var visual))
        {
            return;
        }

        var position = e.GetCurrentPoint(visual.Grid).Position;
        var isRightSplit = visual.Orientation == TerminalSplitOrientation.Right;
        var extent = isRightSplit ? visual.Grid.ActualWidth : visual.Grid.ActualHeight;
        var coordinate = isRightSplit ? position.X : position.Y;
        var usableExtent = Math.Max(1, extent - 6);
        divider.SetRatio((coordinate - 3) / usableExtent, commit);
    }

    private static void ApplyTerminalSplitRatio(TerminalSplitVisual visual, double ratio)
    {
        var normalized = WorkbenchTerminalViewModel.NormalizeSplitRatio(ratio);
        visual.Divider.SynchronizeRatio(normalized);
        if (visual.Orientation == TerminalSplitOrientation.Right)
        {
            visual.FirstColumn!.Width = new GridLength(normalized, GridUnitType.Star);
            visual.SecondColumn!.Width = new GridLength(1 - normalized, GridUnitType.Star);
        }
        else
        {
            visual.FirstRow!.Height = new GridLength(normalized, GridUnitType.Star);
            visual.SecondRow!.Height = new GridLength(1 - normalized, GridUnitType.Star);
        }
    }

    private static void UpdateTerminalPaneDividerHelpText(TerminalSplitVisual visual)
    {
        var leadingPane = visual.Orientation == TerminalSplitOrientation.Right ? "left" : "top";
        AutomationProperties.SetHelpText(
            visual.Divider,
            $"{Math.Round(visual.Divider.Ratio * 100)} percent assigned to the {leadingPane} pane");
    }

    private void OnResizePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isResizing = RightPanelResizeHandle.CapturePointer(e.Pointer);
        if (!_isResizing)
        {
            return;
        }

        _resizeStartX = e.GetCurrentPoint(null).Position.X;
        _resizeStartWidth = ViewModel.Layout.RightPanelWidth;
        e.Handled = true;
    }

    private void OnResizePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing)
        {
            return;
        }

        var currentX = e.GetCurrentPoint(null).Position.X;
        ViewModel.Layout.ResizeRightPanel(_resizeStartWidth + _resizeStartX - currentX);
        UpdateWidthHelpText();
        e.Handled = true;
    }

    private void OnResizePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing)
        {
            return;
        }

        _isResizing = false;
        RightPanelResizeHandle.ReleasePointerCapture(e.Pointer);
        ViewModel.Layout.CommitRightPanelWidth();
        UpdateWidthHelpText();
        e.Handled = true;
    }

    private void OnResizePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing)
        {
            return;
        }

        _isResizing = false;
        ViewModel.Layout.CommitRightPanelWidth();
        UpdateWidthHelpText();
    }

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellLayoutViewModel.SelectedPanel))
        {
            SynchronizeTabs();
        }
        else if (e.PropertyName == nameof(ShellLayoutViewModel.RightPanelWidth))
        {
            ApplyPanelWidth();
            UpdateWidthHelpText();
        }
        else if (e.PropertyName is nameof(ShellLayoutViewModel.TerminalFontFamily) or
                 nameof(ShellLayoutViewModel.TerminalFontSize))
        {
            ApplyTerminalWebTheme();
        }
    }

    private void SynchronizeTabs()
    {
        var selected = ViewModel.Layout.SelectedPanel;
        ChangesPanelTab.IsChecked = selected == WorkbenchPanelKind.Changes;
        FilesPanelTab.IsChecked = selected == WorkbenchPanelKind.Files;
        TerminalPanelTab.IsChecked = selected == WorkbenchPanelKind.Terminal;
        PreviewPanelTab.IsChecked = selected == WorkbenchPanelKind.Preview;
        AgentsPanelTab.IsChecked = selected == WorkbenchPanelKind.Agents;
    }

    private void UpdateWidthHelpText() => AutomationProperties.SetHelpText(
        RightPanelResizeHandle,
        $"Workbench width {Math.Round(ViewModel.Layout.RightPanelWidth)} pixels");

    private void ApplyPanelWidth() => Root.Width = _availableWidth is { } availableWidth
        ? Math.Min(ViewModel.Layout.RightPanelWidth, availableWidth)
        : ViewModel.Layout.RightPanelWidth;

    private sealed class TerminalPaneVisual(TerminalWebViewSurface surface)
    {
        public TerminalWebViewSurface Surface { get; } = surface;

        public Border Border { get; set; } = null!;
    }

    private sealed record TerminalSplitVisual(
        string NodeId,
        TerminalSplitOrientation Orientation,
        Grid Grid,
        TerminalPaneResizeHandle Divider,
        ColumnDefinition? FirstColumn,
        ColumnDefinition? SecondColumn,
        RowDefinition? FirstRow,
        RowDefinition? SecondRow);
}
