using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PiStation.Protocol.Models;
using Windows.Storage.Pickers;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private async Task OpenPiSessionsAsync()
    {
        SettingsNavigation.SelectedItem = SettingsNavigation.MenuItems.OfType<NavigationViewItem>()
            .First(item => item.Tag as string == "PiSessions");
        await OpenSettingsAsync();
    }

    private async void OnBrowsePiSessionsClicked(object sender, RoutedEventArgs e) => await ViewModel.BrowsePiSessionsAsync();
    private async void OnInspectPiSessionClicked(object sender, RoutedEventArgs e) => await ViewModel.InspectPiSessionAsync();
    private async void OnLoadMoreSessionFiles(object sender, RoutedEventArgs e) => await ViewModel.BrowsePiSessionsAsync(loadMore: true);
    private async void OnLoadMoreSessionEntries(object sender, RoutedEventArgs e) => await ViewModel.InspectPiSessionAsync(loadMore: true);
    private async void OnImportSelectedPiSessionClicked(object sender, RoutedEventArgs e) => await ViewModel.CopyPiSessionAsync();
    private async void OnCopyPiSessionClicked(object sender, RoutedEventArgs e) => await ViewModel.CopyPiSessionAsync(copyCurrent: true);
    private async void OnForkPiSessionClicked(object sender, RoutedEventArgs e) => await ViewModel.CopyPiSessionAsync(forkAtSelection: true);
    private async void OnNavigatePiSessionClicked(object sender, RoutedEventArgs e) => await ViewModel.NavigatePiSessionAsync();
    private async void OnSavePiSessionLabelClicked(object sender, RoutedEventArgs e) => await ViewModel.SetPiSessionLabelAsync();
    private async void OnRemovePiSessionLabelClicked(object sender, RoutedEventArgs e) => await ViewModel.SetPiSessionLabelAsync(remove: true);
    private async void OnClearPiSessionFiltersClicked(object sender, RoutedEventArgs e) => await ViewModel.ClearSessionTreeFiltersAsync();
    private async void OnCancelPiSessionNavigationClicked(object sender, RoutedEventArgs e) => await ViewModel.CancelPiSessionNavigationAsync();
    private void OnCopyNavigationPromptClicked(object sender, RoutedEventArgs e)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(ViewModel.PiSessions.NavigationPrompt);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }
    private async void OnRetryPiSessionImportClicked(object sender, RoutedEventArgs e) => await ViewModel.RetryPiSessionImportAsync();
    private void OnCancelPiSessionTransferClicked(object sender, RoutedEventArgs e) => ViewModel.CancelSessionTransfer();

    private async void OnUseDefaultPiSessionFolderClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.PiSessions.Directory = string.Empty;
        await ViewModel.BrowsePiSessionsAsync();
    }

    private async void OnChoosePiSessionFolderClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsRemote || (Application.Current as App)?.FindWindow(XamlRoot) is not { } window) return;
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        if (await picker.PickSingleFolderAsync() is not { } folder) return;
        ViewModel.PiSessions.Directory = folder.Path;
        await ViewModel.BrowsePiSessionsAsync();
    }

    private async void OnChoosePiSessionFileClicked(object sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.FindWindow(XamlRoot) is not { } window) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".jsonl");
        picker.FileTypeFilter.Add(".zip");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        if (await picker.PickSingleFileAsync() is { } file) await ViewModel.ImportLocalPiSessionAsync(file.Path);
    }

    private async void OnExportPiSessionClicked(object sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.FindWindow(XamlRoot) is not { } window || ViewModel.Workspace.SelectedThread is null) return;
        var format = ((sender as FrameworkElement)?.Tag as string) switch { "html" => PiSessionExportFormat.Html, "bundle" => PiSessionExportFormat.Bundle, _ => PiSessionExportFormat.Jsonl };
        var picker = new FileSavePicker { SuggestedFileName = "pistation-session-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) };
        picker.FileTypeChoices.Add("Pi session export", [format switch { PiSessionExportFormat.Html => ".html", PiSessionExportFormat.Bundle => ".zip", _ => ".jsonl" }]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        if (await picker.PickSaveFileAsync() is { } file) await ViewModel.ExportPiSessionAsync(file.Path, format);
    }
}
