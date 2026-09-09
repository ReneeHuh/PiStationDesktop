using PiStation.ClientRuntime;
using PiStation.Protocol.Receipts;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    public PiShellViewModel PiShell { get; } = new();

    public async Task RunPiShellAsync(CancellationToken token = default)
    {
        if (!PiShell.CanRun || SelectedThread is not { } thread || Thread.Projection is not { } projection) return;
        var command = PiShell.CommandText;
        var exclude = !PiShell.IncludeInContext;
        PiShell.IsBusy = true;
        PiShell.Notice = "Starting shell command…";
        SetCommandPending(true);
        try
        {
            var receipt = await RequireClient().RunPiShellAsync(thread.ThreadId, command, exclude, projection.ProjectionEpoch, token).ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                if (SelectedThread?.ThreadId != thread.ThreadId) return;
                PiShell.Notice = receipt.State is CommandReceiptState.Accepted or CommandReceiptState.Completed ? "" : $"Shell request: {receipt.State}. Check its result before retrying.";
                HandleCommandReceipt(receipt);
            });
        }
        catch (CommandDispatchUncertainException error) { ShowUncertainCommand(error.CommandId, error.Message); }
        catch (Exception error) { RunOnUiThread(() => { if (SelectedThread?.ThreadId == thread.ThreadId) PiShell.Notice = error.Message; }); }
        finally { RunOnUiThread(() => PiShell.IsBusy = false); SetCommandPending(false); }
    }

    public async Task CancelPiShellAsync(CancellationToken token = default)
    {
        if (!PiShell.CanCancel || SelectedThread is not { } thread || Thread.Projection is not { } projection || PiShell.Snapshot is not { } shell) return;
        PiShell.IsBusy = true;
        try
        {
            var receipt = await RequireClient().CancelPiShellAsync(thread.ThreadId, shell.CommandId, projection.ProjectionEpoch, token).ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                if (SelectedThread?.ThreadId != thread.ThreadId) return;
                PiShell.Notice = receipt.State is CommandReceiptState.Accepted or CommandReceiptState.Completed
                    ? PiShell.Snapshot?.IsActive == true ? "Cancellation requested; waiting for the shell result." : ""
                    : $"Cancellation: {receipt.State}.";
                HandleCommandReceipt(receipt);
            });
        }
        catch (CommandDispatchUncertainException error) { ShowUncertainCommand(error.CommandId, error.Message); }
        catch (Exception error) { RunOnUiThread(() => { if (SelectedThread?.ThreadId == thread.ThreadId) PiShell.Notice = error.Message; }); }
        finally { RunOnUiThread(() => PiShell.IsBusy = false); }
    }
}
