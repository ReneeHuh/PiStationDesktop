using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    private SetPiSessionLabelRequest? _pendingSessionLabel;

    private PiSessionPageRequest CreateSessionTreeRequest(ThreadId threadId, bool loadMore = false) => new(threadId,
        loadMore ? PiSessions.Snapshot?.NextOffset ?? 0 : 0, ExpectedRevision: loadMore ? PiSessions.Snapshot?.Revision : null,
        Filter: (PiSessionTreeFilter)PiSessions.FilterIndex, SearchQuery: PiSessions.SearchQuery, ActiveBranchOnly: PiSessions.ActiveBranchOnly, CollapsedEntryIds: PiSessions.CollapsedEntryIds.Order(StringComparer.Ordinal).ToArray());

    public async Task ClearSessionTreeFiltersAsync()
    {
        if (!PiSessions.CanAct) return;
        PiSessions.SearchQuery = string.Empty;
        PiSessions.FilterIndex = 0;
        PiSessions.ActiveBranchOnly = false;
        await InspectPiSessionAsync();
    }

    public async Task SetPiSessionLabelAsync(bool remove = false, CancellationToken cancellationToken = default)
    {
        var thread = SelectedThread;
        var snapshot = PiSessions.Snapshot;
        var entry = PiSessions.SelectedEntry?.Entry;
        if (!PiSessions.CanNavigate || thread is null || snapshot?.ThreadId != thread.ThreadId || entry is null) return;
        var label = remove || string.IsNullOrWhiteSpace(PiSessions.EntryLabel) ? null : PiSessions.EntryLabel.Trim();
        var request = new SetPiSessionLabelRequest(Guid.Empty, thread.ThreadId, entry.Id, snapshot.Revision, label);
        if (_pendingSessionLabel is null || _pendingSessionLabel with { OperationId = Guid.Empty } != request)
            _pendingSessionLabel = request with { OperationId = Guid.NewGuid() };
        var query = CreateSessionTreeRequest(thread.ThreadId);
        PiSessions.IsBusy = true;
        PiSessions.Status = label is null ? "Removing bookmark…" : "Saving bookmark…";
        try
        {
            var client = RequireClient();
            await client.SetPiSessionLabelAsync(_pendingSessionLabel, cancellationToken);
            _pendingSessionLabel = null;
            var refreshed = await client.InspectPiSessionPageAsync(query, cancellationToken);
            if (SelectedThread?.ThreadId != thread.ThreadId) return;
            PiSessions.Apply(refreshed);
            PiSessions.Status = (label is null ? "Bookmark removed. " : "Bookmark saved. ") + PiSessions.Status;
        }
        catch (Exception error) { PiSessions.Status = error.Message + " Refresh the tree to check the saved label, or retry the edit."; }
        finally { PiSessions.IsBusy = false; }
    }
}
