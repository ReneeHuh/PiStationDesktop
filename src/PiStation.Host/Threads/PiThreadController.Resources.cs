using System.Text.Json;
using System.Text.Json.Nodes;
using PiStation.Host.Errors;
using PiStation.PiRpc.Diagnostics;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Threads;

public sealed partial class PiThreadController
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _extensionFailures = new(StringComparer.OrdinalIgnoreCase);
    public async Task<PiResourcesSnapshot> ManageResourcesAsync(ManagePiResourcesRequest request, CancellationToken cancellationToken)
    {
        if (request.ThreadId != _thread.ThreadId || request.Action is not ("inspect" or "toggle" or "trust" or "saveModel" or "packageInstall" or "packageRemove" or "packageUpdate" or "login" or "logout"))
            throw new HostOperationException(ProtocolErrorCodes.PiCommandRejected, "The Pi management request is invalid.");
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is null || Journal.Projection.RuntimeState != ThreadRuntimeState.Ready ||
                Journal.Projection.Timeline.Any(item => item is ApprovalTimelineItem { State: InteractionState.Pending } or QuestionTimelineItem { State: InteractionState.Pending }))
                throw new HostOperationException(ProtocolErrorCodes.ThreadBusy, "Finish the turn and pending interactions before managing Pi.");
            var action = JsonSerializer.SerializeToNode(request, ProtocolJsonContext.Default.ManagePiResourcesRequest)!.AsObject();
            if (action.ToJsonString().Length > 64 * 1024)
                throw new HostOperationException(ProtocolErrorCodes.PiCommandRejected, "Pi management request exceeds its size limit.");
            var data = await _process.Connection.ManageAsync(action, cancellationToken).ConfigureAwait(false);
            var result = data.Deserialize(ProtocolJsonContext.Default.PiResourcesSnapshot)
                ?? throw new JsonException("Pi returned an empty resource inventory.");
            if (request.Action != "inspect")
            {
                var refreshed = await _process.Connection.ManageAsync(new JsonObject { ["action"] = "inspect" }, cancellationToken).ConfigureAwait(false);
                result = refreshed.Deserialize(ProtocolJsonContext.Default.PiResourcesSnapshot)! with { Message = result.Message };
            }
            // Pi logs resource load failures on stderr, outside the command-discovery contract.
            if (result.ToolInventory is { } inventory)
            {
                if (inventory.Tools is null || inventory.Tools.Count > 256 || inventory.Tools.Any(tool => tool is null ||
                    string.IsNullOrWhiteSpace(tool.Name) || tool.Name.Length > 128 ||
                    tool.Description is null || tool.Description.Length > 240 || tool.Source is null || tool.Source.Length > 256) ||
                    inventory.Tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != inventory.Tools.Count)
                    throw new JsonException("Pi returned an invalid tool inventory.");
                try { if (inventory.Selection is not null) PiToolSelectionRules.Normalize(inventory.Selection); }
                catch (ArgumentException exception) { throw new JsonException("Pi returned an invalid runtime tool policy.", exception); }
            }
            var diagnostics = result.Diagnostics.ToList();
            if (!string.IsNullOrWhiteSpace(_process.StandardError)) diagnostics.Add(_process.StandardError);
            var resources = result.Resources.ToList();
            foreach (var path in (_options.Extensions.Paths ?? []).Concat(_extensionFailures.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!resources.Any(resource => string.Equals(resource.Path, path, StringComparison.OrdinalIgnoreCase)))
                    resources.Add(new("explicit:" + path, "extensions", Path.GetFileName(path), path, "Explicit extension", "temporary", true, false, false, ""));
            resources = resources.Select(resource => resource with { LoadError = _extensionFailures.GetValueOrDefault(resource.Path) ??
                _process.StandardError.Split('\n').FirstOrDefault(line => line.Replace('\\', '/').Contains(resource.Path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) &&
                    (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase))) }).ToList();
            TouchRuntime();
            return result with { Diagnostics = diagnostics, Resources = resources };
        }
        catch (Exception exception) when (exception is PiRpcException or JsonException)
        {
            throw new HostOperationException(ProtocolErrorCodes.PiCommandRejected, exception.Message);
        }
        finally { _lifecycle.Release(); }
    }
}
