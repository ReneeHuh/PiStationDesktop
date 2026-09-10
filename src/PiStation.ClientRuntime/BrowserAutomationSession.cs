using System.Diagnostics;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

public sealed record BrowserPollingDiagnostics(int CompletedPolls, double MaximumReplyMilliseconds, double MaximumBetweenPollsMilliseconds);

/// <summary>A browser controller for one live connection. Commands are never replayed.</summary>
public sealed class BrowserAutomationSession : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<BrowserAutomationPoll>> _poll;
    private readonly Func<string, BrowserAutomationResult, CancellationToken, Task> _complete;
    private readonly Func<Task> _close;
    private readonly Func<BrowserRecordingChunk, CancellationToken, Task<BrowserRecordingArtifact?>>? _uploadRecording;
    private readonly CancellationTokenSource _stopping;
    private readonly object _gate = new();
    private readonly Task _monitor;
    private BrowserAutomationWork? _active;
    private BrowserAutomationWork? _pending;
    private int _disposed;

    internal BrowserAutomationSession(Func<CancellationToken, Task<BrowserAutomationPoll>> poll,
        Func<string, BrowserAutomationResult, CancellationToken, Task> complete, Func<Task> close, CancellationToken stopping,
        Func<BrowserRecordingChunk, CancellationToken, Task<BrowserRecordingArtifact?>>? uploadRecording = null)
    {
        _poll = poll; _complete = complete; _close = close;
        _uploadRecording = uploadRecording;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _monitor = MonitorAsync();
    }

    public bool IsActive => !_stopping.IsCancellationRequested;
    public CancellationToken Stopping => _stopping.Token;
    public string? Error { get; private set; }
    private BrowserPollingDiagnostics _polling = new(0, 0, 0);
    public BrowserPollingDiagnostics PollingDiagnostics => Volatile.Read(ref _polling);

    public BrowserAutomationWork? TakeNext()
    {
        lock (_gate)
        {
            var next = _pending;
            _pending = null;
            return next?.CancellationToken.IsCancellationRequested == false ? next : null;
        }
    }

    public void Cancel() => _stopping.Cancel();

    public async Task CompleteAsync(BrowserAutomationWork work, BrowserAutomationResult result)
    {
        work.CancellationToken.ThrowIfCancellationRequested();
        await _complete(work.Request.Id, result, work.CancellationToken).ConfigureAwait(false);
    }

    private async Task MonitorAsync()
    {
        var lastSuccess = Stopwatch.GetTimestamp();
        var pollStarted = lastSuccess;
        var polls = 0;
        var maximumReply = 0d;
        var maximumBetween = 0d;
        var initialGcPause = GC.GetTotalPauseDuration();
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                pollStarted = Stopwatch.GetTimestamp();
                maximumBetween = Math.Max(maximumBetween, Stopwatch.GetElapsedTime(lastSuccess, pollStarted).TotalMilliseconds);
                var state = await _poll(deadline.Token).ConfigureAwait(false);
                lastSuccess = Stopwatch.GetTimestamp();
                polls++;
                maximumReply = Math.Max(maximumReply, Stopwatch.GetElapsedTime(pollStarted, lastSuccess).TotalMilliseconds);
                Volatile.Write(ref _polling, new(polls, maximumReply, maximumBetween));
                lock (_gate)
                {
                    if (_active is { } active && state.ActiveRequestId != active.Request.Id)
                    {
                        active.Cancel();
                        _active = null;
                        _pending = null;
                    }
                    if (state.Request is { } request)
                    {
                        if (_active is not null || state.ActiveRequestId != request.Id)
                            throw new InvalidDataException("The host returned an inconsistent browser request.");
                        _active = _pending = new BrowserAutomationWork(request, _stopping.Token);
                    }
                }
                await Task.Delay(250, _stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            ThreadPool.GetAvailableThreads(out var workers, out var io);
            Error = $"Browser connection ended: {error.Message} (poll {polls + 1}, " +
                $"{Stopwatch.GetElapsedTime(pollStarted).TotalMilliseconds:F0} ms awaiting reply, " +
                $"{Stopwatch.GetElapsedTime(lastSuccess).TotalMilliseconds:F0} ms since last reply; " +
                $"max previous reply {maximumReply:F0} ms, between polls {maximumBetween:F0} ms; " +
                $"available workers {workers}, I/O {io}, queued work {ThreadPool.PendingWorkItemCount}, threads {ThreadPool.ThreadCount}, " +
                $"GC pause {(GC.GetTotalPauseDuration() - initialGcPause).TotalMilliseconds:F0} ms; {error.GetType().Name}).";
            Trace.TraceWarning(Error);
        }
        finally
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
            lock (_gate) { _active?.Cancel(); }
        }
    }

    public async Task<BrowserRecordingArtifact> UploadRecordingAsync(BrowserAutomationWork work, string path)
    {
        if (work.Request.Operation != "recording_stop" || _uploadRecording is null)
            throw new InvalidOperationException("Recording upload requires an active recording-stop request.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        if (file.Length is < 12 or > BrowserAutomationLimits.MaximumRecordingBytes)
            throw new InvalidDataException("Browser recordings must be MP4 files up to 64 MiB.");
        var buffer = new byte[BrowserAutomationLimits.RecordingChunkBytes];
        long offset = 0;
        while (offset < file.Length)
        {
            var count = await file.ReadAsync(buffer, work.CancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("The recording changed during upload.");
            var final = offset + count == file.Length;
            var artifact = await _uploadRecording(new(work.Request.Id, offset, buffer[..count], final), work.CancellationToken).ConfigureAwait(false);
            offset += count;
            if (final) return artifact ?? throw new InvalidDataException("The host did not finalize the recording.");
        }
        throw new InvalidDataException("The recording is empty.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _monitor.ConfigureAwait(false);
        await _close().ConfigureAwait(false);
    }
}

public sealed class BrowserAutomationWork
{
    private readonly CancellationTokenSource _stopping;
    private int _cancelled;
    public BrowserAutomationRequest Request { get; }
    public CancellationToken CancellationToken { get; }
    internal BrowserAutomationWork(BrowserAutomationRequest request, CancellationToken stopping)
    {
        Request = request;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        CancellationToken = _stopping.Token;
        // Host and desktop clocks may differ. The host enforces CreatedUtc and its
        // heartbeat cancels expired requests; locally bound time from receipt.
        _stopping.CancelAfter(BrowserAutomationLimits.RequestLifetime(request.Operation));
    }
    internal void Cancel()
    {
        if (Interlocked.Exchange(ref _cancelled, 1) != 0) return;
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
