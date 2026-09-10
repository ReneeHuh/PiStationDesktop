using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PiStation.ClientRuntime;
using PiStation.Protocol.Models;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private bool _sharingSession;
    private async void OnSharePiSessionClicked(object sender, RoutedEventArgs e)
    {
        if (_sharingSession || !ViewModel.PiSessions.CanAct || ViewModel.Workspace.SelectedThread is not { } thread) return;
        _sharingSession = true;
        var directory = Path.Combine(Path.GetTempPath(), "PiStation-share-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "session.html");
        try
        {
            Directory.CreateDirectory(directory);
            await ViewModel.ExportPiSessionAsync(path, PiSessionExportFormat.Html);
            if (!File.Exists(path)) return;
            var prepared = await PiSessionShare.PrepareAsync(path);
            var publicGist = new CheckBox { Content = "Make this gist public", IsChecked = false };
            var review = new Button { Content = "Review the exact HTML export" };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Share this conversation on GitHub?",
                PrimaryButtonText = "Create gist", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = false };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "PiSessionShareDialog");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(publicGist, "PiSessionSharePublicToggle");
            review.Click += async (_, _) =>
            {
                try { dialog.IsPrimaryButtonEnabled = await Windows.System.Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(path)); }
                catch (Exception error) { ViewModel.PiSessions.Status = error.Message; }
            };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = $"{thread.Title}\n{prepared.Bytes:N0} bytes. This uploads the active branch, including thinking, tool output and embedded images, using GitHub CLI on this PC. Unlisted gists can be read by anyone with the link. Review the export before sharing.", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(review);
            content.Children.Add(publicGist);
            dialog.Content = content;
            if (await ShowConnectionDialogAsync(dialog, CancellationToken.None) != ContentDialogResult.Primary) return;
            ViewModel.PiSessions.IsBusy = true;
            ViewModel.PiSessions.Status = "Creating the selected GitHub gist…";
            var url = await PiSessionShare.PublishAsync(prepared, thread.Title, publicGist.IsChecked == true);
            ViewModel.PiSessions.Status = "Shared: " + url.AbsoluteUri;
        }
        catch (Exception error) { ViewModel.PiSessions.Status = error.Message; }
        finally
        {
            _sharingSession = false;
            ViewModel.PiSessions.IsBusy = false;
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ViewModel.PiSessions.Status += " Temporary export cleanup failed: " + directory; }
        }
    }
}
