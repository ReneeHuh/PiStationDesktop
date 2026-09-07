using PiStation.ClientRuntime;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    public PiAgentsViewModel Agents { get; } = new();

    public Task ManageAgentsAsync(string action, CancellationToken cancellationToken = default) => RunAgentActionAsync(
        () => RequireClient().ManageAgentsAsync(SelectedThread!.ThreadId, action, action is "save" or "delete" ? Agents.EditRevision : Agents.Snapshot?.Revision,
            action is "save" or "delete" ? Agents.EditedPreset() : null, cancellationToken), "Agent setup updated.");

    public Task StartAgentWorkflowAsync(CancellationToken cancellationToken = default) => !Agents.CanRun ? Task.CompletedTask : RunAgentActionAsync(
        () => RequireClient().RunAgentWorkflowAsync(SelectedThread!.ThreadId, Agents.Workflow(), cancellationToken), "Workflow requested. Child activity will appear when Pi delegates the tasks.", collapseLauncher: true);

    public Task ContinueAgentAsync(AgentActivityRowViewModel activity, string task, CancellationToken cancellationToken = default) =>
        RunAgentActionAsync(() => RequireClient().RunAgentWorkflowAsync(SelectedThread!.ThreadId,
            new PiAgentWorkflow("single", [new(activity.Title, task)], activity.ControlId), cancellationToken), "Child continuation requested.", collapseLauncher: true);

    private async Task RunAgentActionAsync(Func<Task<CommandReceipt>> action, string success, bool collapseLauncher = false)
    {
        var thread = SelectedThread;
        if (thread is null || !Agents.CanManage) { Agents.Status = "Finish the active turn and return to normal tools before starting or configuring a workflow."; return; }
        Agents.IsBusy = true;
        try
        {
            var receipt = await action().ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                if (SelectedThread?.ThreadId != thread.ThreadId) return;
                HandleCommandReceipt(receipt);
                if (collapseLauncher && receipt.State is CommandReceiptState.Accepted or CommandReceiptState.Completed) Agents.LauncherExpanded = false;
                Agents.Status = receipt.State is CommandReceiptState.Accepted or CommandReceiptState.Completed ? success : $"Agent action: {receipt.State}. Check its result before retrying.";
            });
        }
        catch (CommandDispatchUncertainException exception) { ShowUncertainCommand(exception.CommandId, exception.Message); }
        catch (Exception exception) { RunOnUiThread(() => { if (SelectedThread?.ThreadId == thread.ThreadId) Agents.Status = exception.Message; }); }
        finally { RunOnUiThread(() => Agents.IsBusy = false); }
    }
}
