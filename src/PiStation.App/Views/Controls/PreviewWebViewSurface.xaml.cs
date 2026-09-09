using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using PiStation.App.ViewModels;
using Windows.Media.Editing;
using Windows.Media.Transcoding;
using Windows.Storage;
using PiStation.ClientRuntime;
using Windows.Storage.Streams;

namespace PiStation.App.Views.Controls;

public sealed partial class PreviewWebViewSurface : UserControl, IDisposable
{
    private static readonly ConcurrentDictionary<string, Task<CoreWebView2Environment>> ProfileEnvironments =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, string?> _navigationContexts = [];
    private string? _activeNavigationContext;
    private bool _disposed;
    private TaskCompletionSource<PreviewElementSelection?>? _elementPickCompletion;
    private string? _elementPickToken;
    private Task? _initializationTask;
    private string? _navigationContext;
    private string? _profileDataPath;
    private bool _allowDevTools;
    private bool _inPrivate;
    private double _zoomFactor = 1;
    private PreviewColorScheme _colorScheme;
    private CancellationTokenSource? _recordingCancellation;
    private Task? _recordingTask;
    private string? _recordingDirectory;
    private int _recordingFrameRate;
    private RemotePreviewProxy? _remoteRoute;
    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    public Func<Uri, Task<RemotePreviewProxy?>>? RemoteRouteFactory { get; set; }

    public PreviewWebViewSurface()
    {
        InitializeComponent();
    }

    public event EventHandler<PreviewNavigationStartingEventArgs>? NavigationStarted;

    public event EventHandler<PreviewNavigationCompletedEventArgs>? NavigationFinished;

    public event EventHandler<PreviewBrowserStateEventArgs>? BrowserStateChanged;

    public event EventHandler<PreviewBrowserFailureEventArgs>? BrowserFailed;

    public bool IsInitialized => !_disposed && Browser.CoreWebView2 is not null;

    public string? CurrentSource => LogicalSource(Browser.CoreWebView2?.Source);

    private string? LogicalSource(string? source) => _remoteRoute is not null && Uri.TryCreate(source, UriKind.Absolute, out var uri)
        ? _remoteRoute.ToLogicalUri(uri).AbsoluteUri : source;

    public void Configure(
        string profileDataPath,
        bool allowDevTools,
        double zoomFactor,
        PreviewColorScheme colorScheme,
        bool inPrivate = false)
    {
        if (_initializationTask is not null &&
            !string.Equals(_profileDataPath, profileDataPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A preview profile cannot change after its browser starts.");
        }

        _profileDataPath = Path.GetFullPath(profileDataPath);
        _inPrivate = inPrivate;
        _allowDevTools = allowDevTools;
        _zoomFactor = NormalizeZoom(zoomFactor);
        _colorScheme = Enum.IsDefined(colorScheme) ? colorScheme : PreviewColorScheme.System;
        if (Browser.CoreWebView2 is not null)
        {
            ApplyDevToolsPolicy(allowDevTools);
            _ = ApplyZoomAsync(_zoomFactor);
            _ = ApplyColorSchemeAsync(_colorScheme);
        }
    }

    public void SetNavigationContext(string? context)
    {
        if (string.Equals(_navigationContext, context, StringComparison.Ordinal))
        {
            return;
        }

        Browser.CoreWebView2?.Stop();
        _navigationContext = context;
        _activeNavigationContext = context;
    }

    public Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _initializationTask ??= InitializeCoreAsync();
    }

    public async Task NavigateAsync(Uri uri, Action? validate = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!WorkbenchPreviewViewModel.TryNormalizeAddress(uri.AbsoluteUri, out var normalized, out var error))
        {
            throw new ArgumentException(error, nameof(uri));
        }

