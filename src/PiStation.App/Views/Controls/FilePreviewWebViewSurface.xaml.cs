using System.Text.Encodings.Web;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace PiStation.App.Views.Controls;

public sealed partial class FilePreviewWebViewSurface : UserControl, IDisposable
{
    private string? _allowedFileUri;
    private bool _disposed;
    private Task? _initializationTask;

    public FilePreviewWebViewSurface()
    {
        InitializeComponent();
    }

    public async Task ShowHtmlAsync(string html)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await InitializeAsync();
        _allowedFileUri = null;
        var encoded = HtmlEncoder.Default.Encode(html ?? string.Empty);
        Browser.NavigateToString($$"""
            <!doctype html>
            <html>
              <head>
                <meta charset="utf-8">
                <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data: blob:; media-src data: blob:; font-src data:; style-src 'unsafe-inline'; frame-src 'self'">
                <meta name="referrer" content="no-referrer">
                <style>html,body,iframe{width:100%;height:100%;margin:0;border:0;background:white}iframe{display:block}</style>
              </head>
              <body><iframe sandbox="" referrerpolicy="no-referrer" srcdoc="{{encoded}}"></iframe></body>
            </html>
            """);
    }

    public async Task ShowPdfAsync(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || !Path.GetExtension(fullPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("The PDF preview file is unavailable.", fullPath);
        }

        await InitializeAsync();
        _allowedFileUri = new Uri(fullPath).AbsoluteUri;
        Browser.CoreWebView2.Navigate(_allowedFileUri);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Browser.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= OnNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.PermissionRequested -= OnPermissionRequested;
            core.DownloadStarting -= OnDownloadStarting;
        }

        Browser.Close();
    }

    private Task InitializeAsync() => _initializationTask ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        await Browser.EnsureCoreWebView2Async();
        var core = Browser.CoreWebView2 ??
            throw new InvalidOperationException("WebView2 did not create its core instance.");
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsScriptEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.PermissionRequested += OnPermissionRequested;
        core.DownloadStarting += OnDownloadStarting;
        Browser.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        LoadingIndicator.IsActive = false;
        LoadingIndicator.Visibility = Visibility.Collapsed;
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (args.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ||
            (_allowedFileUri is not null &&
             (args.Uri.Equals(_allowedFileUri, StringComparison.OrdinalIgnoreCase) ||
              args.Uri.StartsWith(_allowedFileUri + "#", StringComparison.OrdinalIgnoreCase) ||
              args.Uri.StartsWith(_allowedFileUri + "?", StringComparison.OrdinalIgnoreCase))))
        {
            return;
        }

        args.Cancel = true;
    }

    private static void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args) =>
        args.Handled = true;

    private static void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
        args.Handled = true;
    }

    private static void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args) =>
        args.Cancel = true;
}
