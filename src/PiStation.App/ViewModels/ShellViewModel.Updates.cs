using Windows.ApplicationModel;
using Windows.System;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    public async Task CheckForUpdatesAsync()
    {
        Settings.UpdateSummary = "Checking for updates…";
        try
        {
            var package = Package.Current;
            var feed = package.GetAppInstallerInfo()?.Uri;
            if (feed is null)
            {
                Settings.UpdateSummary = "No update feed is configured for this installation. Install a newer package manually. Production publisher, signing, and update-feed setup are scheduled for later.";
                return;
            }
            var result = await package.CheckUpdateAvailabilityAsync();
            Settings.UpdateSummary = result.Availability switch
            {
                PackageUpdateAvailability.NoUpdates => "Pi Station is up to date.",
                PackageUpdateAvailability.Available => "An update is available. Open the installer to review and install it.",
                PackageUpdateAvailability.Required => "A required update is available. Open the installer to review and install it.",
                PackageUpdateAvailability.Error => $"The update check failed: {result.ExtendedError?.Message ?? "The feed could not be reached."}",
                _ => "Windows could not determine update availability. Try again later.",
            };
            Settings.CanOpenUpdateInstaller = result.Availability is PackageUpdateAvailability.Available or PackageUpdateAvailability.Required;
        }
        catch (Exception exception) { Settings.UpdateSummary = $"Updates are unavailable: {exception.Message}"; }
    }

    public async Task OpenUpdateInstallerAsync()
    {
        try
        {
            var feed = Package.Current.GetAppInstallerInfo()?.Uri;
            if (feed?.Scheme == "https") await Launcher.LaunchUriAsync(new Uri($"ms-appinstaller:?source={Uri.EscapeDataString(feed.AbsoluteUri)}"));
        }
        catch (Exception exception) { Settings.UpdateSummary = $"The update installer could not be opened: {exception.Message}"; }
    }
}
