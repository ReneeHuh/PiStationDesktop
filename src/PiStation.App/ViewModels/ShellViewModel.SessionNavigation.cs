using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    private NavigatePiSessionRequest? _pendingNavigation;

    public async Task NavigatePiSessionAsync(CancellationToken cancellationToken = default)
    {
        var thread = SelectedThread;
        var snapshot = PiSessions.Snapshot;
        var entry = PiSessions.SelectedEntry?.Entry;
        if (!PiSessions.CanNavigate || thread is null || snapshot?.ThreadId != thread.ThreadId || entry is null) return;
        var request = new NavigatePiSessionRequest(Guid.Empty, thread.ThreadId, entry.Id, snapshot.Revision,
            PiSessions.SummarizeBranch, string.IsNullOrWhiteSpace(PiSessions.SummaryInstructions) ? null : PiSessions.SummaryInstructions,
            PiSessions.ReplaceSummaryInstructions);
        if (_pendingNavigation is null || _pendingNavigation with { OperationId = Guid.Empty } != request)
            _pendingNavigation = request with { OperationId = Guid.NewGuid() };
        var pending = _pendingNavigation;
        var query = CreateSessionTreeRequest(thread.ThreadId);
        PiSessions.IsBusy = true;
        PiSessions.Status = "Saving the draft before switching branches…";
        try
        {
            var client = RequireClient();
            await Composer.PrepareTurnAsync(cancellationToken);
            if (SelectedThread?.ThreadId != thread.ThreadId) throw new InvalidOperationException("The selected thread changed. Refresh its session tree.");
            PiSessions.CanCancelNavigation = true;
            PiSessions.Status = request.Summarize ? "Summarizing and switching branches…" : "Switching branches…";
            var result = await client.NavigatePiSessionAsync(pending, cancellationToken);
            _pendingNavigation = null;
            if (SelectedThread?.ThreadId != thread.ThreadId) return;
            PiSessions.NavigationPrompt = result.EditorText ?? string.Empty;
            var refreshed = await client.InspectPiSessionPageAsync(query, cancellationToken);
            if (SelectedThread?.ThreadId != thread.ThreadId) return;
            PiSessions.Apply(refreshed);
            PiSessions.Status = result.Cancelled ? "Branch switch canceled. Your current draft is preserved."
                : "Branch switched. Your draft and workspace files are preserved." +
                    (result.EditorText is null ? string.Empty : " The selected prompt is available below for copying.");
        }
        catch (Exception error)
        {
            PiSessions.Status = error.Message + " Refresh the tree to check the saved branch, or retry the same selection.";
        }
        finally { PiSessions.CanCancelNavigation = false; PiSessions.IsBusy = false; }
    }

    public async Task CancelPiSessionNavigationAsync()
    {
        if (_pendingNavigation is not { } pending || !PiSessions.CanCancelNavigation) return;
        PiSessions.CanCancelNavigation = false;
        PiSessions.Status = "Canceling branch switch…";
        try
        {
            var client = RequireClient();
            // Navigation can still be waiting for Pi startup when Cancel is clicked.
            while (_pendingNavigation == pending && PiSessions.IsBusy)
            {
                if (await client.CancelPiSessionNavigationAsync(new(pending.ThreadId, pending.OperationId))) return;
                await Task.Delay(100);
            }
        }
        catch (Exception error)
        {
            if (_pendingNavigation != pending || !PiSessions.IsBusy) return;
            PiSessions.CanCancelNavigation = true;
            PiSessions.Status = error.Message + " Retry cancellation or refresh the tree to check the saved outcome.";
        }
    }
}
