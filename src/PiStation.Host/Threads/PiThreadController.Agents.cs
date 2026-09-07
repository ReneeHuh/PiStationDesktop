using System.Text.Json;
using System.Text.Json.Nodes;
using PiStation.Host.Errors;
using PiStation.Protocol.Commands;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Serialization;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Threads;

public sealed partial class PiThreadController
{
    private static readonly SemaphoreSlim AgentSettingsGate = new(1, 1);

    private async Task<PiAgentSetup?> ReadAgentsOnStartAsync(CancellationToken cancellationToken)
    {
        if (_options.AgentExtensionPath is null) return null;
        try
        {
            var data = await _process!.Connection.ManageAgentsAsync(new JsonObject { ["action"] = "inspect" }, cancellationToken).ConfigureAwait(false);
            var setup = PiAgentSetup.Parse(data.GetRawText());
            if (setup.SessionId != _thread.PiSessionId) throw new JsonException("Agent setup belongs to another session.");
            return setup;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(_thread.PiSessionId, false, false, string.Empty, [], "Agent setup unavailable: " + exception.Message[..Math.Min(exception.Message.Length, 3000)]);
        }
    }

    public async Task ManageAgentsAsync(ThreadManageAgentsCommand command, CancellationToken cancellationToken)
    {
        if (command.Action is not ("inspect" or "enable" or "disable" or "save" or "delete") || command.ExpectedRevision?.Length > 128)
            throw new HostOperationException(ProtocolErrorCodes.PiCommandRejected, "Invalid agent setup action.");
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Journal.Projection.RuntimeState != ThreadRuntimeState.Ready || _process is null)
                throw new HostOperationException(ProtocolErrorCodes.ThreadBusy, "Finish the active turn before managing agents.");
            await AgentSettingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var data = await _process.Connection.ManageAgentsAsync(new JsonObject
                {
                    ["action"] = command.Action, ["expectedRevision"] = command.ExpectedRevision,
                    ["preset"] = command.Preset is null ? null : JsonSerializer.SerializeToNode(command.Preset, ProtocolJsonContext.Default.PiAgentPreset),
                }, cancellationToken).ConfigureAwait(false);
                var setup = PiAgentSetup.Parse(data.GetRawText());
                if (setup.SessionId != _thread.PiSessionId) throw new JsonException("Agent setup belongs to another session.");
                Journal.Commit(new PiAgentSetupChangedEvent(setup));
            }
            finally { AgentSettingsGate.Release(); }
        }
        finally { TouchRuntime(); _lifecycle.Release(); }
    }

    public Task<TurnId> RunAgentWorkflowAsync(PiAgentWorkflow workflow, ClientId clientId, CommandId commandId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var prompt = $"{(workflow.ResumeId is null ? "Run" : "Continue")} {workflow.Mode} agent workflow:\n\n" +
            string.Join("\n\n", (workflow.Tasks ?? []).Select((task, index) => $"{index + 1}. {task?.Agent}\n{task?.Task}"));
        return StartTurnCoreAsync(prompt, [], clientId, commandId, null, cancellationToken, workflow);
    }

    private async Task ValidateAgentWorkflowAsync(PiAgentWorkflow workflow, CancellationToken cancellationToken)
    {
        if (workflow.Mode is not ("single" or "parallel" or "chain") || workflow.Tasks is null || workflow.Tasks.Count is < 1 or > 8 ||
            workflow.Mode == "single" && workflow.Tasks.Count != 1 || workflow.Tasks.Any(t => t is null || string.IsNullOrWhiteSpace(t.Agent) || t.Agent.Length > 40 || string.IsNullOrWhiteSpace(t.Task) || t.Task.Length > 8192) ||
            workflow.ResumeId is not null && (workflow.Mode != "single" || workflow.ResumeId.Length != 32))
            throw new HostOperationException(ProtocolErrorCodes.PiCommandRejected, "Use one single task or up to eight parallel/chain tasks with a saved agent preset.");
        if (Journal.Projection.Plan?.Mode is { } mode && mode != "off")
            throw new HostOperationException(ProtocolErrorCodes.PiCommandRejected, "Return to normal tools to start a separate agent workflow. Plan execution can delegate through its approved turn.");
        if (Journal.Projection.Queue?.PendingMessageCount > 0 || Journal.Projection.Timeline.Any(item => item is ApprovalTimelineItem { State: InteractionState.Pending } or QuestionTimelineItem { State: InteractionState.Pending }))
            throw new HostOperationException(ProtocolErrorCodes.ThreadBusy, "Finish queued messages and interactions before starting a workflow.");
        var setup = await ReadAgentsOnStartAsync(cancellationToken).ConfigureAwait(false);
        if (setup is not { Available: true, Enabled: true })
            throw new HostOperationException(ProtocolErrorCodes.PiCommandRejected, setup is { Available: true } ? "Agent workflows are disabled. Enable them in the Agents panel." : setup?.Message ?? "The bundled agent integration is unavailable.");
        Journal.Commit(new PiAgentSetupChangedEvent(setup));
        if (workflow.ResumeId is null && workflow.Tasks.Any(task => !setup.Presets.Any(preset => preset.Name == task.Agent)))
            throw new HostOperationException(ProtocolErrorCodes.PiCommandRejected, "An agent preset changed. Refresh setup and review the workflow.");
        if (workflow.ResumeId is { } id && !(Journal.Projection.AgentActivities ?? []).Any(activity => activity.ControlId == id && activity.CanResume))
            throw new HostOperationException(ProtocolErrorCodes.PiCommandRejected, "This child has no saved continuation in the selected thread.");
        await _process!.Connection.ManageAgentsAsync(new JsonObject
        {
            ["action"] = "prepare", ["expectedRevision"] = setup.Revision,
            ["workflow"] = JsonSerializer.SerializeToNode(workflow, ProtocolJsonContext.Default.PiAgentWorkflow),
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task StopChildAsync(string controlId, CancellationToken cancellationToken)
    {
        if (_process is null || Journal.Projection.RuntimeState != ThreadRuntimeState.Running)
            throw new HostOperationException(ProtocolErrorCodes.PiCommandRejected, "This child is no longer running.");
        await _process.Connection.ManageAgentsAsync(new JsonObject { ["action"] = "stop", ["controlId"] = controlId }, cancellationToken).ConfigureAwait(false);
        TouchRuntime();
    }
}
