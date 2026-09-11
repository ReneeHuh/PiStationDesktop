using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private void OnSettingsSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (SettingsNavigation is null) return;
        var query = sender.Text.Trim();
        foreach (var item in SettingsNavigation.MenuItems.OfType<NavigationViewItem>())
        {
            var keywords = (item.Tag as string) switch
            {
                "General" => "keyboard shortcuts quit sidebar composer behavior",
                "Appearance" => "theme color palette font typography contrast motion scale terminal",
                "PiRuntime" => "provider model authentication login executable setup retry compaction",
                "PiResources" => "extensions skills packages prompts instructions tools",
                "PiSessions" => "history branch bookmark import export sessions",
                "SourceControl" => "git branch commit pull request hosting repository publish",
                "Integrations" => "browser connections remote SSH Tailscale sharing",
                "Diagnostics" => "process CPU memory logs health performance",
                "Usage" => "cost tokens pricing chart consumption",
                "Limits" => "quota subscription accounts hub",
                "Updates" => "install version release",
                _ => "projects checkout worktree scripts",
            };
            item.Visibility = (item.Content + " " + keywords).Contains(query, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
