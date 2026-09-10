using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace PiStation.App.Views.Controls;

public sealed partial class TerminalWebViewSurface : UserControl, IDisposable
{
    private const string TerminalHost = "pistation-terminal.local";
    private const string TerminalOrigin = $"https://{TerminalHost}";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private bool _disposed;
    private bool _initialized;
    private bool _ready;
    private string _automationValue = string.Empty;
    private string _selectedText = string.Empty;
    private string[] _commandGestures = [];

    public TerminalWebViewSurface()
    {
        InitializeComponent();
        GotFocus += OnGotFocus;
    }

    public event EventHandler<TerminalWebDataEventArgs>? DataReceived;

    public event EventHandler<TerminalWebResizeEventArgs>? ResizeRequested;

    public event EventHandler<TerminalWebLinkEventArgs>? LinkRequested;

    public event EventHandler<TerminalWebContextMenuEventArgs>? ContextMenuRequested;

    public event EventHandler<TerminalWebStateEventArgs>? StateChanged;

    public event EventHandler<TerminalWebShortcutEventArgs>? ShortcutRequested;

    public event EventHandler? Ready;

    public event EventHandler<TerminalWebFailureEventArgs>? Failed;

    public string AutomationValue
    {
        get => _automationValue;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_automationValue, value, StringComparison.Ordinal))
            {
                return;
            }

            var previous = _automationValue;
            _automationValue = value;
            if (FrameworkElementAutomationPeer.FromElement(this) is TerminalWebViewSurfaceAutomationPeer peer)
            {
                peer.RaiseValueChanged(previous, value);
            }
        }
    }

    public bool IsReady => _ready;

    public async Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            var assetsPath = Path.Combine(AppContext.BaseDirectory, "TerminalWeb");
            if (!Directory.Exists(assetsPath))
            {
                throw new DirectoryNotFoundException($"Terminal web assets were not found at '{assetsPath}'.");
            }

            await Browser.EnsureCoreWebView2Async();
            var core = Browser.CoreWebView2 ?? throw new InvalidOperationException("WebView2 did not create its core instance.");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping(
                TerminalHost,
                assetsPath,
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.WebMessageReceived += OnWebMessageReceived;
            core.ProcessFailed += OnProcessFailed;
            core.NavigationStarting += OnNavigationStarting;
            Browser.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 24, 24, 27);
            Browser.Source = new Uri($"{TerminalOrigin}/index.html");
        }
        catch
        {
            _initialized = false;
            throw;
        }
    }

    public void Write(string text) => PostMessage(new { type = "write", text });

    public void Reset(string text) => PostMessage(new { type = "reset", text });

    public void Paste(string text) => PostMessage(new { type = "paste", text });

    public void SelectAll() => PostMessage(new { type = "selectAll" });

    public void SetCommandGestures(IEnumerable<string> gestures)
    {
        ArgumentNullException.ThrowIfNull(gestures);
        _commandGestures = gestures
            .Where(static gesture => !string.IsNullOrWhiteSpace(gesture))
            .Distinct(StringComparer.Ordinal)
            .Take(128)
            .ToArray();
        PostMessage(new { type = "commandGestures", gestures = _commandGestures });
    }

    public void OpenSearch()
    {
        if (!_ready || _disposed)
        {
            return;
        }

        SearchOverlay.Visibility = Visibility.Visible;
        SearchTextBox.Focus(FocusState.Programmatic);
        SearchTextBox.SelectAll();
        UpdateSearch();
    }

    public void SetTheme(
        Windows.UI.Color foreground,
        Windows.UI.Color background,
        Windows.UI.Color cursor,
        Windows.UI.Color selection,
        string fontFamily,
        double fontSize)
    {
        // WebView2 accepts only fully opaque or fully transparent backgrounds. The terminal
        // paints an opaque RGB canvas; native shell surface opacity must not reach this API.
        background.A = 255;
        Browser.DefaultBackgroundColor = background;
        PostMessage(new
        {
            type = "theme",
            theme = new
            {
                foreground = Rgb(foreground),
                background = Rgb(background),
                cursor = Rgb(cursor),
                selectionBackground = $"rgba({selection.R}, {selection.G}, {selection.B}, {selection.A / 255d:0.###})",
            },
            font = new
            {
                family = fontFamily,
                size = fontSize,
            },
        });
    }

    public void FocusTerminal()
    {
        if (!_ready)
        {
            return;
        }

        Browser.Focus(FocusState.Programmatic);
        PostMessage(new { type = "focus" });
    }

    public void ShowFailure(string message)
    {
        _ready = false;
        LoadingProgress.IsActive = false;
        LoadingMessage.Text = message;
        LoadingOverlay.Visibility = Visibility.Visible;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ready = false;
        GotFocus -= OnGotFocus;
        if (Browser.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.ProcessFailed -= OnProcessFailed;
            core.NavigationStarting -= OnNavigationStarting;
        }

        Browser.Close();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TerminalWebViewSurfaceAutomationPeer(this);

    private static object Rgb(Windows.UI.Color color) => new { r = color.R, g = color.G, b = color.B };

    private static bool IsTerminalOrigin(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.IdnHost, TerminalHost, StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort;

    private void PostMessage(object message)
    {
        if (!_ready || _disposed || Browser.CoreWebView2 is not { } core)
        {
            return;
        }

        core.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void OnGotFocus(object sender, RoutedEventArgs e) => FocusTerminal();

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsTerminalOrigin(args.Uri))
        {
            args.Cancel = true;
        }
    }

    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        var message = $"Ghostty terminal process failed: {args.ProcessFailedKind}.";
        ShowFailure(message);
        Failed?.Invoke(this, new TerminalWebFailureEventArgs(message));
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!IsTerminalOrigin(args.Source))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            switch (typeElement.GetString())
            {
                case "ready":
                    _ready = true;
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    Ready?.Invoke(this, EventArgs.Empty);
                    break;
                case "data" when root.TryGetProperty("data", out var data):
                    DataReceived?.Invoke(this, new TerminalWebDataEventArgs(data.GetString() ?? string.Empty));
                    break;
                case "resize" when root.TryGetProperty("columns", out var columns) &&
                                          root.TryGetProperty("rows", out var rows):
                    ResizeRequested?.Invoke(this, new TerminalWebResizeEventArgs(columns.GetInt32(), rows.GetInt32()));
                    break;
                case "link" when root.TryGetProperty("text", out var link):
                    LinkRequested?.Invoke(this, new TerminalWebLinkEventArgs(link.GetString() ?? string.Empty));
                    break;
                case "contextMenu" when root.TryGetProperty("x", out var x) &&
                                                root.TryGetProperty("y", out var y) &&
                                                root.TryGetProperty("selection", out var selection):
                    _selectedText = selection.GetString() ?? string.Empty;
                    ContextMenuRequested?.Invoke(
                        this,
                        new TerminalWebContextMenuEventArgs(
                            x.GetDouble(),
                            y.GetDouble(),
                            _selectedText));
                    break;
                case "searchRequested":
                    OpenSearch();
                    break;
                case "shortcut" when root.TryGetProperty("gesture", out var gesture) &&
                                             gesture.GetString() is { Length: > 0 } shortcut:
                    ShortcutRequested?.Invoke(this, new TerminalWebShortcutEventArgs(shortcut));
                    break;
                case "searchState" when root.TryGetProperty("query", out var query) &&
                                                root.TryGetProperty("activeIndex", out var activeIndex) &&
                                                root.TryGetProperty("total", out var total):
                    UpdateSearchState(
                        query.GetString() ?? string.Empty,
                        activeIndex.GetInt32(),
                        total.GetInt32());
                    break;
                case "fontState" when root.TryGetProperty("family", out var family) &&
                                              root.TryGetProperty("size", out var size) &&
                                              root.TryGetProperty("usedFallback", out var usedFallback):
                    var fontFamily = family.GetString() ?? "Default monospace";
                    var fontSize = size.GetDouble();
                    var fontDescription = usedFallback.GetBoolean()
                        ? $"Terminal font fallback at {fontSize:0} pixels"
                        : $"Terminal font {fontFamily.Split(',')[0].Trim()} at {fontSize:0} pixels";
                    AutomationProperties.SetItemType(this, fontDescription);
                    AutomationProperties.SetName(this, $"Terminal output — {fontDescription}");
                    break;
                case "state" when root.TryGetProperty("text", out var text) &&
                                          root.TryGetProperty("selection", out var stateSelection) &&
                                          root.TryGetProperty("mouseTracking", out var mouseTracking):
                    _selectedText = stateSelection.GetString() ?? string.Empty;
                    StateChanged?.Invoke(
                        this,
                        new TerminalWebStateEventArgs(text.GetString() ?? string.Empty, mouseTracking.GetBoolean()));
                    break;
                case "error" when root.TryGetProperty("message", out var error):
                    var message = error.GetString() ?? "Ghostty terminal failed.";
                    ShowFailure(message);
                    Failed?.Invoke(this, new TerminalWebFailureEventArgs(message));
                    break;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            Failed?.Invoke(this, new TerminalWebFailureEventArgs($"Terminal bridge message was invalid: {exception.Message}"));
        }
    }

    private void RequestAutomationContextMenu()
    {
        if (_ready)
        {
            ContextMenuRequested?.Invoke(
                this,
                new TerminalWebContextMenuEventArgs(12, 12, _selectedText));
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => UpdateSearch();

    private void OnSearchOptionClicked(object sender, RoutedEventArgs e) => UpdateSearch();

    private void OnSearchPreviousClicked(object sender, RoutedEventArgs e) =>
        PostMessage(new { type = "navigateSearch", direction = -1 });

    private void OnSearchNextClicked(object sender, RoutedEventArgs e) =>
        PostMessage(new { type = "navigateSearch", direction = 1 });

    private void OnCloseSearchClicked(object sender, RoutedEventArgs e) => CloseSearch();

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseSearch();
            return;
        }

        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        PostMessage(new { type = "navigateSearch", direction = shift ? -1 : 1 });
    }

    private void UpdateSearch()
    {
        if (SearchOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        SearchCountText.Text = string.IsNullOrEmpty(SearchTextBox.Text) ? "0 of 0" : "Searching…";
        SearchPreviousButton.IsEnabled = false;
        SearchNextButton.IsEnabled = false;
        PostMessage(new
        {
            type = "search",
            query = SearchTextBox.Text,
            caseSensitive = SearchMatchCaseToggle.IsChecked == true,
            wholeWord = SearchWholeWordToggle.IsChecked == true,
        });
    }

    private void UpdateSearchState(string query, int activeIndex, int total)
    {
        if (SearchOverlay.Visibility != Visibility.Visible ||
            !string.Equals(query, SearchTextBox.Text, StringComparison.Ordinal))
        {
            return;
        }

        SearchCountText.Text = total > 0 && activeIndex >= 0
            ? $"{activeIndex + 1} of {total}"
            : "0 of 0";
        SearchPreviousButton.IsEnabled = total > 0;
        SearchNextButton.IsEnabled = total > 0;
    }

    private void CloseSearch()
    {
        SearchOverlay.Visibility = Visibility.Collapsed;
        PostMessage(new { type = "clearSearch" });
        FocusTerminal();
    }

    private sealed class TerminalWebViewSurfaceAutomationPeer(TerminalWebViewSurface owner)
        : FrameworkElementAutomationPeer(owner), IValueProvider, IInvokeProvider
    {
        private TerminalWebViewSurface Terminal => (TerminalWebViewSurface)Owner;

        public bool IsReadOnly => true;

        public string Value => Terminal.AutomationValue;

        public void SetValue(string value) => throw new InvalidOperationException("Terminal output is read-only.");

        public void Invoke() => Terminal.DispatcherQueue.TryEnqueue(Terminal.RequestAutomationContextMenu);

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface is PatternInterface.Value or PatternInterface.Invoke
                ? this
                : base.GetPatternCore(patternInterface);

        protected override string GetClassNameCore() => "Terminal";

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

        internal void RaiseValueChanged(string previous, string current) => RaisePropertyChangedEvent(
            ValuePatternIdentifiers.ValueProperty,
            previous,
            current);
    }
}

public sealed class TerminalWebDataEventArgs(string data) : EventArgs
{
    public string Data { get; } = data;
}

public sealed class TerminalWebResizeEventArgs(int columns, int rows) : EventArgs
{
    public int Columns { get; } = columns;

    public int Rows { get; } = rows;
}

public sealed class TerminalWebLinkEventArgs(string text) : EventArgs
{
    public string Text { get; } = text;
}

public sealed class TerminalWebContextMenuEventArgs(double x, double y, string selection) : EventArgs
{
    public double X { get; } = x;

    public double Y { get; } = y;

    public string Selection { get; } = selection;
}

public sealed class TerminalWebStateEventArgs(string text, bool mouseTracking) : EventArgs
{
    public string Text { get; } = text;

    public bool MouseTracking { get; } = mouseTracking;
}

public sealed class TerminalWebShortcutEventArgs(string gesture) : EventArgs
{
    public string Gesture { get; } = gesture;
}

public sealed class TerminalWebFailureEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}
