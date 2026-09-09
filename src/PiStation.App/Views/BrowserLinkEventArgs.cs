namespace PiStation.App.Views;

public sealed class BrowserLinkEventArgs(Uri uri, bool forceSystem) : EventArgs
{
    public Uri Uri { get; } = uri;
    public bool ForceSystem { get; } = forceSystem;
}
