using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PiStation.App.Composition;
using PiStation.App.ViewModels;
using PiStation.ClientRuntime;

namespace PiStation.App.Views.Controls;

public sealed partial class BrowserSettingsPanel
{
    private void OnFindImportSources(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        try
        {
            var sources = BrowserCookieImport.Discover();
            ImportSourceSelector.ItemsSource = sources;
            ImportSourceSelector.SelectedIndex = -1;
            Status.Text = sources.Count == 0 ? "No Firefox or Helium cookie profiles were found on this computer." : "Select a source profile, then choose Copy cookies.";
        }
        catch (Exception) { Status.Text = "Local browser profiles could not be enumerated. Check access to their profile folders."; }
    }

    private async void OnImportCookies(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        if (ImportSourceSelector.SelectedItem is not BrowserImportSource source) { Status.Text = "Choose a local source profile first."; return; }
        var name = ImportName.Text.Trim();
        if (name.Length is 0 or > 48) { Status.Text = "Enter a profile name of 1–48 characters."; return; }
        var profile = new BrowserProfilePreference(Guid.NewGuid().ToString("N"), name);
        var directory = BrowserProfilePaths.ProfileDirectory(ViewModel.PreviewProfileRoot, profile.Id);
        var layout = ViewModel.Layout;
        _busy = true;
        ImportEditor.IsEnabled = ProfileEditor.IsEnabled = false;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _importCancellation = cancellation;
        var token = cancellation.Token;
        WebView2? browser = null;
        var committed = false;
        try
        {
            Status.Text = "Copying cookies from the selected source…";
            var read = await Task.Run(() => BrowserCookieImport.ReadSelected(source, token), token);
            token.ThrowIfCancellationRequested();
            if (read.Cookies.Count == 0) { Status.Text = $"No compatible unexpired cookies found. Skipped {read.Skipped}."; return; }
            browser = new WebView2 { Width = 1, Height = 1, IsTabStop = false };
            ClearBrowserHost.Content = browser;
            await browser.EnsureCoreWebView2Async(await PreviewWebViewSurface.GetProfileEnvironmentAsync(directory));
            var imported = 0;
            var skipped = read.Skipped;
            foreach (var cookie in read.Cookies)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var response = await browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.setCookie", JsonSerializer.Serialize(cookie.Parameters()));
                    using var result = JsonDocument.Parse(response);
                    if (result.RootElement.TryGetProperty("success", out var success) && success.GetBoolean()) imported++; else skipped++;
                }
                catch (Exception) { skipped++; } // Never put cookie parameters or browser errors in logs/status.
            }
            token.ThrowIfCancellationRequested();
            if (imported == 0) throw new InvalidOperationException("No cookies could be written to the new profile.");
            layout.CommitImportedBrowserProfile(profile);
            committed = true;
            ProfileSelector.SelectedItem = layout.BrowserProfiles.First(item => item.Id == profile.Id);
            Status.Text = $"Created {name}: imported {imported} cookies, skipped {skipped}. Select this profile when opening a new tab. Some sites may require sign-in again.";
        }
        catch (OperationCanceledException) { Status.Text = "Cookie import canceled."; }
        catch (Exception) { Status.Text = "Cookie import could not be completed or saved. The source profile was not changed. Close the source browser if its database is locked, then retry."; }
        finally
        {
            if (browser is not null)
            {
                if (!committed)
                {
                    try { await PreviewWebViewSurface.ClearProfileDataAsync(directory, browser); }
                    catch (Exception) { Status.Text += " The unregistered destination could not be cleared: " + directory; }
                }
                browser.Close(); ClearBrowserHost.Content = null;
            }
            _importCancellation = null;
            _busy = false;
            ImportEditor.IsEnabled = ProfileEditor.IsEnabled = true;
        }
    }
}
