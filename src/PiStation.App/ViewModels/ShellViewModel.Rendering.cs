using PiStation.Protocol.Projections;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    private readonly CancellationTokenSource _renderShutdown = new();
    private ThreadProjection? _pendingProjection;
    private int _renderScheduled;

    private void ScheduleProjection(ThreadProjection projection)
    {
        Interlocked.Exchange(ref _pendingProjection, projection);
        if (Interlocked.CompareExchange(ref _renderScheduled, 1, 0) == 0) _ = RenderPendingProjectionsAsync();
    }

    private async Task RenderPendingProjectionsAsync()
    {
        try
        {
            while (!_renderShutdown.IsCancellationRequested)
            {
                await Task.Delay(33, _renderShutdown.Token).ConfigureAwait(false);
                var projection = Interlocked.Exchange(ref _pendingProjection, null);
                if (projection is not null)
                {
                    await RunOnUiThreadAsync(() =>
                    {
                        if (SelectedThread?.ThreadId == projection.ThreadId && _subscription?.Store.Current is { } latest && latest.ThreadId == projection.ThreadId)
                            ApplyThreadProjection(latest);
                    }).ConfigureAwait(false);
                }
                Interlocked.Exchange(ref _renderScheduled, 0);
                if (Volatile.Read(ref _pendingProjection) is null || Interlocked.CompareExchange(ref _renderScheduled, 1, 0) != 0) return;
            }
        }
        catch (OperationCanceledException) when (_renderShutdown.IsCancellationRequested) { }
        catch (Exception exception) { ReportRuntimeError(exception); Interlocked.Exchange(ref _renderScheduled, 0); }
    }
}
