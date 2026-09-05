using PiStation.ClientRuntime;
using PiStation.Host.Hosting;
using PiStation.App.ViewModels;

namespace PiStation.App.Composition;

internal sealed class AppRuntime(
    EmbeddedEnvironmentHost host,
    EnvironmentClient client,
    ShellViewModel viewModel) : IAsyncDisposable
{
    private readonly EnvironmentClient _client = client;
    private readonly EmbeddedEnvironmentHost _host = host;
    private readonly ShellViewModel _viewModel = viewModel;

    public async ValueTask DisposeAsync()
    {
        using var flushTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _viewModel.FlushDraftAsync(flushTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
        {
            // A debounced save already reports failures in the UI; shutdown must still release the host.
        }
        finally
        {
            await _viewModel.DisposeAsync().ConfigureAwait(false);
            await _client.DisposeAsync().ConfigureAwait(false);
            await _host.DisposeAsync().ConfigureAwait(false);
        }
    }
}