        await InitializeAsync();
        await _navigationGate.WaitAsync();
        try
        {
            validate?.Invoke();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_remoteRoute is not null && normalized.GetLeftPart(UriPartial.Authority) != _remoteRoute.Target.GetLeftPart(UriPartial.Authority))
            {
                _remoteRoute.Reconnected -= OnRouteReconnected;
                await _remoteRoute.DisposeAsync();
                _remoteRoute = null;
            }
            if (_remoteRoute is null && RemoteRouteFactory is { } factory)
            {
                _remoteRoute = await factory(normalized);
                if (_remoteRoute is not null) _remoteRoute.Reconnected += OnRouteReconnected;
                if (_disposed)
                {
                    if (_remoteRoute is not null) await _remoteRoute.DisposeAsync();
                    _remoteRoute = null;
                    return;
                }
            }
            validate?.Invoke();
            if (_remoteRoute is { } route)
            {
                RestoreRemoteRouteCookie();
                normalized = route.ToBrowserUri(normalized);
            }
            Browser.CoreWebView2.Navigate(normalized.AbsoluteUri);
        }
        finally { _navigationGate.Release(); }
    }

    public void GoBack()
    {
        if (Browser.CoreWebView2?.CanGoBack == true)
        {
            Browser.CoreWebView2.GoBack();
        }
    }

    public void GoForward()
    {
        if (Browser.CoreWebView2?.CanGoForward == true)
        {
            Browser.CoreWebView2.GoForward();
        }
    }

    public void Reload()
    {
        // Profile clearing also removes our short-lived transport cookie. Restore
        // only this app-issued credential so Reload can reach the paired host.
        RestoreRemoteRouteCookie();
        Browser.CoreWebView2?.Reload();
    }

    private void RestoreRemoteRouteCookie()
    {
        if (Browser.CoreWebView2 is not { } core || _remoteRoute is not { } route) return;
        var cookie = core.CookieManager.CreateCookie(route.CookieName, route.CookieValue, route.Address.Host, "/");
        cookie.IsHttpOnly = true;
        cookie.SameSite = CoreWebView2CookieSameSiteKind.Strict;
        core.CookieManager.AddOrUpdateCookie(cookie);
    }

    private void OnRouteReconnected(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_disposed && ReferenceEquals(sender, _remoteRoute)) Reload();
    });

    public void Stop() => Browser.CoreWebView2?.Stop();

    public void ApplyDevToolsPolicy(bool allow)
    {
        _allowDevTools = allow;
        if (Browser.CoreWebView2 is { } core)
        {
            core.Settings.AreDevToolsEnabled = allow;
        }
    }

    public void OpenDevTools()
    {
        if (!_allowDevTools || Browser.CoreWebView2 is not { } core)
        {
            throw new InvalidOperationException("DevTools are disabled by the preview security policy.");
        }

        core.OpenDevToolsWindow();
    }

    public void SetZoomFactor(double value)
    {
        _zoomFactor = NormalizeZoom(value);
        if (Browser.CoreWebView2 is not null)
        {
            _ = ApplyZoomAsync(_zoomFactor);
        }
    }

    public async Task SetColorSchemeAsync(PreviewColorScheme colorScheme)
    {
        _colorScheme = Enum.IsDefined(colorScheme) ? colorScheme : PreviewColorScheme.System;
        if (Browser.CoreWebView2 is not null)
        {
            await ApplyColorSchemeAsync(_colorScheme);
        }
    }

    public async Task<int> ImportCookiesAsync(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        await InitializeAsync();
        var manager = Browser.CoreWebView2.CookieManager;
        var imported = 0;
        foreach (var item in ParseCookies(content).Take(2_000))
        {
            try
            {
                var cookie = manager.CreateCookie(item.Name, item.Value, item.Domain, item.Path);
                cookie.IsSecure = item.IsSecure;
                cookie.IsHttpOnly = item.IsHttpOnly;
                manager.AddOrUpdateCookie(cookie);
                imported++;
            }
            catch (ArgumentException)
            {
            }
        }

        return imported;
    }

    public async Task<string> GetDomSnapshotAsync(Action? validate = null)
    {
        await InitializeAsync();
        validate?.Invoke();
        var raw = await Browser.CoreWebView2.ExecuteScriptAsync(DomSnapshotScript);
        return DecodeScriptString(raw, "[]", 32 * 1024);
    }

    public async Task<string> ClickElementAsync(string selector, Action? validate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        if (selector.Length > 1024)
        {
            throw new ArgumentException("The browser selector exceeds 1,024 characters.", nameof(selector));
        }

        await InitializeAsync();
        var script = $$"""
            (() => {
              const element = document.querySelector({{JsonSerializer.Serialize(selector)}});
              if (!element) return JSON.stringify({ ok: false, message: 'No matching element' });
              element.scrollIntoView({ block: 'center', inline: 'center' });
              element.click();
              return JSON.stringify({ ok: true, tag: element.tagName.toLowerCase() });
            })()
            """;
        validate?.Invoke();
        return DecodeScriptString(await Browser.CoreWebView2.ExecuteScriptAsync(script), "{}", 4 * 1024);
    }

    public async Task<string> TypeIntoElementAsync(string selector, string value, Action? validate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ArgumentNullException.ThrowIfNull(value);
        if (selector.Length > 1024 || value.Length > 8 * 1024)
        {
            throw new ArgumentException("The browser selector or value exceeds its safe limit.");
        }

        await InitializeAsync();
        var script = $$"""
            (() => {
              const element = document.querySelector({{JsonSerializer.Serialize(selector)}});
              if (!element) return JSON.stringify({ ok: false, message: 'No matching element' });
              const value = {{JsonSerializer.Serialize(value)}};
              element.focus();
              if ('value' in element) element.value = value;
              else if (element.isContentEditable) element.textContent = value;
              else return JSON.stringify({ ok: false, message: 'Element does not accept text' });
              element.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: value }));
              element.dispatchEvent(new Event('change', { bubbles: true }));
              return JSON.stringify({ ok: true, tag: element.tagName.toLowerCase() });
            })()
            """;
        validate?.Invoke();
        return DecodeScriptString(await Browser.CoreWebView2.ExecuteScriptAsync(script), "{}", 4 * 1024);
    }

    public async Task<byte[]> CapturePreviewPngAsync(Action? validate = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await InitializeAsync();
        var core = Browser.CoreWebView2 ??
            throw new InvalidOperationException("Web preview is not initialized.");
        using var stream = new InMemoryRandomAccessStream();
        validate?.Invoke();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        if (stream.Size is 0 or > 20 * 1024 * 1024)
        {
            throw new InvalidOperationException("The preview screenshot was empty or exceeded 20 MB.");
        }

        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync(checked((uint)stream.Size));
        var bytes = new byte[checked((int)stream.Size)];
        reader.ReadBytes(bytes);
        return bytes;
    }

    public Task StartRecordingAsync(int frameRate = 4)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_recordingTask is not null)
        {
            throw new InvalidOperationException("The preview is already recording.");
        }

        _recordingFrameRate = Math.Clamp(frameRate, 1, 12);
        var root = Path.Combine(Path.GetTempPath(), "PiStationDesktop", "preview-recordings");
        Directory.CreateDirectory(root);
        _recordingDirectory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_recordingDirectory);
        _recordingCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _recordingTask = CaptureRecordingFramesAsync(_recordingDirectory, _recordingFrameRate, _recordingCancellation.Token);
        return Task.CompletedTask;
    }

    public async Task<string> StopRecordingAsync(string outputDirectory)
    {
        if (_recordingTask is null || _recordingCancellation is null || _recordingDirectory is null)
        {
            throw new InvalidOperationException("The preview is not recording.");
        }

        var recordingTask = _recordingTask;
        var frameDirectory = _recordingDirectory;
        var frameRate = _recordingFrameRate;
        _recordingCancellation.Cancel();
        try
        {
            await recordingTask;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _recordingCancellation.Dispose();
            _recordingCancellation = null;
            _recordingTask = null;
            _recordingDirectory = null;
        }

        var framePaths = Directory.EnumerateFiles(frameDirectory, "*.png")
            .OrderBy(static path => path, StringComparer.Ordinal)
            .Take(1_440)
            .ToArray();
        if (framePaths.Length == 0)
        {
            DeleteRecordingFrames(frameDirectory);
            throw new InvalidOperationException("The recording did not capture any frames.");
        }

        Directory.CreateDirectory(outputDirectory);
        var outputFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(outputDirectory));
        var output = await outputFolder.CreateFileAsync(
            $"preview-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.mp4",
            CreationCollisionOption.GenerateUniqueName);
        try
        {
            var composition = new MediaComposition();
            var duration = TimeSpan.FromSeconds(1d / frameRate);
            foreach (var framePath in framePaths)
            {
                var frame = await StorageFile.GetFileFromPathAsync(framePath);
                composition.Clips.Add(await MediaClip.CreateFromImageFileAsync(frame, duration));
            }

            var failure = await composition.RenderToFileAsync(
                output,
                MediaTrimmingPreference.Precise);
            if (failure != TranscodeFailureReason.None)
            {
                throw new InvalidOperationException($"The recording encoder failed ({failure}).");
            }

            return output.Path;
        }
        finally
        {
            DeleteRecordingFrames(frameDirectory);
        }
    }

    public async Task<PreviewElementSelection?> PickElementAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await InitializeAsync();
        var core = Browser.CoreWebView2 ??
            throw new InvalidOperationException("Web preview is not initialized.");
        CancelElementPicker();
        var token = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<PreviewElementSelection?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _elementPickToken = token;
        _elementPickCompletion = completion;
        core.Settings.IsWebMessageEnabled = true;
        core.WebMessageReceived += OnWebMessageReceived;
        try
        {
            await core.ExecuteScriptAsync(ElementPickerScript.Replace("__PISTATION_TOKEN__", token, StringComparison.Ordinal));
        }
        catch
        {
            FinishElementPicker(null);
            throw;
        }

        return await completion.Task;
    }

    public void CancelElementPicker()
    {
        if (_elementPickCompletion is null)
        {
            return;
        }

        if (Browser.CoreWebView2 is { } core)
        {
            _ = core.ExecuteScriptAsync("window.__piStationPreviewPicker?.cleanup?.();");
        }

        FinishElementPicker(null);
    }

    public async Task RecoverAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await InitializeAsync();
        Reload();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SetAutomationEnabled(false);
        InterruptAutomationDocument();
        if (_scriptExecutor is { } executor) _ = executor.DisposeAsync().AsTask();
        var recordingTask = _recordingTask;
        var recordingDirectory = _recordingDirectory;
        _recordingCancellation?.Cancel();
        _recordingCancellation?.Dispose();
        _recordingCancellation = null;
        _recordingTask = null;
        _recordingDirectory = null;
        if (recordingTask is not null && recordingDirectory is not null)
        {
            _ = recordingTask.ContinueWith(
                static (task, state) =>
                {
                    _ = task.Exception;
                    DeleteRecordingFrames((string)state!);
                },
                recordingDirectory,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        if (_remoteRoute is { } route)
        {
            route.Reconnected -= OnRouteReconnected;
            Browser.CoreWebView2?.CookieManager.DeleteCookies(route.CookieName, route.Address.AbsoluteUri);
            _ = route.DisposeAsync().AsTask();
            _remoteRoute = null;
        }
        CancelElementPicker();
        if (Browser.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.SourceChanged -= OnSourceChanged;
            core.DocumentTitleChanged -= OnDocumentTitleChanged;
            core.HistoryChanged -= OnHistoryChanged;
            core.ProcessFailed -= OnProcessFailed;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.PermissionRequested -= OnPermissionRequested;
            core.DownloadStarting -= OnDownloadStarting;
            core.WebMessageReceived -= OnWebMessageReceived;
        }

        Browser.Close();
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            if (_profileDataPath is { } profileDataPath)
            {
                Directory.CreateDirectory(profileDataPath);
                var environment = await GetProfileEnvironmentAsync(profileDataPath);
                ObjectDisposedException.ThrowIf(_disposed, this);
                var controllerOptions = environment.CreateCoreWebView2ControllerOptions();
                controllerOptions.IsInPrivateModeEnabled = _inPrivate;
                await Browser.EnsureCoreWebView2Async(environment, controllerOptions);
            }
            else
            {
                await Browser.EnsureCoreWebView2Async();
            }
            var core = Browser.CoreWebView2 ??
                throw new InvalidOperationException("WebView2 did not create its core instance.");
            if (_disposed)
            {
                Browser.Close();
                throw new ObjectDisposedException(nameof(PreviewWebViewSurface));
            }

            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = _allowDevTools;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.SourceChanged += OnSourceChanged;
            core.DocumentTitleChanged += OnDocumentTitleChanged;
            core.HistoryChanged += OnHistoryChanged;
            core.ProcessFailed += OnProcessFailed;
            core.NewWindowRequested += OnNewWindowRequested;
            core.PermissionRequested += OnPermissionRequested;
            core.DownloadStarting += OnDownloadStarting;
            // Web content commonly relies on the browser's white default without declaring a body background.
            Browser.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            await ApplyZoomAsync(_zoomFactor);
            await ApplyColorSchemeAsync(_colorScheme);
            InitializationProgress.IsActive = false;
            InitializationOverlay.Visibility = Visibility.Collapsed;
            PublishBrowserState(_navigationContext);
        }
        catch
        {
            _initializationTask = null;
            InitializationProgress.IsActive = false;
            InitializationMessage.Text = "Web preview could not start";
            throw;
        }
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        InterruptAutomationDocument();
        if (_elementPickCompletion is not null)
        {
            FinishElementPicker(null);
        }

        if (!_navigationContexts.TryGetValue(args.NavigationId, out var context))
        {
            context = _navigationContext;
            _navigationContexts[args.NavigationId] = context;
        }

        _activeNavigationContext = context;
        if (!WorkbenchPreviewViewModel.TryNormalizeAddress(args.Uri, out var uri, out var error))
        {
            args.Cancel = true;
            NavigationFinished?.Invoke(this, new PreviewNavigationCompletedEventArgs(context, false, error));
            return;
        }

        if (_remoteRoute is { } route)
        {
            if (uri.IsLoopback && uri.GetLeftPart(UriPartial.Authority) != route.Address.GetLeftPart(UriPartial.Authority))
            {
                args.Cancel = true;
                NavigationFinished?.Invoke(this, new PreviewNavigationCompletedEventArgs(context, false,
                    "Enter the new host-local address in Preview to authorize another route."));
                return;
            }
            uri = route.ToLogicalUri(uri);
        }
        NavigationStarted?.Invoke(this, new PreviewNavigationStartingEventArgs(context, uri));
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        var context = _navigationContexts.GetValueOrDefault(args.NavigationId, _activeNavigationContext);
        _navigationContexts.Remove(args.NavigationId);
        PublishBrowserState(context);
        NavigationFinished?.Invoke(
            this,
            new PreviewNavigationCompletedEventArgs(
                context,
                args.IsSuccess,
                args.IsSuccess ? null : FriendlyNavigationError(args.WebErrorStatus)));
    }

    private void OnSourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args) =>
        PublishBrowserState(_activeNavigationContext);

    private void OnDocumentTitleChanged(CoreWebView2 sender, object args) =>
        PublishBrowserState(_activeNavigationContext);

    private void OnHistoryChanged(CoreWebView2 sender, object args) =>
        PublishBrowserState(_activeNavigationContext);

    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        InterruptAutomationDocument();
        FinishElementPicker(null);
        BrowserFailed?.Invoke(
            this,
            new PreviewBrowserFailureEventArgs(
                _navigationContext,
                $"The preview browser process stopped ({args.ProcessFailedKind}). Reload to recover."));
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (WorkbenchPreviewViewModel.TryNormalizeAddress(args.Uri, out var uri, out _))
        {
            sender.Navigate(uri.AbsoluteUri);
        }
    }

    private static void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
        args.Handled = true;
    }

    private static void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args) =>
        args.Cancel = true;

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_elementPickCompletion is null || string.IsNullOrWhiteSpace(_elementPickToken))
        {
            return;
        }

        try
        {
            var message = args.TryGetWebMessageAsString();
            if (message.Length > 32 * 1024)
            {
                return;
            }

            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (!root.TryGetProperty("channel", out var channel) ||
                channel.GetString() != "pistation.preview.annotation" ||
                !root.TryGetProperty("token", out var token) ||
                token.GetString() != _elementPickToken)
            {
                return;
            }

            if (root.TryGetProperty("cancelled", out var cancelled) && cancelled.GetBoolean())
            {
                FinishElementPicker(null);
                return;
            }

            if (!root.TryGetProperty("element", out var element))
            {
                return;
            }

            var selection = new PreviewElementSelection(
                Limit(element, "label", 256),
                Limit(element, "selector", 1024),
                Limit(element, "text", 512),
                Limit(element, "outerHtml", 2048),
                Number(element, "x"),
                Number(element, "y"),
                Number(element, "width"),
                Number(element, "height"));
            FinishElementPicker(selection);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            FinishElementPicker(null);
        }
    }

    private void FinishElementPicker(PreviewElementSelection? result)
    {
        var completion = _elementPickCompletion;
        _elementPickCompletion = null;
        _elementPickToken = null;
        if (Browser.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.Settings.IsWebMessageEnabled = false;
        }

        completion?.TrySetResult(result);
    }

    private static string Limit(JsonElement element, string property, int maximumLength)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var text = value.GetString() ?? string.Empty;
        return text.Length <= maximumLength ? text : text[..maximumLength];
    }

    private static double Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : 0;

    private void PublishBrowserState(string? context)
    {
        if (Browser.CoreWebView2 is not { } core)
        {
            return;
        }

        BrowserStateChanged?.Invoke(
            this,
            new PreviewBrowserStateEventArgs(
                context,
                LogicalSource(core.Source) ?? core.Source,
                core.DocumentTitle,
                core.CanGoBack,
                core.CanGoForward));
    }

    private static string FriendlyNavigationError(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.CannotConnect =>
            "The server refused the connection. Check that it is still running, then reload.",
        CoreWebView2WebErrorStatus.HostNameNotResolved =>
            "The preview host name could not be resolved.",
        CoreWebView2WebErrorStatus.Timeout =>
            "The preview server took too long to respond.",
        CoreWebView2WebErrorStatus.ConnectionAborted or
        CoreWebView2WebErrorStatus.ConnectionReset or
        CoreWebView2WebErrorStatus.Disconnected =>
            "The connection to the preview server was interrupted.",
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or
        CoreWebView2WebErrorStatus.CertificateExpired or
        CoreWebView2WebErrorStatus.ClientCertificateContainsErrors or
        CoreWebView2WebErrorStatus.CertificateRevoked or
        CoreWebView2WebErrorStatus.CertificateIsInvalid =>
            "The preview server presented an invalid HTTPS certificate.",
        _ => $"The page could not be loaded ({status}).",
    };

    private async Task CaptureRecordingFramesAsync(
        string frameDirectory,
        int frameRate,
        CancellationToken cancellationToken)
    {
        var frame = 0;
        var interval = TimeSpan.FromSeconds(1d / frameRate);
        while (!cancellationToken.IsCancellationRequested && frame < 1_440)
        {
            var started = DateTimeOffset.UtcNow;
            var content = await CapturePreviewPngAsync();
            await File.WriteAllBytesAsync(
                Path.Combine(frameDirectory, $"frame-{frame++:D5}.png"),
                content,
                cancellationToken);
            var remaining = interval - (DateTimeOffset.UtcNow - started);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken);
            }
        }
    }

    private static void DeleteRecordingFrames(string frameDirectory)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PiStationDesktop", "preview-recordings"));
        var target = Path.GetFullPath(frameDirectory);
        if (target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(target))
        {
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task ApplyColorSchemeAsync(PreviewColorScheme colorScheme)
    {
        if (Browser.CoreWebView2 is not { } core)
        {
            return;
        }

        var features = colorScheme == PreviewColorScheme.System
            ? "[]"
            : $"[{{\"name\":\"prefers-color-scheme\",\"value\":\"{colorScheme.ToString().ToLowerInvariant()}\"}}]";
        await core.CallDevToolsProtocolMethodAsync(
            "Emulation.setEmulatedMedia",
            $"{{\"features\":{features}}}");
    }

    private async Task ApplyZoomAsync(double zoomFactor)
    {
        if (Browser.CoreWebView2 is { } core)
        {
            await core.CallDevToolsProtocolMethodAsync(
                "Emulation.setPageScaleFactor",
                $"{{\"pageScaleFactor\":{zoomFactor.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
        }
    }

    private static double NormalizeZoom(double value) =>
        double.IsFinite(value) ? Math.Clamp(Math.Round(value, 2), 0.25, 3) : 1;

    private static string DecodeScriptString(string raw, string fallback, int maximumLength)
    {
        try
        {
            var decoded = JsonSerializer.Deserialize<string>(raw) ?? fallback;
            return decoded.Length <= maximumLength ? decoded : decoded[..maximumLength];
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static IEnumerable<ImportedCookie> ParseCookies(string content)
    {
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('['))
        {
            using var document = JsonDocument.Parse(content);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var name = JsonString(item, "name", 512);
                var value = JsonString(item, "value", 8 * 1024);
                var domain = JsonString(item, "domain", 512);
                var path = JsonString(item, "path", 1024);
                if (name.Length > 0 && domain.Length > 0)
                {
                    yield return new ImportedCookie(
                        name,
                        value,
                        domain,
                        path.Length == 0 ? "/" : path,
                        JsonBoolean(item, "secure"),
                        JsonBoolean(item, "httpOnly"));
                }
            }

            yield break;
        }

        foreach (var rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine;
            var httpOnly = false;
            if (line.StartsWith("#HttpOnly_", StringComparison.Ordinal))
            {
                line = line[10..];
                httpOnly = true;
            }
            else if (line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length >= 7 && fields[0].Length is > 0 and <= 512 &&
                fields[2].Length is > 0 and <= 1024 && fields[5].Length is > 0 and <= 512 &&
                fields[6].Length <= 8 * 1024)
            {
                yield return new ImportedCookie(
                    fields[5],
                    fields[6],
                    fields[0],
                    fields[2],
                    fields[3].Equals("TRUE", StringComparison.OrdinalIgnoreCase),
                    httpOnly);
            }
        }
    }

    private static string JsonString(JsonElement item, string name, int maximumLength)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var text = value.GetString() ?? string.Empty;
        return text.Length <= maximumLength ? text : text[..maximumLength];
    }

    private static bool JsonBoolean(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;

    private sealed record ImportedCookie(
        string Name,
        string Value,
        string Domain,
        string Path,
        bool IsSecure,
        bool IsHttpOnly);

    private const string DomSnapshotScript = """
        (() => JSON.stringify(Array.from(document.querySelectorAll('a,button,input,textarea,select,[role],h1,h2,h3'))
          .filter(element => {
            const rect = element.getBoundingClientRect();
            const style = getComputedStyle(element);
            return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
          })
          .slice(0, 250)
          .map((element, index) => ({
            index,
            tag: element.tagName.toLowerCase(),
            role: element.getAttribute('role') || '',
            label: (element.getAttribute('aria-label') || element.innerText || element.value || '').trim().slice(0, 240),
            id: element.id || '',
            name: element.getAttribute('name') || '',
            type: element.getAttribute('type') || ''
          }))))()
        """;

    private const string ElementPickerScript = """
        (() => {
          window.__piStationPreviewPicker?.cleanup?.();
          const token = '__PISTATION_TOKEN__';
          const overlay = document.createElement('div');
          overlay.setAttribute('data-pistation-preview-picker', '');
          Object.assign(overlay.style, {
            position: 'fixed', zIndex: '2147483647', pointerEvents: 'none',
            border: '2px solid #7c3aed', background: 'rgba(124,58,237,.14)',
            borderRadius: '3px', display: 'none', boxSizing: 'border-box'
          });
          document.documentElement.appendChild(overlay);
          const priorCursor = document.documentElement.style.cursor;
          document.documentElement.style.cursor = 'crosshair';
          const describe = (element) => {
            const tag = element.tagName.toLowerCase();
            const id = element.id ? `#${element.id}` : '';
            const classes = Array.from(element.classList).slice(0, 3).map(value => `.${value}`).join('');
            return `${tag}${id}${classes}`;
          };
          const selector = (element) => {
            const parts = [];
            let current = element;
            while (current && current.nodeType === Node.ELEMENT_NODE && parts.length < 8) {
              if (current.id) {
                parts.unshift(`#${CSS.escape(current.id)}`);
                break;
              }
              let part = current.tagName.toLowerCase();
              const siblings = current.parentElement
                ? Array.from(current.parentElement.children).filter(candidate => candidate.tagName === current.tagName)
                : [];
              if (siblings.length > 1) part += `:nth-of-type(${siblings.indexOf(current) + 1})`;
              parts.unshift(part);
              current = current.parentElement;
            }
            return parts.join(' > ');
          };
          const send = (payload) => chrome.webview.postMessage(JSON.stringify({
            channel: 'pistation.preview.annotation', token, ...payload
          }));
          const move = (event) => {
            const element = event.target;
            if (!(element instanceof Element) || element === overlay) return;
            const rect = element.getBoundingClientRect();
            Object.assign(overlay.style, {
              display: 'block', left: `${rect.left}px`, top: `${rect.top}px`,
              width: `${rect.width}px`, height: `${rect.height}px`
            });
          };
          const cleanup = () => {
            document.removeEventListener('mousemove', move, true);
            document.removeEventListener('click', pick, true);
            document.removeEventListener('keydown', key, true);
            overlay.remove();
            document.documentElement.style.cursor = priorCursor;
            delete window.__piStationPreviewPicker;
          };
          const pick = (event) => {
            const element = event.target;
            if (!(element instanceof Element) || element === overlay) return;
            event.preventDefault(); event.stopImmediatePropagation();
            const rect = element.getBoundingClientRect();
            const payload = { element: {
              label: describe(element), selector: selector(element),
              text: (element.innerText || element.getAttribute('aria-label') || '').trim().slice(0, 512),
              outerHtml: element.outerHTML.slice(0, 2048),
              x: rect.left, y: rect.top, width: rect.width, height: rect.height
            }};
            cleanup(); send(payload);
          };
          const key = (event) => {
            if (event.key !== 'Escape') return;
            event.preventDefault(); event.stopImmediatePropagation();
            cleanup(); send({ cancelled: true });
          };
          document.addEventListener('mousemove', move, true);
          document.addEventListener('click', pick, true);
          document.addEventListener('keydown', key, true);
          window.__piStationPreviewPicker = { cleanup };
          return true;
        })();
        """;
}

public sealed record PreviewElementSelection(
    string ElementLabel,
    string Selector,
    string Text,
    string OuterHtml,
    double X,
    double Y,
    double Width,
    double Height);

public sealed class PreviewNavigationStartingEventArgs(string? context, Uri uri) : EventArgs
{
    public string? Context { get; } = context;

    public Uri Uri { get; } = uri;
}

public sealed class PreviewNavigationCompletedEventArgs(string? context, bool succeeded, string? message) : EventArgs
{
    public string? Context { get; } = context;

    public bool Succeeded { get; } = succeeded;

    public string? Message { get; } = message;
}

public sealed class PreviewBrowserStateEventArgs(
    string? context,
    string? source,
    string? title,
    bool canGoBack,
    bool canGoForward) : EventArgs
{
    public string? Context { get; } = context;

    public string? Source { get; } = source;

    public string? Title { get; } = title;

    public bool CanGoBack { get; } = canGoBack;

    public bool CanGoForward { get; } = canGoForward;
}

public sealed class PreviewBrowserFailureEventArgs(string? context, string message) : EventArgs
{
    public string? Context { get; } = context;

    public string Message { get; } = message;
}
