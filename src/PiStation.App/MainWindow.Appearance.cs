using Microsoft.UI.Xaml;
using PiStation.App.Views;
using Windows.UI.ViewManagement;

namespace PiStation.App;

public sealed partial class MainWindow
{
    private readonly UISettings _appearanceUiSettings = new();
    private bool _appearanceClosed;

    private void InitializeAppearance()
    {
        ApplyAppearance();
        _appearanceUiSettings.AdvancedEffectsEnabledChanged += OnEffectsChanged;
    }
    private void ApplyAppearance()
    {
        if (_appearanceClosed) return;
        RootGrid.RequestedTheme = _viewModel.Layout.Themes.PreviewAppearance switch
        {
            "light" => ElementTheme.Light, "dark" => ElementTheme.Dark,
            _ => _viewModel.Layout.ThemePreference switch { ViewModels.AppThemePreference.Dark => ElementTheme.Dark, ViewModels.AppThemePreference.Light => ElementTheme.Light, _ => ElementTheme.Default },
        };
        AppearanceResources.Apply(RootGrid, _viewModel.Layout, _textScalePercent / 100d, _appearanceUiSettings.AdvancedEffectsEnabled);
        ThemePreviewNotice.IsOpen = _viewModel.Layout.Themes.IsPreviewing;
        UpdatePresentationHelpText();
    }
    private void OnEffectsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(ApplyAppearance);
    private void OnCancelThemePreview(object sender, RoutedEventArgs args) => _viewModel.Layout.Themes.Cancel();
    private void ReleaseAppearance()
    {
        _appearanceClosed = true;
        _appearanceUiSettings.AdvancedEffectsEnabledChanged -= OnEffectsChanged;
    }
}
