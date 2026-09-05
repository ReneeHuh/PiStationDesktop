using Microsoft.AspNetCore.SignalR.Client;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Streaming;

namespace PiStation.ClientRuntime;

public sealed class ThreadSubscription : IAsyncDisposable
{
    private readonly ConnectionSupervisor _supervisor;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _subscriptionTask;
    private bool _disposed;

    internal ThreadSubscription(ConnectionSupervisor supervisor, ThreadId threadId)
    {
        _supervisor = supervisor;
        Store = new ProjectionStore(threadId);
        _subscriptionTask = RunAsync(_stopping.Token);
    }

    public ProjectionStore Store { get; }

    public ThreadId ThreadId => Store.ThreadId;

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
                await foreach (var envelope in _supervisor.Connection.StreamAsync<ThreadEnvelope>(
                                   "SubscribeThread",
                                   ThreadId,
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
