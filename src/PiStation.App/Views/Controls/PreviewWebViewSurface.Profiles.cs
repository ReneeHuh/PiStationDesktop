using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace PiStation.App.Views.Controls;

public sealed partial class PreviewWebViewSurface
{
    private static async Task<CoreWebView2Environment> GetProfileEnvironmentAsync(string path)
    {
        path = Path.GetFullPath(path);
        var pending = ProfileEnvironments.GetOrAdd(path,
            static directory => CoreWebView2Environment.CreateWithOptionsAsync(null, directory, null).AsTask());
        try { return await pending; }
        catch
        {
            ProfileEnvironments.TryRemove(new KeyValuePair<string, Task<CoreWebView2Environment>>(path, pending));
            throw;
        }
    }

    internal static async Task ClearProfileDataAsync(string profileDataPath, WebView2 browser)
    {
        var environment = await GetProfileEnvironmentAsync(profileDataPath);
        await browser.EnsureCoreWebView2Async(environment);
        await browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
    }
}
