using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    public async Task SaveSettlementSettingsAsync()
    {
        try
        {
            if (Settings.AutoSettleInactive && (!double.IsFinite(Settings.AutoSettleDays) ||
                Settings.AutoSettleDays < 1 || Settings.AutoSettleDays > 365 || Settings.AutoSettleDays != Math.Truncate(Settings.AutoSettleDays)))
                throw new InvalidOperationException("Choose a whole number of days from 1 to 365.");
            await RequireClient().SaveSettlementSettingsAsync(new SettlementSettings(
                Settings.AutoSettleInactive ? (int)Settings.AutoSettleDays : null, Settings.AutoSettleMerged, Settings.AutoSettleClosed));
            Settings.Status = "Settlement rules saved. Applied on the next minute's check while PiStation is open.";
        }
        catch (Exception exception) { Settings.Status = $"Settlement settings were not saved: {exception.Message}"; }
    }
}
