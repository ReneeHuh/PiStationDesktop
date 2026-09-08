using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PiStation.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace PiStation.App.Views.Controls;

public sealed partial class ConversationTimeline
{
    private bool _sentAttachmentActionBusy;

    private void OnSentVideoContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        OnSentVideoUnloaded(sender, new RoutedEventArgs());
        if (sender.IsLoaded) OnSentVideoLoaded(sender, new RoutedEventArgs());
    }

    private async void OnSentVideoLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaPlayerElement { DataContext: DraftAttachmentViewModel { IsVideo: true } attachment } player) return;
        var request = new object();
        player.Tag = request;
        try
        {
            await PiStation.ClientRuntime.SentAttachmentAccess.VerifyAsync(attachment.Attachment);
            var file = await StorageFile.GetFileFromPathAsync(attachment.Attachment.ServerPath);
            if (!player.IsLoaded || player.DataContext != attachment || !ReferenceEquals(player.Tag, request)) return;
            var previous = player.Source;
            player.Source = Windows.Media.Core.MediaSource.CreateFromStorageFile(file);
            (previous as IDisposable)?.Dispose();
        }
        catch (Exception error) { ViewModel.ComposerPower.Status = $"Video unavailable: {error.Message}"; }
    }

    private void OnSentVideoUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaPlayerElement player) return;
        player.Tag = null;
        player.MediaPlayer?.Pause();
        var source = player.Source;
        player.Source = null;
        (source as IDisposable)?.Dispose();
    }

    private async void OnOpenSentCitation(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ComposerContextChipViewModel citation })
            await ViewModel.RevealComposerContextAsync(citation);
    }

    private async Task WithSentAttachmentAsync(object sender, Func<DraftAttachmentViewModel, Task> action)
    {
        if (_sentAttachmentActionBusy || sender is not FrameworkElement { DataContext: DraftAttachmentViewModel attachment }) return;
        _sentAttachmentActionBusy = true;
        try
        {
            await PiStation.ClientRuntime.SentAttachmentAccess.VerifyAsync(attachment.Attachment);
            await action(attachment);
        }
        catch (Exception exception)
        {
            ViewModel.ComposerPower.Status = $"Attachment unavailable: {exception.Message}";
            if (IsLoaded && XamlRoot is not null)
            {
                try
                {
                    await new ContentDialog { XamlRoot = XamlRoot, Title = "Attachment unavailable",
                        Content = exception.Message, CloseButtonText = "Close" }.ShowAsync();
                }
                catch (Exception) { /* A different window-level dialog may already be open; status remains available. */ }
            }
        }
        finally { _sentAttachmentActionBusy = false; }
    }

    private async void OnOpenSentAttachment(object sender, RoutedEventArgs e) =>
        await WithSentAttachmentAsync(sender, async attachment =>
        {
            // Opening arbitrary attachments can launch programs; make this an explicit action.
            var confirm = new ContentDialog { XamlRoot = XamlRoot, Title = "Open attachment?",
                Content = $"Open {attachment.FileName} with its Windows application? Only open files you trust.",
                PrimaryButtonText = "Open", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            var file = await StorageFile.GetFileFromPathAsync(attachment.Attachment.ServerPath);
            if (!await Launcher.LaunchFileAsync(file)) throw new IOException("Windows could not open this file. Try Save as instead.");
        });

    private async void OnCopySentAttachmentPath(object sender, RoutedEventArgs e) =>
        await WithSentAttachmentAsync(sender, attachment =>
        {
            var data = new DataPackage();
            data.SetText(attachment.Attachment.ServerPath);
            Clipboard.SetContent(data);
            return Task.CompletedTask;
        });

    private async void OnSaveSentAttachment(object sender, RoutedEventArgs e) =>
        await WithSentAttachmentAsync(sender, async attachment =>
        {
            if ((Application.Current as App)?.MainWindow is not { } window) return;
            var source = await StorageFile.GetFileFromPathAsync(attachment.Attachment.ServerPath);
            var picker = new FileSavePicker { SuggestedFileName = attachment.FileName };
            var extension = Path.GetExtension(attachment.FileName);
            picker.FileTypeChoices.Add("Attachment", [string.IsNullOrEmpty(extension) ? ".bin" : extension]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            if (await picker.PickSaveFileAsync() is not { } target) return;
            if (string.Equals(source.Path, target.Path, StringComparison.OrdinalIgnoreCase)) return;
            await source.CopyAndReplaceAsync(target);
        });

    private async void OnPreviewSentAttachment(object sender, RoutedEventArgs e) =>
        await WithSentAttachmentAsync(sender, async attachment =>
        {
            if (!attachment.IsImage) return;
            var file = await StorageFile.GetFileFromPathAsync(attachment.Attachment.ServerPath);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage { DecodePixelWidth = 1600 };
            await bitmap.SetSourceAsync(stream);
            var preview = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 600, MaxWidth = 900 };
            await new ContentDialog { XamlRoot = XamlRoot, Title = attachment.FileName,
                Content = new ScrollViewer { Content = preview, ZoomMode = ZoomMode.Enabled,
                    MinZoomFactor = 0.5f, MaxZoomFactor = 4, MaxHeight = 600 }, CloseButtonText = "Close" }.ShowAsync();
        });
}
