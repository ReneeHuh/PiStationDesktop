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
    private CancellationTokenSource? _sentAttachmentCancellation;

    private void OnSentMediaContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) =>
        AttachmentMediaLoader.Start(sender, ViewModel);

    private void OnSentMediaLoaded(object sender, RoutedEventArgs e) => AttachmentMediaLoader.Start(sender, ViewModel);

    private void OnSentMediaUnloaded(object sender, RoutedEventArgs e) => AttachmentMediaLoader.Stop(sender);

    private async void OnOpenSentCitation(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ComposerContextChipViewModel citation })
            await ViewModel.RevealComposerContextAsync(citation);
    }

    private async Task WithSentAttachmentAsync(object sender, Func<DraftAttachmentViewModel, string, Task> action)
    {
        if (_sentAttachmentActionBusy || sender is not FrameworkElement { DataContext: DraftAttachmentViewModel attachment }) return;
        _sentAttachmentActionBusy = true;
        using var request = new CancellationTokenSource();
        _sentAttachmentCancellation = request;
        try
        {
            var path = await ViewModel.GetAttachmentFileAsync(attachment.Attachment, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (!IsLoaded || sender is not FrameworkElement element || !ReferenceEquals(element.DataContext, attachment)) return;
            await action(attachment, path);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
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
        finally { _sentAttachmentActionBusy = false; _sentAttachmentCancellation = null; }
    }

    private async void OnOpenSentAttachment(object sender, RoutedEventArgs e) =>
        await WithSentAttachmentAsync(sender, async (attachment, path) =>
        {
            // Opening arbitrary attachments can launch programs; make this an explicit action.
            var confirm = new ContentDialog { XamlRoot = XamlRoot, Title = "Open attachment?",
                Content = $"Open {attachment.FileName} with its Windows application? Only open files you trust.",
                PrimaryButtonText = "Open", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (!await Launcher.LaunchFileAsync(file)) throw new IOException("Windows could not open this file. Try Save as instead.");
        });

    private async void OnCopySentAttachmentPath(object sender, RoutedEventArgs e) =>
        await WithSentAttachmentAsync(sender, (attachment, path) =>
        {
            var data = new DataPackage();
            data.SetText(path);
            Clipboard.SetContent(data);
            return Task.CompletedTask;
        });

    private async void OnSaveSentAttachment(object sender, RoutedEventArgs e) =>
        await WithSentAttachmentAsync(sender, async (attachment, path) =>
        {
            if ((Application.Current as App)?.FindWindow(XamlRoot) is not { } window) return;
            var source = await StorageFile.GetFileFromPathAsync(path);
            var picker = new FileSavePicker { SuggestedFileName = attachment.FileName };
            var extension = Path.GetExtension(attachment.FileName);
            picker.FileTypeChoices.Add("Attachment", [string.IsNullOrEmpty(extension) ? ".bin" : extension]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            if (await picker.PickSaveFileAsync() is not { } target) return;
            if (string.Equals(source.Path, target.Path, StringComparison.OrdinalIgnoreCase)) return;
            await source.CopyAndReplaceAsync(target);
        });

    private async void OnPreviewSentAttachment(object sender, RoutedEventArgs e) =>
        await WithSentAttachmentAsync(sender, async (attachment, path) =>
        {
            if (!attachment.IsImage) return;
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage { DecodePixelWidth = 1600 };
            await bitmap.SetSourceAsync(stream);
            var preview = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 600, MaxWidth = 900 };
            await new ContentDialog { XamlRoot = XamlRoot, Title = attachment.FileName,
                Content = new ScrollViewer { Content = preview, ZoomMode = ZoomMode.Enabled,
                    MinZoomFactor = 0.5f, MaxZoomFactor = 4, MaxHeight = 600 }, CloseButtonText = "Close" }.ShowAsync();
        });
}
