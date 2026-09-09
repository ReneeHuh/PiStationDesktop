using Microsoft.AspNetCore.SignalR.Client;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Streaming;

namespace PiStation.ClientRuntime;

public sealed class TerminalSubscription : IAsyncDisposable
{
    private readonly ConnectionSupervisor _supervisor;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _subscriptionTask;
    private readonly Func<ValueTask>? _release;
    private int _disposed;

    internal TerminalSubscription(
        ConnectionSupervisor supervisor,
        TerminalSessionId terminalSessionId)
    {
        _supervisor = supervisor;
        Store = new TerminalStore(terminalSessionId);
        _supervisor.StateChanged += OnStateChanged;
        _subscriptionTask = RunAsync(_stopping.Token);
    }

    internal TerminalSubscription(TerminalSubscription shared, Func<ValueTask> release)
    {
        _supervisor = shared._supervisor;
        Store = shared.Store;
        _subscriptionTask = Task.CompletedTask;
        _release = release;
    }

    public TerminalStore Store { get; }

    public TerminalSessionId TerminalSessionId => Store.TerminalSessionId;

    private void OnStateChanged(object? sender, ConnectionStateChangedEventArgs args)
    {
        if (args.State != EnvironmentConnectionState.Connected) Store.SetSynchronized(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_release is not null)
        {
            _stopping.Dispose();
            await _release().ConfigureAwait(false);
            return;
        }
        await _stopping.CancelAsync().ConfigureAwait(false);
        _supervisor.StateChanged -= OnStateChanged;
        Store.SetSynchronized(false);
        try
        {
            await _subscriptionTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }

        _stopping.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _supervisor.WaitUntilConnectedAsync(cancellationToken).ConfigureAwait(false);
                var connection = _supervisor.Connection;
                var cursor = Store.Cursor;
                Store.SetSynchronized(false);
                var mustRestart = false;
                await foreach (var envelope in connection.StreamAsync<TerminalEnvelope>(
                                   "SubscribeTerminal",
                                   TerminalSessionId,
                                   cursor,
                                   cancellationToken).ConfigureAwait(false))
                {
                    if (!ReferenceEquals(connection, _supervisor.Connection) || _supervisor.State != EnvironmentConnectionState.Connected) break;
                    if (envelope is TerminalSynchronizedEnvelope synchronized)
                    {
                        if (Store.Cursor == new TerminalCursor(synchronized.Sequence, synchronized.Epoch))
                        { Store.SetSynchronized(true); continue; }
                        Store.Reset();
                        mustRestart = true;
                        break;
                    }
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
