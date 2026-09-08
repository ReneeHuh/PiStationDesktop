using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PiStation.App.ViewModels;
using Windows.Media.Core;
using Windows.Storage;

namespace PiStation.App.Views.Controls;

/// <summary>Owns asynchronous media loading for a realized attachment template.</summary>
internal sealed class AttachmentMediaLoader : IDisposable
{
    private readonly FrameworkElement _element;
    private readonly ShellViewModel _shell;
    private CancellationTokenSource? _request;
    private bool _disposed;

    private AttachmentMediaLoader(FrameworkElement element, ShellViewModel shell)
    {
        _element = element;
        _shell = shell;
        _shell.PropertyChanged += OnConnectionChanged;
        Reload();
    }

    internal static void Start(object sender, ShellViewModel shell)
    {
        if (sender is not FrameworkElement element) return;
        Stop(element);
        if (element.IsLoaded) element.Tag = new AttachmentMediaLoader(element, shell);
    }

    internal static void Stop(object sender)
    {
        if (sender is not FrameworkElement element) return;
        (element.Tag as AttachmentMediaLoader)?.Dispose();
        element.Tag = null;
    }

    private void OnConnectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ShellViewModel.IsConnected)) Reload();
    }

    private void Reload()
    {
        _request?.Cancel();
        _request = null;
        ClearMedia();
        if (!_shell.IsConnected || _element.DataContext is not DraftAttachmentViewModel attachment ||
            !(_element is Image && attachment.IsImage || _element is MediaPlayerElement && attachment.IsVideo)) return;
        _request = new CancellationTokenSource();
        _ = LoadAsync(attachment, _request);
    }

    private async Task LoadAsync(DraftAttachmentViewModel attachment, CancellationTokenSource request)
    {
        try
        {
            ToolTipService.SetToolTip(_element, "Loading attachment…");
            var path = await _shell.GetAttachmentFileAsync(attachment.Attachment, request.Token);
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (!IsCurrent()) return;
            if (_element is Image image)
            {
                using var stream = await file.OpenReadAsync();
                var bitmap = new BitmapImage { DecodePixelWidth = 320 };
                await bitmap.SetSourceAsync(stream);
                if (!IsCurrent()) return;
                image.Source = bitmap;
            }
            else if (_element is MediaPlayerElement player)
                player.Source = MediaSource.CreateFromStorageFile(file);
            ToolTipService.SetToolTip(_element, attachment.FileName);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (IsCurrent()) ToolTipService.SetToolTip(_element, $"Attachment unavailable: {error.Message}");
        }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
            request.Dispose();
        }

        bool IsCurrent() => !_disposed && !request.IsCancellationRequested && _element.IsLoaded &&
            ReferenceEquals(_element.DataContext, attachment) && ReferenceEquals(_request, request);
    }

    private void ClearMedia()
    {
        if (_element is Image image) image.Source = null;
        if (_element is MediaPlayerElement player)
        {
            player.MediaPlayer?.Pause();
            var source = player.Source;
            player.Source = null;
            (source as IDisposable)?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shell.PropertyChanged -= OnConnectionChanged;
        _request?.Cancel();
        _request = null;
        ClearMedia();
    }
}
