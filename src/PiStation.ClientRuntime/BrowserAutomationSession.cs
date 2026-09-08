using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

/// <summary>A browser controller for one live connection. Commands are never replayed.</summary>
public sealed class BrowserAutomationSession : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<BrowserAutomationPoll>> _poll;
    private readonly Func<string, BrowserAutomationResult, CancellationToken, Task> _complete;
    private readonly Func<Task> _close;
    private readonly CancellationTokenSource _stopping;
    private readonly object _gate = new();
    private readonly Task _monitor;
    private BrowserAutomationWork? _active;
    private BrowserAutomationWork? _pending;
    private int _disposed;

    internal BrowserAutomationSession(Func<CancellationToken, Task<BrowserAutomationPoll>> poll,
        Func<string, BrowserAutomationResult, CancellationToken, Task> complete, Func<Task> close, CancellationToken stopping)
    {
        _poll = poll; _complete = complete; _close = close;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _monitor = MonitorAsync();
    }

    public bool IsActive => !_stopping.IsCancellationRequested;
    public string? Error { get; private set; }

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
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                var state = await _poll(deadline.Token).ConfigureAwait(false);
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
        catch (Exception error) { Error = "Browser connection ended: " + error.Message; }
        finally
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
            lock (_gate) { _active?.Cancel(); }
        }
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
        _stopping.CancelAfter(TimeSpan.FromSeconds(30));
    }
    internal void Cancel()
    {
        if (Interlocked.Exchange(ref _cancelled, 1) != 0) return;
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
