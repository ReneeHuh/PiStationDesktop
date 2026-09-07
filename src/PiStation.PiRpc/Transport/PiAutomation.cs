using System.Text.Json.Nodes;

namespace PiStation.PiRpc.Transport;

public sealed partial class PiRpcConnection
{
    public async Task SetAutoCompactionAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureSuccess(await SendCommandAsync("set_auto_compaction", new JsonObject { ["enabled"] = enabled }, cancellationToken).ConfigureAwait(false));
    }

    public async Task SetAutoRetryAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureSuccess(await SendCommandAsync("set_auto_retry", new JsonObject { ["enabled"] = enabled }, cancellationToken).ConfigureAwait(false));
    }
}
