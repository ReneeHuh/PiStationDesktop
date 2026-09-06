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
    private async void OnImportSelectedPiSessionClicked(object sender, RoutedEventArgs e) => await ViewModel.CopyPiSessionAsync();
    private async void OnCopyPiSessionClicked(object sender, RoutedEventArgs e) => await ViewModel.CopyPiSessionAsync(copyCurrent: true);
    private async void OnForkPiSessionClicked(object sender, RoutedEventArgs e) => await ViewModel.CopyPiSessionAsync(forkAtSelection: true);

    private async void OnChoosePiSessionFolderClicked(object sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.MainWindow is not { } window) return;
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        if (await picker.PickSingleFolderAsync() is { } folder)
        {
            ViewModel.PiSessions.Directory = folder.Path;
            await ViewModel.BrowsePiSessionsAsync();
        }
    }

    private async void OnChoosePiSessionFileClicked(object sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.MainWindow is not { } window) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".jsonl");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        if (await picker.PickSingleFileAsync() is { } file) await ViewModel.CopyPiSessionAsync(file.Path);
    }

    private async void OnExportPiSessionClicked(object sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.MainWindow is not { } window || ViewModel.Workspace.SelectedThread is null) return;
        var format = (sender as FrameworkElement)?.Tag as string == "html" ? PiSessionExportFormat.Html : PiSessionExportFormat.Jsonl;
        var picker = new FileSavePicker { SuggestedFileName = "pistation-session-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) };
        picker.FileTypeChoices.Add(format == PiSessionExportFormat.Html ? "Readable transcript" : "Pi session", [format == PiSessionExportFormat.Html ? ".html" : ".jsonl"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        if (await picker.PickSaveFileAsync() is { } file) await ViewModel.ExportPiSessionAsync(file.Path, format);
    }
}
