using PiStation.ClientRuntime;
using PiStation.Host.Hosting;
using PiStation.App.ViewModels;

namespace PiStation.App.Composition;

internal sealed class AppRuntime(
    EmbeddedEnvironmentHost? host,
    EnvironmentClient client,
    ShellViewModel viewModel,
    IAsyncDisposable? transport = null) : IAsyncDisposable
{
    private readonly EnvironmentClient _client = client;
    private readonly EmbeddedEnvironmentHost? _host = host;
    private readonly ShellViewModel _viewModel = viewModel;
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private bool _disposed;

    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        await _client.ConnectAsync(cancellationToken);
        await _viewModel.LoadProjectsAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => DisposeAsync(flushDraft: true);

    /// <summary>Disposes the runtime without flushing when its client identity is being replaced.</summary>
    public async ValueTask DisposeAsync(bool flushDraft)
    {
        await _disposeGate.WaitAsync().ConfigureAwait(false);
        if (_disposed)
        {
            _disposeGate.Release();
            return;
        }

        _disposed = true;
        try
        {
            await DesktopLifecycle.ShutdownAsync(
                _viewModel.FlushDraftAsync,
                [
                    () => _viewModel.DisposeAsync().AsTask(),
                    () => transport is null ? Task.CompletedTask : transport.DisposeAsync().AsTask(),
                    () => _client.DisposeAsync().AsTask(),
                    () => _host is null ? Task.CompletedTask : _host.DisposeAsync().AsTask(),
                ], TimeSpan.FromSeconds(5), flushDraft).ConfigureAwait(false);
            return;
        }
        finally
        {
            _disposeGate.Release();
        }
    }
}
