using PiStation.App.ViewModels;

namespace PiStation.App.Composition;

internal static class BrowserLinkRouter
{
    internal static bool ShouldOpenInApp(Uri uri, BrowserLinkTarget preference, bool hasThread, bool forceSystem) =>
        preference == BrowserLinkTarget.App && hasThread && !forceSystem &&
        uri.IsAbsoluteUri && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);

    internal static async Task OpenAsync(Uri uri, BrowserLinkTarget preference, bool hasThread, bool forceSystem,
        Func<Uri, Task> openPreview, Func<Uri, Task> openSystem)
    {
        if (ShouldOpenInApp(uri, preference, hasThread, forceSystem))
        {
            try { await openPreview(uri); return; }
            catch (OperationCanceledException) { return; }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Preview link failed: {0}", error.Message); }
        }
        await openSystem(uri);
    }
}
