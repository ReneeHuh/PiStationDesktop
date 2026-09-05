using Microsoft.AspNetCore.SignalR.Client;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Streaming;

namespace PiStation.ClientRuntime;

public sealed class TerminalSubscription : IAsyncDisposable
{
    private readonly ConnectionSupervisor _supervisor;
    private readonly Action<TerminalSubscription> _disposedCallback;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _subscriptionTask;
    private bool _disposed;

    internal TerminalSubscription(
        ConnectionSupervisor supervisor,
        TerminalSessionId terminalSessionId,
        Action<TerminalSubscription> disposedCallback)
    {
        _supervisor = supervisor;
        _disposedCallback = disposedCallback ?? throw new ArgumentNullException(nameof(disposedCallback));
        Store = new TerminalStore(terminalSessionId);
        _subscriptionTask = RunAsync(_stopping.Token);
    }

    public TerminalStore Store { get; }

    public TerminalSessionId TerminalSessionId => Store.TerminalSessionId;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _subscriptionTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }

        _stopping.Dispose();
        _disposedCallback(this);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _supervisor.WaitUntilConnectedAsync(cancellationToken).ConfigureAwait(false);
                var cursor = Store.Cursor;
                var mustRestart = false;
                await foreach (var envelope in _supervisor.Connection.StreamAsync<TerminalEnvelope>(
                                   "SubscribeTerminal",
                                   TerminalSessionId,
                                   cursor,
                                   cancellationToken).ConfigureAwait(false))
                {
                    if (Store.Apply(envelope) == ProjectionApplyResult.ResyncRequired)
                    {
                        Store.Reset();
                        mustRestart = true;
                        break;
                    }
                }

                if (!mustRestart)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
