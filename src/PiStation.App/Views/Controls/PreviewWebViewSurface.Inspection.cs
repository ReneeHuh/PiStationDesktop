using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using PiStation.ClientRuntime;
using PiStation.Protocol.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PiStation.App.Views.Controls;

public sealed partial class PreviewWebViewSurface
{
    private readonly Dictionary<CoreWebView2DevToolsProtocolEventReceiver,
        Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2DevToolsProtocolEventReceivedEventArgs>> _diagnosticReceivers = [];
    private CancellationTokenSource _automationAccess = new();
    private CancellationTokenSource _automationDocument = new();
    private BrowserScriptExecutor? _scriptExecutor;
    private Task? _diagnosticsPreparation;
    private bool _automationEnabled;
    public SemaphoreSlim AutomationGate { get; } = new(1, 1);
    public BrowserDiagnostics AutomationDiagnostics { get; } = new();

    public void SetAutomationEnabled(bool enabled)
    {
        if (_automationEnabled == enabled) return;
        _automationEnabled = enabled;
        if (enabled)
        {
            _automationAccess.Dispose();
            _automationAccess = new();
        }
        else
        {
            _automationAccess.Cancel();
            foreach (var pair in _diagnosticReceivers) pair.Key.DevToolsProtocolEventReceived -= pair.Value;
            _diagnosticReceivers.Clear();
            _diagnosticsPreparation = null;
            AutomationDiagnostics.Clear();
        }
    }

    private void InterruptAutomationDocument()
    {
        _automationDocument.Cancel();
        _automationDocument.Dispose();
        _automationDocument = new();
        AutomationDiagnostics.NavigationStarted();
    }

    public async Task PrepareAutomationInspectionAsync(Action validate, CancellationToken cancellationToken)
    {
        await InitializeAsync();
        validate();
        if (!_automationEnabled) throw new InvalidOperationException("Browser inspection permission is off.");
        _scriptExecutor ??= new BrowserScriptExecutor(SendAutomationProtocolAsync, () =>
        {
            Dispose();
            BrowserFailed?.Invoke(this, new PreviewBrowserFailureEventArgs(_navigationContext,
                "The browser could not stop its JavaScript execution and was closed. Close and reopen this tab."));
        });
        _diagnosticsPreparation ??= EnableAutomationDiagnosticsAsync(_automationAccess.Token);
        await _diagnosticsPreparation.WaitAsync(cancellationToken);
        validate();
    }

    private Task<string> SendAutomationProtocolAsync(string method, string parameters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Browser.CoreWebView2.CallDevToolsProtocolMethodAsync(method, parameters).AsTask();
    }

