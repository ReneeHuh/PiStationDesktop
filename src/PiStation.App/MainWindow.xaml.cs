using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellLayoutViewModel.ThemePreference))
        {
            ApplyTheme();
        }
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        _viewModel.Layout.PropertyChanged -= OnLayoutPropertyChanged;
        _shellPage.SidebarCollapsedChanged -= OnSidebarCollapsedChanged;
        _chatHeader.CommandPaletteRequested -= OnCommandPaletteRequested;
        _shellPage.Release();
    }

    private async void OnCommandPaletteRequested(object? sender, EventArgs e) =>
        await _shellPage.OpenCommandPaletteAsync();

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
