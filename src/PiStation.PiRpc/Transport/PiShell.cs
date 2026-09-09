using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiStation.PiRpc.Transport;

public sealed record PiBashResult(string Output, int? ExitCode, bool Cancelled, bool Truncated, string? FullOutputPath);

public sealed partial class PiRpcConnection
{
    public async Task<PiBashResult> ExecuteBashAsync(string command, bool excludeFromContext,
        Action<string> output, Action dispatched, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var response = await SendCommandCoreAsync("bash", new JsonObject
        {
            ["command"] = command,
            ["excludeFromContext"] = excludeFromContext,
        }, output, dispatched, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var data = GetRequiredData(response);
        return new(GetRequiredString(data, "output"),
            data.TryGetProperty("exitCode", out var exit) && exit.ValueKind == JsonValueKind.Number ? exit.GetInt32() : null,
            data.GetProperty("cancelled").GetBoolean(), data.GetProperty("truncated").GetBoolean(),
            GetOptionalString(data, "fullOutputPath"));
    }

    public async Task AbortBashAsync(CancellationToken cancellationToken = default) =>
        EnsureSuccess(await SendCommandAsync("abort_bash", cancellationToken: cancellationToken).ConfigureAwait(false));
}