    private async Task EnableAutomationDiagnosticsAsync(CancellationToken cancellationToken)
    {
        foreach (var method in new[] { "Runtime.consoleAPICalled", "Runtime.exceptionThrown", "Log.entryAdded",
            "Network.requestWillBeSent", "Network.responseReceived", "Network.loadingFailed", "Network.loadingFinished" })
        {
            var receiver = Browser.CoreWebView2.GetDevToolsProtocolEventReceiver(method);
            Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2DevToolsProtocolEventReceivedEventArgs> handler = (_, args) =>
            {
                if (_automationEnabled && !_disposed && !cancellationToken.IsCancellationRequested)
                    AutomationDiagnostics.Receive(method, args.ParameterObjectAsJson, url => LogicalSource(url) ?? url);
            };
            _diagnosticReceivers.Add(receiver, handler);
            receiver.DevToolsProtocolEventReceived += handler;
        }
        foreach (var domain in new[] { "Runtime", "Log", "Network", "Accessibility" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parameters = domain == "Network" ? "{\"maxTotalBufferSize\":0,\"maxResourceBufferSize\":0,\"maxPostDataSize\":0}" : "{}";
            await SendAutomationProtocolAsync(domain + ".enable", parameters).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    public async Task<JsonElement> EvaluateAutomationAsync(BrowserAutomationCommand command, Action validate, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _automationAccess.Token, _automationDocument.Token);
        return await _scriptExecutor!.EvaluateAsync(command.Expression!, command.AwaitPromise, command.TimeoutMs, validate, lifetime.Token);
    }

    public async Task<BrowserAutomationResult> CaptureAutomationSnapshotAsync(int timeoutMs, Action validate, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _automationAccess.Token, _automationDocument.Token);
        lifetime.CancelAfter(timeoutMs);
        var token = lifetime.Token;
        try
        {
            var evaluation = await _scriptExecutor!.EvaluateAsync(AutomationSnapshotScript, true, timeoutMs, validate, token, 256 * 1024);
            var page = evaluation.GetProperty("value");
            validate();
            var rawTree = await SendAutomationProtocolAsync("Accessibility.getFullAXTree", "{\"depth\":8}").WaitAsync(token);
            validate();
            // CDP has a depth option, but no breadth/byte limit. Bound parsing as well as
            // the projected wire result; report omitted trees instead of a huge payload.
            var treeOmitted = rawTree.Length > 4 * 1024 * 1024;
            using var tree = JsonDocument.Parse(treeOmitted ? "{\"nodes\":[]}" : rawTree);
            var png = await CaptureSnapshotImageAsync(validate, token);
            validate();
            token.ThrowIfCancellationRequested();
            var snapshot = BrowserSnapshotBuilder.Build(page, tree.RootElement, AutomationDiagnostics, png.Width, png.Height, url => LogicalSource(url) ?? url);
            if (treeOmitted)
            {
                var amended = System.Text.Json.Nodes.JsonNode.Parse(snapshot.GetRawText())!;
                amended["truncated"]!["accessibilityTree"] = true;
                snapshot = JsonSerializer.SerializeToElement(amended);
            }
            return new(true, snapshot, ScreenshotPng: png.Bytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_automationAccess.IsCancellationRequested)
        {
            throw new InvalidOperationException("Snapshot was interrupted by navigation or exceeded its time limit.");
        }
    }

    private async Task<(byte[] Bytes, int Width, int Height)> CaptureSnapshotImageAsync(Action validate, CancellationToken cancellationToken)
    {
        var bytes = await CapturePreviewPngAsync(validate).WaitAsync(cancellationToken);
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); }
        var decoder = await BitmapDecoder.CreateAsync(input);
        var width = Math.Min(1280u, decoder.PixelWidth);
        var height = Math.Max(1u, (uint)Math.Round(decoder.PixelHeight * (double)width / decoder.PixelWidth));
        if (width != decoder.PixelWidth)
        {
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateForTranscodingAsync(output, decoder);
            encoder.BitmapTransform.ScaledWidth = width;
            encoder.BitmapTransform.ScaledHeight = height;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            await encoder.FlushAsync();
            output.Seek(0);
            using var reader = new DataReader(output.GetInputStreamAt(0));
            await reader.LoadAsync((uint)output.Size);
            bytes = new byte[checked((int)output.Size)];
            reader.ReadBytes(bytes);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > BrowserAutomationLimits.MaximumScreenshotBytes)
            throw new InvalidOperationException("The snapshot image exceeds 4 MiB. Reduce the viewport and retry.");
        return (bytes, (int)width, (int)height);
    }

    private const string AutomationSnapshotScript = """
        (() => {
          const limit = (value, length) => String(value || '').slice(0, length);
          const selector = e => {
            const parts = [];
            for (let n = e; n && n.nodeType === 1 && parts.length < 8; n = n.parentElement) {
              if (n.id) { parts.unshift('#' + CSS.escape(n.id)); break; }
              const testId = n.getAttribute('data-testid');
              if (testId) { parts.unshift('[data-testid=' + CSS.escape(testId) + ']'); break; }
              let p = n.tagName.toLowerCase();
              if (n.parentElement) {
                const siblings = Array.from(n.parentElement.children).filter(s => s.tagName === n.tagName);
                if (siblings.length > 1) p += ':nth-of-type(' + (siblings.indexOf(n) + 1) + ')';
              }
              parts.unshift(p);
            }
            const result = parts.join(' > ');
            return result.length <= 1024 ? result : null;
          };
          const elements = [];
          let truncated = false, scanned = 0, elementBytes = 2;
          for (const e of document.querySelectorAll('a[href],button,input,textarea,select,[role],[tabindex]')) {
            if (++scanned > 5000 || elements.length === 200) { truncated = true; break; }
            const r = e.getBoundingClientRect(), style = getComputedStyle(e);
            if (!r.width || !r.height || style.display === 'none' || style.visibility === 'hidden') continue;
            const label = e.getAttribute('aria-label') || (e.labels && Array.from(e.labels).map(l => l.innerText).join(' ')) || e.innerText || e.getAttribute('name');
            const target = selector(e), role = e.getAttribute('role');
            if ((label || '').length > 256 || (role || '').length > 64 || target === null) truncated = true;
            const item = {tag:e.tagName.toLowerCase(),role:limit(role,64),name:limit(label,256),
              selector:target,x:r.x,y:r.y,width:r.width,height:r.height,disabled:!!e.disabled};
            const size = new TextEncoder().encode(JSON.stringify(item)).length + 1;
            if (elementBytes + size > 60000) { truncated = true; break; }
            elementBytes += size;
            elements.push(item);
          }
          const text = document.body?.innerText || '';
          return {url:location.href,title:limit(document.title,512),loading:document.readyState !== 'complete',
            visibleText:text.slice(0,20000),textTruncated:text.length > 20000,interactiveElements:elements,elementsTruncated:truncated};
        })()
        """;
}
