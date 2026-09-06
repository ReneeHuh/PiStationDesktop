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
    public async Task<PiResourcesSnapshot> ManageResourcesAsync(ManagePiResourcesRequest request, CancellationToken cancellationToken)
    {
        if (request.ThreadId != _thread.ThreadId || request.Action is not ("inspect" or "toggle" or "trust" or "saveModel"))
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
            var diagnostics = result.Diagnostics.ToList();
            if (!string.IsNullOrWhiteSpace(_process.StandardError)) diagnostics.Add(_process.StandardError);
            TouchRuntime();
            return result with { Diagnostics = diagnostics };
        }
        catch (Exception exception) when (exception is PiRpcException or JsonException)
        {
            throw new HostOperationException(ProtocolErrorCodes.PiCommandRejected, exception.Message);
        }
        finally { _lifecycle.Release(); }
    }
}
