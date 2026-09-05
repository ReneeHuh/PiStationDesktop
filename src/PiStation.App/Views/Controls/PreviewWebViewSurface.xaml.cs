using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using PiStation.App.ViewModels;
using Windows.Storage.Streams;

namespace PiStation.App.Views.Controls;

public sealed partial class PreviewWebViewSurface : UserControl, IDisposable
{
    private readonly Dictionary<ulong, string?> _navigationContexts = [];
    private string? _activeNavigationContext;
    private bool _disposed;
    private TaskCompletionSource<PreviewElementSelection?>? _elementPickCompletion;
    private string? _elementPickToken;
    private Task? _initializationTask;
    private string? _navigationContext;

    public PreviewWebViewSurface()
    {
        InitializeComponent();
    }

    public event EventHandler<PreviewNavigationStartingEventArgs>? NavigationStarted;

    public event EventHandler<PreviewNavigationCompletedEventArgs>? NavigationFinished;

    public event EventHandler<PreviewBrowserStateEventArgs>? BrowserStateChanged;

    public event EventHandler<PreviewBrowserFailureEventArgs>? BrowserFailed;

    public bool IsInitialized => Browser.CoreWebView2 is not null;

    public string? CurrentSource => Browser.CoreWebView2?.Source;

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

    public async Task NavigateAsync(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!WorkbenchPreviewViewModel.TryNormalizeAddress(uri.AbsoluteUri, out var normalized, out var error))
        {
            throw new ArgumentException(error, nameof(uri));
        }

        await InitializeAsync();
        Browser.CoreWebView2.Navigate(normalized.AbsoluteUri);
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

    public void Reload() => Browser.CoreWebView2?.Reload();

    public void Stop() => Browser.CoreWebView2?.Stop();

    public async Task<byte[]> CapturePreviewPngAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await InitializeAsync();
        var core = Browser.CoreWebView2 ??
            throw new InvalidOperationException("Web preview is not initialized.");
        using var stream = new InMemoryRandomAccessStream();
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
        Browser.CoreWebView2.Reload();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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
            await Browser.EnsureCoreWebView2Async();
            var core = Browser.CoreWebView2 ??
                throw new InvalidOperationException("WebView2 did not create its core instance.");

            core.Settings.AreDefaultContextMenusEnabled = true;
#if DEBUG
            core.Settings.AreDevToolsEnabled = true;
#else
            core.Settings.AreDevToolsEnabled = false;
#endif
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
                core.Source,
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
