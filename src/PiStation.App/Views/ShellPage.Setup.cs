using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private async void OnOpenPiSetupClicked(object sender, RoutedEventArgs e)
    {
        SettingsNavigation.SelectedItem = SettingsNavigation.MenuItems
            .OfType<Microsoft.UI.Xaml.Controls.NavigationViewItem>()
            .First(item => item.Tag as string == "PiRuntime");
        await OpenSettingsAsync();
    }

    private async void OnCheckForUpdatesClicked(object sender, RoutedEventArgs e) => await ViewModel.CheckForUpdatesAsync();
    private async void OnOpenUpdateInstallerClicked(object sender, RoutedEventArgs e) => await ViewModel.OpenUpdateInstallerAsync();

    private async void OnConfigurePiRuntimeClicked(object sender, RoutedEventArgs e) => await ViewModel.ConfigurePiRuntimeAsync();

    private async void OnRestartConfiguredPiClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.RestartPiAsync();
        await ViewModel.RefreshComposerPowerAsync();
    }

    private void OnInsertExtensionTextClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Workspace.SelectedThread is not { } thread || ViewModel.Thread.Projection?.ThreadId != thread.ThreadId ||
            !ViewModel.Composer.OwnsDraft(thread.ThreadId)) return;
        var text = ViewModel.ExtensionUi.TakeSuggestedText();
        if (text.Length == 0) return;
        ViewModel.PromptText = string.IsNullOrWhiteSpace(ViewModel.PromptText) ? text : ViewModel.PromptText + Environment.NewLine + text;
    }

    private async void OnBrowsePiExecutableClicked(object sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.MainWindow is not { } window) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        if (await picker.PickSingleFileAsync() is { } file) ViewModel.Settings.PiExecutablePath = file.Path;
    }
}
