using Microsoft.UI.Xaml;

namespace PiStation.App.Views.Controls;

public sealed partial class ComposerSurface
{
    private void OnAttachmentImageLoaded(object sender, RoutedEventArgs e) => AttachmentMediaLoader.Start(sender, ViewModel);
    private void OnAttachmentImageUnloaded(object sender, RoutedEventArgs e) => AttachmentMediaLoader.Stop(sender);
    private void OnAttachmentImageContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) =>
        AttachmentMediaLoader.Start(sender, ViewModel);
}
