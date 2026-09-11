using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using PiStation.App.Composition;

namespace PiStation.App.Views.Controls;

public sealed partial class PreviewWebViewSurface
{
    private BrowserVideoCapture? _videoCapture;
    private CoreWebView2DevToolsProtocolEventReceiver? _videoEvents;
    private Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2DevToolsProtocolEventReceivedEventArgs>? _videoFrameHandler;
    private string? _recordingOwner;
    private CancellationTokenRegistration _recordingOwnerCancellation;
    private readonly SemaphoreSlim _recordingGate = new(1, 1);
    public bool IsRecording => _videoCapture is not null;
    public string? RecordingOwner => _recordingOwner;
    public bool RecordingFinished => _videoCapture?.HasFinished == true;

    public async Task StartVideoRecordingAsync(string owner, string directory, int frameRate,
        CancellationToken ownerLifetime, CancellationToken requestCancellation)
    {
        await _recordingGate.WaitAsync(requestCancellation);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_videoCapture is not null) throw new InvalidOperationException("This tab already has a recording. Stop it before starting another.");
            ownerLifetime.ThrowIfCancellationRequested();
            await InitializeAsync();
            var core = Browser.CoreWebView2 ?? throw new InvalidOperationException("The browser is unavailable.");
            var capture = new BrowserVideoCapture(directory, frameRate);
            _videoCapture = capture;
            _recordingOwner = owner;
            _videoEvents = core.GetDevToolsProtocolEventReceiver("Page.screencastFrame");
            _videoFrameHandler = (sender, args) => OnRecordingFrame(capture, sender, args);
            _videoEvents.DevToolsProtocolEventReceived += _videoFrameHandler;
            _recordingOwnerCancellation = ownerLifetime.Register(() =>
                DispatcherQueue.TryEnqueue(() => { if (ReferenceEquals(_videoCapture, capture)) _ = CancelVideoRecordingAsync(capture); }));
            try
            {
                await core.CallDevToolsProtocolMethodAsync("Page.startScreencast", "{\"format\":\"jpeg\",\"quality\":80,\"maxWidth\":1280,\"maxHeight\":1280,\"everyNthFrame\":1}")
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(5), requestCancellation);
                await capture.StartAsync(requestCancellation);
                ownerLifetime.ThrowIfCancellationRequested();
                _ = ObserveRecordingCompletionAsync(capture);
            }
            catch { await ReleaseVideoCaptureAsync(capture); throw; }
        }
        finally { _recordingGate.Release(); }
    }

    private async void OnRecordingFrame(BrowserVideoCapture capture, CoreWebView2 sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (!ReferenceEquals(_videoCapture, capture)) return;
        try
        {
            using var message = JsonDocument.Parse(args.ParameterObjectAsJson);
            var sessionId = message.RootElement.GetProperty("sessionId").GetInt32();
            var acknowledgement = sender.CallDevToolsProtocolMethodAsync("Page.screencastFrameAck", JsonSerializer.Serialize(new { sessionId }));
            try
            {
                var encoded = message.RootElement.GetProperty("data").GetString()!;
                if (encoded.Length > 6 * 1024 * 1024) throw new InvalidDataException("The browser recording frame is too large.");
                capture.PushFrame(encoded);
            }
            finally { await acknowledgement; }
        }
        catch (Exception error) { capture.Fail(error); }
    }

    private async Task ObserveRecordingCompletionAsync(BrowserVideoCapture capture)
    {
        try { await capture.Completion; }
        catch (Exception) { /* Stop reports encoder failures; cancellation discards the file. */ }
        if (!ReferenceEquals(_videoCapture, capture) || _disposed) return;
        try
        {
            if (_videoEvents is not null) _videoEvents.DevToolsProtocolEventReceived -= _videoFrameHandler;
            _videoEvents = null;
            _videoFrameHandler = null;
            if (Browser.CoreWebView2 is { } core) await core.CallDevToolsProtocolMethodAsync("Page.stopScreencast", "{}");
            PublishBrowserState(_navigationContext);
        }
        catch (Exception) { /* The tab may have closed while the encoder completed. */ }
    }

    public async Task<BrowserVideoResult> StopVideoRecordingAsync(string? owner, CancellationToken token)
    {
        await _recordingGate.WaitAsync(token);
        try
        {
            var capture = _videoCapture ?? throw new InvalidOperationException("This tab is not recording.");
            if (owner is not null && _recordingOwner != owner) throw new InvalidOperationException("This recording belongs to another controller.");
            try { return await capture.StopAsync(token); }
            finally { await ReleaseVideoCaptureAsync(capture); }
        }
        finally { _recordingGate.Release(); }
    }

    public Task CancelVideoRecordingAsync() => CancelVideoRecordingAsync(null);

    private async Task CancelVideoRecordingAsync(BrowserVideoCapture? expected)
    {
        await _recordingGate.WaitAsync();
        try { if (_videoCapture is { } capture && (expected is null || ReferenceEquals(capture, expected))) await ReleaseVideoCaptureAsync(capture); }
        finally { _recordingGate.Release(); }
    }

    private async Task ReleaseVideoCaptureAsync(BrowserVideoCapture capture)
    {
        if (!ReferenceEquals(_videoCapture, capture)) return;
        _recordingOwnerCancellation.Dispose();
        if (_videoEvents is not null) _videoEvents.DevToolsProtocolEventReceived -= _videoFrameHandler;
        _videoEvents = null;
        _videoFrameHandler = null;
        try
        {
            if (!_disposed && Browser.CoreWebView2 is { } core)
                await core.CallDevToolsProtocolMethodAsync("Page.stopScreencast", "{}").AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Browser capture stopped: {0}", error.Message); }
        await capture.DisposeAsync();
        _videoCapture = null;
        _recordingOwner = null;
    }
}
