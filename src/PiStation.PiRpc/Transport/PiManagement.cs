using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiStation.PiRpc.Diagnostics;

namespace PiStation.PiRpc.Transport;

public sealed partial class PiRpcConnection
{
    public const string ManagementCommand = "pistation-desktop-resources";
    public const string PlanCommand = "pistation-desktop-plan";
    public const string AgentsCommand = "pistation-desktop-agents";
    public const string SessionsCommand = "pistation-desktop-sessions";
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _managementRequests = new();

    public Task<JsonElement> ManageAsync(JsonObject action, CancellationToken cancellationToken = default) =>
        ManageCoreAsync(ManagementCommand, action, cancellationToken);

    public Task<JsonElement> NavigateSessionAsync(JsonObject action, CancellationToken cancellationToken = default) =>
        ManageCoreAsync(SessionsCommand, action, cancellationToken);
    public Task<JsonElement> SetSessionLabelAsync(JsonObject action, CancellationToken cancellationToken = default) =>
        ManageCoreAsync(SessionsCommand, action, cancellationToken);

    public Task<JsonElement> ManagePlanAsync(JsonObject action, CancellationToken cancellationToken = default) =>
        ManageCoreAsync(PlanCommand, action, cancellationToken);

    public Task<JsonElement> ManageAgentsAsync(JsonObject action, CancellationToken cancellationToken = default) =>
        ManageCoreAsync(AgentsCommand, action, cancellationToken);

    private async Task<JsonElement> ManageCoreAsync(string commandName, JsonObject action, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCancellation.Token);
        timeout.CancelAfter(commandName == SessionsCommand && action["action"]?.ToString() != "label" || action["action"]?.ToString() is "packageInstall" or "packageRemove" or "packageUpdate" or "login"
            ? _options.LongRunningCommandTimeout : _options.DefaultCommandTimeout);
        if (!(await GetCommandsAsync(timeout.Token).ConfigureAwait(false)).Any(command => command.Name == commandName))
            throw new PiRpcCommandException(commandName, "The PiStation management extension is unavailable. Restart Pi after updating PiStation.");
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _managementRequests[id] = completion;
        try
        {
            action = (JsonObject)action.DeepClone();
            action["id"] = id;
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(action.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await PromptAsync("/" + commandName + " " + payload, [], null, timeout.Token, expandSkills: false).ConfigureAwait(false);
            var record = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (!record.GetProperty("success").GetBoolean())
                throw new PiRpcCommandException(commandName, record.GetProperty("error").GetString() ?? "Management failed.");
            return record.GetProperty("data").Clone();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_disposeCancellation.IsCancellationRequested)
        {
            throw new PiRpcCommandException(commandName, "Pi management timed out. Refresh to check the saved state before retrying a change.");
        }
        finally { _managementRequests.TryRemove(id, out _); }
    }

    private bool TryHandleManagementResponse(JsonElement record)
    {
        const string prefix = "pistation-management:";
        if (GetOptionalString(record, "method") != "setStatus" ||
            GetOptionalString(record, "statusKey") is not { } key || !key.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        if (_managementRequests.TryGetValue(key[prefix.Length..], out var completion))
        {
            try
            {
                using var document = JsonDocument.Parse(GetRequiredString(record, "statusText"));
                completion.TrySetResult(document.RootElement.Clone());
            }
            catch (Exception exception) when (exception is JsonException or PiRpcConnectionException)
            {
                completion.TrySetException(new PiRpcConnectionException("Pi returned an invalid management response.", exception));
            }
        }
        return true;
    }
}
