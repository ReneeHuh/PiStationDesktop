using PiStation.ClientRuntime;
using PiStation.Protocol.Receipts;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    public PiPlanViewModel Plan { get; } = new();

    public async Task ManagePlanAsync(string action, CancellationToken cancellationToken = default)
    {
        var thread = SelectedThread;
        var state = Plan.Snapshot;
        if (thread is null || state is null || !Plan.CanAct) return;
        var revision = action == "save" ? Plan.EditRevision : state.Revision;
        var text = action == "save" ? Plan.EditText : null;
        Plan.IsBusy = true;
        Plan.Status = "Updating plan…";
        try
        {
            var receipt = await RequireClient().ManagePlanAsync(thread.ThreadId, action, revision, text, cancellationToken).ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                if (SelectedThread?.ThreadId != thread.ThreadId) return;
                Plan.Status = receipt.State is CommandReceiptState.Accepted or CommandReceiptState.Completed
                    ? action == "execute" ? "Execution approved. Progress reflects the agent's reported completed steps."
                        : action == "plan" ? "Planning enabled. Send your request in the composer to draft a plan." : "Plan updated."
                    : $"Plan action: {receipt.State}. Refresh and check the saved plan before retrying.";
                HandleCommandReceipt(receipt);
            });
        }
        catch (CommandDispatchUncertainException exception) { ShowUncertainCommand(exception.CommandId, exception.Message); }
        catch (Exception exception) { RunOnUiThread(() => { if (SelectedThread?.ThreadId == thread.ThreadId) Plan.Status = exception.Message; }); }
        finally { RunOnUiThread(() => Plan.IsBusy = false); }
    }
}
