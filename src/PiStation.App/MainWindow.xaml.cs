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

    public MainWindow(ShellViewModel viewModel, int textScalePercent = 100)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        _textScalePercent = textScalePercent;
        InitializeComponent();
        _expandedSidebarWidth = TitleBarSidebarColumn.Width;
        ApplyTheme();
        _viewModel.Layout.PropertyChanged += OnLayoutPropertyChanged;
        Closed += OnMainWindowClosed;
        Activated += OnReadWindowActivated;
        RootGrid.Loaded += OnRootGridLoaded;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1200, 800));
        AppWindow.Closing += OnWindowClosing;

        _shellPage = new ShellPage(viewModel);
        _shellPage.SidebarCollapsedChanged += OnSidebarCollapsedChanged;
        _chatHeader = new ChatHeader(viewModel);
        _chatHeader.CommandPaletteRequested += OnCommandPaletteRequested;
        TitleBarChatHeaderHost.Content = _chatHeader;
        RootContent.Content = _shellPage;
        ApplySidebarState(_shellPage);
    }

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellLayoutViewModel.ThemePreference))
        {
            ApplyTheme();
        }
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        AppWindow.Closing -= OnWindowClosing;
        Activated -= OnReadWindowActivated;
        _viewModel.SetReadWindowActive(false);
        _viewModel.Layout.PropertyChanged -= OnLayoutPropertyChanged;
        _shellPage.SidebarCollapsedChanged -= OnSidebarCollapsedChanged;
        _chatHeader.CommandPaletteRequested -= OnCommandPaletteRequested;
        _shellPage.Release();
    }

    private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_discardEditsOnClose) return;
        var files = _viewModel.WorkbenchFiles.UnsavedDocumentNames;
        var plans = _viewModel.Plan.UnsavedPlanCount;
        if (files.Count == 0 && plans == 0) return;
        args.Cancel = true;
        if (_closeDialogOpen) return;
        _closeDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Unsaved edits",
                Content = $"{files.Count} file(s) and {plans} plan(s) have unsaved edits, including other workspaces.\n" +
                    string.Join('\n', files.Take(12)),
                PrimaryButtonText = "Keep editing",
                SecondaryButtonText = "Discard edits and close",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Secondary)
            {
                _discardEditsOnClose = true;
                Close();
            }
        }
        catch (Exception error) { _viewModel.ReportRuntimeError(error); }
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
        RootGrid.RequestedTheme = _viewModel.Layout.ThemePreference switch
        {
            AppThemePreference.Dark => ElementTheme.Dark,
            AppThemePreference.Light => ElementTheme.Light,
            AppThemePreference.System => ElementTheme.Default,
            _ => ElementTheme.Dark,
        };
        UpdatePresentationHelpText();
    }

    private void UpdatePresentationHelpText()
    {
        var displayScalePercent = (int)Math.Round((RootGrid.XamlRoot?.RasterizationScale ?? 1) * 100);
        AutomationProperties.SetHelpText(
            RootGrid,
            $"Presentation: {_viewModel.Layout.ThemePreference} theme • " +
            $"{_textScalePercent}% text profile • {displayScalePercent}% display scale");
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
