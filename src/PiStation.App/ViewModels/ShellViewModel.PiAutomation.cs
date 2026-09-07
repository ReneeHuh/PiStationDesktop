using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    public async Task ReloadPiAutomationAsync()
    {
        try
        {
            var saved = await RequireClient().GetPiAutomationSettingsAsync();
            Settings.ApplyAutomationSettings(saved);
            await RefreshPiAutomationStatusAsync();
        }
        catch (Exception exception) { Settings.AutomationStatus = $"Could not reload preferences: {exception.Message}"; }
    }

    public async Task SavePiAutomationAsync()
    {
        try
        {
            if (Settings.AutomationRevision < 0) throw new InvalidOperationException("Reload preferences before saving.");
            var requested = new PiAutomationSettings(Settings.ManageAutoCompaction ? Settings.AutoCompaction : null,
                Settings.ManageAutoRetry ? Settings.AutoRetry : null, Settings.AutomationRevision);
            var saved = await RequireClient().SavePiAutomationSettingsAsync(requested);
            Settings.ApplyAutomationSettings(saved);
            Settings.AutomationStatus = "Saved. Existing turns are unchanged; preferences apply before the next turn/restart. You can apply them to the selected idle runtime now.";
        }
        catch (Exception exception) { Settings.AutomationStatus = $"Save was not confirmed. Reload before retrying: {exception.Message}"; }
    }

    public async Task ApplyPiAutomationAsync()
    {
        var thread = SelectedThread;
        if (thread is null) { Settings.AutomationStatus = "Select a thread first."; return; }
        try
        {
            var status = await RequireClient().ApplyPiAutomationAsync(thread.ThreadId);
            if (SelectedThread?.ThreadId == thread.ThreadId) Settings.AutomationStatus = FormatAutomationStatus(status);
        }
        catch (Exception exception) { Settings.AutomationStatus = $"Apply was not confirmed. Refresh runtime status before retrying: {exception.Message}"; }
    }

    public async Task RefreshPiAutomationStatusAsync()
    {
        var thread = SelectedThread;
        if (thread is null) { Settings.AutomationStatus = "Saved preferences apply to all PiStation threads; select a thread to see runtime status."; return; }
        try
        {
            var status = await RequireClient().GetPiAutomationStatusAsync(thread.ThreadId);
            if (SelectedThread?.ThreadId == thread.ThreadId) Settings.AutomationStatus = FormatAutomationStatus(status);
        }
        catch (Exception exception) { Settings.AutomationStatus = $"Runtime status unavailable: {exception.Message}"; }
    }

    private static string FormatAutomationStatus(PiAutomationStatus status) =>
        $"Saved revision {status.Saved.Revision}; applied revision {status.AppliedRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}. " +
        $"Auto-compaction verified: {status.VerifiedAutoCompaction?.ToString() ?? "unknown"}; auto-retry acknowledged: {status.AcknowledgedAutoRetry?.ToString() ?? "unknown / unmanaged"}. {status.Message}";
}
