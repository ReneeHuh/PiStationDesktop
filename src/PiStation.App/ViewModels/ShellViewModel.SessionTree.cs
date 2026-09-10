namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    public async Task FoldSessionEntryAsync(bool expandAll = false)
    {
        if (!PiSessions.CanAct || (!expandAll && !PiSessions.CanFold)) return;
        if (expandAll) PiSessions.CollapsedEntryIds.Clear();
        else if (PiSessions.SelectedEntry is { } row && !PiSessions.CollapsedEntryIds.Remove(row.Entry.Id))
        {
            if (PiSessions.CollapsedEntryIds.Count >= 1000) { PiSessions.Status = "Expand a branch before folding more than 1,000 entries."; return; }
            PiSessions.CollapsedEntryIds.Add(row.Entry.Id);
        }
        await InspectPiSessionAsync();
    }

    public async Task<string?> NavigateSessionTreeAsync(int branchDirection = 0, bool copy = false)
    {
        var thread = SelectedThread;
        var entry = PiSessions.SelectedEntry?.Entry;
        if (thread is null || entry is null || !PiSessions.CanNavigate) return null;
        var request = CreateSessionTreeRequest(thread.ThreadId) with { ExpectedRevision = PiSessions.Snapshot?.Revision,
            AnchorEntryId = entry.Id, BranchDirection = branchDirection, IncludeEntryText = copy };
        PiSessions.IsBusy = true;
        try
        {
            var result = await RequireClient().InspectPiSessionPageAsync(request);
            if (SelectedThread?.ThreadId != thread.ThreadId) return null;
            PiSessions.Apply(result);
            return copy ? result.SelectedEntryText : null;
        }
        catch (Exception error) { PiSessions.Status = error.Message; return null; }
        finally { PiSessions.IsBusy = false; }
    }
}
