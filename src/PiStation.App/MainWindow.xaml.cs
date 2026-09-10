using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using PiStation.App.ViewModels;
using PiStation.App.Views;
using PiStation.App.Views.Controls;
using Windows.Graphics;

namespace PiStation.App;

/// <summary>
/// Hosts the Pi Station Desktop shell.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly GridLength _expandedSidebarWidth;
    private readonly ShellViewModel _viewModel;
    private readonly int _textScalePercent;
    private readonly ShellPage _shellPage;
    private readonly ChatHeader _chatHeader;
    private bool _discardEditsOnClose;
    private bool _closeDialogOpen;
    private bool _allowClose;
    private Task? _closeTask;
    internal Func<Task>? BeforeCloseAsync { get; set; }
    private async void OnCloseTemporarySessionClicked(object sender, RoutedEventArgs e)
    {
        try { if (await ConfirmPlanDiscardAsync()) await CloseWithRecoveryAsync(); }
        catch (Exception error) { _viewModel.ReportRuntimeError(error); }
    }
    internal void ReleaseSurfaces() => _shellPage.Release();
    internal void MarkTemporary()
    {
        TitleBarBrandText.Text = "Temporary";
        TemporarySessionNotice.Visibility = Visibility.Visible;
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(TitleBarBrandText,
            "Conversation and draft are discarded when this window closes. Project edits and explicit exports remain.");
    }

    public MainWindow(ShellViewModel viewModel, int textScalePercent = 100)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        _textScalePercent = textScalePercent;
        InitializeComponent();
        _viewModel.ObserveWindowVisibility(() => AppWindow.IsVisible &&
            AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized });
        InitializeQuitGesture();
        _expandedSidebarWidth = TitleBarSidebarColumn.Width;
        ApplyTheme();
        InitializeAppearance();
        _viewModel.Layout.PropertyChanged += OnLayoutPropertyChanged;
        Closed += OnMainWindowClosed;
        Activated += OnReadWindowActivated;
        Activated += OnWindowActivated;
        AppWindow.Closing += OnAppWindowClosing;
        RootGrid.Loaded += OnRootGridLoaded;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1200, 800));

        _shellPage = new ShellPage(viewModel);
        _shellPage.SidebarCollapsedChanged += OnSidebarCollapsedChanged;
        _chatHeader = new ChatHeader(viewModel);
        _chatHeader.CommandPaletteRequested += OnCommandPaletteRequested;
        TitleBarChatHeaderHost.Content = _chatHeader;
        RootContent.Content = _shellPage;
        ApplySidebarState(_shellPage);
    }

    internal async Task PrepareForTransitionAsync()
    {
        if (!_discardEditsOnClose && _viewModel.Plan.UnsavedPlanCount > 0)
            throw new InvalidOperationException("Save or discard plan edits before switching environments or closing this window.");
        _shellPage.IsEnabled = _chatHeader.IsEnabled = false;
        try { await _viewModel.PreserveEditsAsync(); }
        catch { ResumeEditing(); throw; }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        _viewModel.SetWindowActive(args.WindowActivationState != WindowActivationState.Deactivated);
        if (args.WindowActivationState == WindowActivationState.Deactivated) ResetQuitGesture();
    }

    internal void ResumeEditing() => _shellPage.IsEnabled = _chatHeader.IsEnabled = true;

    internal Task<Microsoft.UI.Xaml.Controls.ContentDialogResult> ShowConnectionDialogAsync(
        Microsoft.UI.Xaml.Controls.ContentDialog dialog, CancellationToken cancellationToken) =>
        _shellPage.ShowConnectionDialogAsync(dialog, cancellationToken);

    internal async Task CloseWithRecoveryAsync()
    {
        try { await (_closeTask ??= CloseCoreAsync()); }
        catch
        {
            _closeTask = null;
            _discardEditsOnClose = _allowClose = false;
            ResumeEditing();
            throw;
        }
    }

    private async Task CloseCoreAsync()
    {
        await PrepareForTransitionAsync();
        if (BeforeCloseAsync is not null) await BeforeCloseAsync();
        // Let a canceled native Closing event return before requesting the actual close.
        await Task.Yield();
        _allowClose = true;
        Close();
    }

    private async void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        try { if (await ConfirmPlanDiscardAsync()) await CloseWithRecoveryAsync(); }
        catch (Exception exception) { _viewModel.ReportRuntimeError(exception); }
    }

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellLayoutViewModel.Appearance) or nameof(ShellLayoutViewModel.Themes)) ApplyAppearance();
        if (e.PropertyName == nameof(ShellLayoutViewModel.QuitConfirmationModeIndex)) ResetQuitGesture();
        if (e.PropertyName == nameof(ShellLayoutViewModel.ThemePreference))
        {
            ApplyTheme();
        }
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        ReleaseAppearance();
        ResetQuitGesture();
        if (_quitTimer is not null) _quitTimer.Tick -= OnQuitTick;
        Activated -= OnReadWindowActivated;
        _viewModel.SetReadWindowActive(false);
        AppWindow.Closing -= OnAppWindowClosing;
        Activated -= OnWindowActivated;
        _viewModel.SetWindowActive(false);
        _viewModel.Layout.PropertyChanged -= OnLayoutPropertyChanged;
        _shellPage.SidebarCollapsedChanged -= OnSidebarCollapsedChanged;
        _chatHeader.CommandPaletteRequested -= OnCommandPaletteRequested;
        _shellPage.Release();
    }

    private async Task<bool> ConfirmPlanDiscardAsync()
    {
        var plans = _viewModel.Plan.UnsavedPlanCount;
        if (_discardEditsOnClose || plans == 0) return true;
        if (_closeDialogOpen) return false;
        _closeDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Unsaved plan edits",
                Content = $"{plans} plan(s) have unsaved edits, including other workspaces. File edits and draft messages will be kept for recovery.",
                PrimaryButtonText = "Keep editing",
                SecondaryButtonText = "Discard plan edits and close",
                DefaultButton = ContentDialogButton.Primary,
            };
            _discardEditsOnClose = await _shellPage.ShowConnectionDialogAsync(dialog, CancellationToken.None) == ContentDialogResult.Secondary;
            return _discardEditsOnClose;
        }
        finally { _closeDialogOpen = false; }
    }

    private async void OnCommandPaletteRequested(object? sender, EventArgs e) =>
        await _shellPage.OpenCommandPaletteAsync();

    private void OnReadWindowActivated(object sender, WindowActivatedEventArgs args) =>
        _viewModel.SetReadWindowActive(args.WindowActivationState != WindowActivationState.Deactivated);

    private void OnRootGridLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnRootGridLoaded;
        UpdatePresentationHelpText();
    }

    private void ApplyTheme()
    {
        ApplyAppearance();
    }

    private void UpdatePresentationHelpText()
    {
        var displayScalePercent = (int)Math.Round((RootGrid.XamlRoot?.RasterizationScale ?? 1) * 100);
        AutomationProperties.SetHelpText(
            RootGrid,
            $"Presentation: {_viewModel.Layout.ThemePreference} theme • " +
            $"{_textScalePercent}% text profile • {displayScalePercent}% display scale • {_viewModel.Layout.AppearanceSummary}");
    }

    private void OnSidebarCollapsedChanged(object? sender, EventArgs e)
    {
        if (sender is not ShellPage shellPage)
        {
            return;
        }

        ApplySidebarState(shellPage);
    }

    private void ApplySidebarState(ShellPage shellPage)
    {
        var collapsedWidth = (double)Application.Current.Resources["PiSidebarCollapsedWidth"];
        TitleBarSidebarColumn.Width = shellPage.IsSidebarCollapsed
            ? new GridLength(collapsedWidth)
            : _expandedSidebarWidth;
        TitleBarBrandText.Visibility = shellPage.IsSidebarCollapsed
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
