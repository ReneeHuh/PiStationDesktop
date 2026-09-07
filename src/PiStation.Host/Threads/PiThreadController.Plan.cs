using System.Text.Json.Nodes;
using PiStation.Host.Errors;
using PiStation.PiRpc.Transport;
using PiStation.Protocol.Commands;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Threads;

public sealed partial class PiThreadController
{
    private async Task<PiPlanState?> ReadPlanOnStartAsync(CancellationToken cancellationToken)
    {
        if (_options.PlanExtensionPath is null) return null;
        var connection = _process!.Connection;
        // A missing/failed bundled policy must prevent prompts from starting.
        var data = await connection.ManagePlanAsync(new JsonObject { ["action"] = "inspect" }, cancellationToken).ConfigureAwait(false);
        var plan = ReadPlan(data.GetRawText());
        await _database.RecordSettlementPlanAsync(_thread.ThreadId, plan, cancellationToken).ConfigureAwait(false);
        return plan;
    }

    public Task<PiStation.Protocol.Identifiers.TurnId> ExecutePlanAsync(long revision,
        PiStation.Protocol.Identifiers.ClientId clientId, PiStation.Protocol.Identifiers.CommandId commandId,
        CancellationToken cancellationToken) => StartTurnCoreAsync("Execute the approved plan and report completed steps.", [],
            clientId, commandId, revision, cancellationToken);

    private PiPlanState ReadPlan(string json)
    {
        var state = PiPlanState.Parse(json);
        if (state.SessionId != _thread.PiSessionId)
            throw new HostOperationException(ProtocolErrorCodes.PiCommandRejected, "Pi returned a plan for a different session.");
        return state;
    }

    public async Task ManagePlanAsync(ThreadManagePlanCommand command, CancellationToken cancellationToken)
    {
        if (command.Action is not ("inspect" or "plan" or "save" or "off"))
            throw new HostOperationException(ProtocolErrorCodes.PiCommandRejected, "Unknown plan action.");
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await ApplyPlanCommandAsync(command, cancellationToken).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task ApplyPlanCommandAsync(ThreadManagePlanCommand command, CancellationToken cancellationToken)
    {
        if (_process is null || Journal.Projection.RuntimeState != ThreadRuntimeState.Ready ||
            Journal.Projection.Queue?.PendingMessageCount > 0 ||
            Journal.Projection.Timeline.Any(item => item is ApprovalTimelineItem { State: InteractionState.Pending } or QuestionTimelineItem { State: InteractionState.Pending }))
            throw new HostOperationException(ProtocolErrorCodes.ThreadBusy, "Finish the turn and pending interactions before changing the plan.");
        if (command.ExpectedRevision < 0 || command.Text?.Length > 32 * 1024)
            throw new HostOperationException(ProtocolErrorCodes.PiCommandRejected, "The plan request is invalid or exceeds 32 KiB.");
        var data = await _process.Connection.ManagePlanAsync(new JsonObject
        {
            ["action"] = command.Action, ["expectedRevision"] = command.ExpectedRevision, ["text"] = command.Text,
        }, cancellationToken).ConfigureAwait(false);
        var plan = ReadPlan(data.GetRawText());
        await _database.RecordSettlementPlanAsync(_thread.ThreadId, plan, cancellationToken).ConfigureAwait(false);
        Journal.Commit(new PiPlanChangedEvent(plan));
        TouchRuntime();
    }
}
