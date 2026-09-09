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
        if (ViewModel.PiResources.Snapshot is not null) await ViewModel.RefreshPiResourcesAsync();
    }

    private async void OnSearchPiPackagesClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.SearchPiPackagesAsync(sender is FrameworkElement { Tag: "more" });

    private void OnSelectPiPackageClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PiStation.Protocol.Models.PiPackageSearchItem package })
            ViewModel.PiResources.SelectPackageSource(package.Source);
    }

    private async void OnPiPackageClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string action } element) return;
        if (element.DataContext is PiStation.Protocol.Models.PiPackageDescriptor package)
        {
            ViewModel.PiResources.PackageSource = package.Source;
            ViewModel.PiResources.PackageLocal = package.Scope == "project";
        }
        await ViewModel.ManagePiResourcesAsync(action);
    }
    private async void OnPiNativeAccountClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string action })
        {
            SettingsDialog.Hide();
            await ViewModel.ManagePiResourcesAsync(action);
        }
    }
    private async void OnRefreshPiResourcesClicked(object sender, RoutedEventArgs e) => await ViewModel.RefreshPiResourcesAsync();
    private async void OnTogglePiResourceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Button { DataContext: ViewModels.PiResourceRow row })
            await ViewModel.ManagePiResourcesAsync("toggle", row, !row.Resource.Enabled);
    }
    private async void OnTrustPiProjectClicked(object sender, RoutedEventArgs e) => await ViewModel.ManagePiResourcesAsync("trust", enabled: true);
    private async void OnUntrustPiProjectClicked(object sender, RoutedEventArgs e) => await ViewModel.ManagePiResourcesAsync("trust", enabled: false);
    private async void OnSavePiModelClicked(object sender, RoutedEventArgs e) => await ViewModel.ManagePiResourcesAsync("saveModel");
    private async void OnPiSetupTerminalClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Button { Tag: string action })
        {
            SettingsDialog.Hide();
            await ViewModel.StartPiSetupTerminalAsync(action);
        }
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
        if (ViewModel.IsRemote)
        {
            var selected = await HostPathPicker.PickAsync(XamlRoot, "Select Pi on " + ViewModel.EnvironmentLabel,
                ViewModel.BrowseHostPathAsync, showDialog: dialog => ShowConnectionDialogAsync(dialog, CancellationToken.None));
            if (selected is not null) ViewModel.Settings.PiExecutablePath = selected;
            return;
        }
        if ((Application.Current as App)?.MainWindow is not { } window) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        if (await picker.PickSingleFileAsync() is { } file) ViewModel.Settings.PiExecutablePath = file.Path;
    }
}
