using Microsoft.UI.Xaml;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private async Task OpenUsageDashboardAsync()
    {
        SettingsNavigation.SelectedItem = SettingsNavigation.MenuItems.OfType<Microsoft.UI.Xaml.Controls.NavigationViewItem>().First(item => item.Tag as string == "Usage");
        await OpenSettingsAsync();
    }
    private async void OnRefreshUsageClicked(object sender, RoutedEventArgs e) => await ViewModel.RefreshUsageAsync();
    private async void OnRescanUsageClicked(object sender, RoutedEventArgs e) => await ViewModel.RefreshUsageAsync(rescan: true);
    private async void OnRefreshUsagePricingClicked(object sender, RoutedEventArgs e) => await ViewModel.RefreshUsageAsync(pricing: true);
}
