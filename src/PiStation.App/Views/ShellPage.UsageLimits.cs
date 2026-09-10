using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PiStation.Protocol.Models;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private async Task OpenUsageLimitsAsync()
    {
        SettingsNavigation.SelectedItem = SettingsNavigation.MenuItems.OfType<NavigationViewItem>().First(item => item.Tag as string == "Limits");
        await OpenSettingsAsync();
    }
    private async void OnRefreshLimitsClicked(object sender, RoutedEventArgs e) => await ViewModel.RefreshLimitsAsync(refresh: ViewModel.CanOperate);
    private void OnLimitSourceSelected(object sender, SelectionChangedEventArgs e)
    {
        if ((sender as ComboBox)?.SelectedItem is not UsageLimitSourceConfiguration source) return;
        LimitManagementKey.Password = ""; ViewModel.Limits.Edit(source);
    }
    private void OnNewLimitSourceClicked(object sender, RoutedEventArgs e)
    {
        LimitSourceSelector.SelectedItem = null; LimitManagementKey.Password = ""; ViewModel.Limits.Edit(null);
    }
    private async void OnSaveLimitSourceClicked(object sender, RoutedEventArgs e)
    {
        var key = LimitManagementKey.Password; LimitManagementKey.Password = "";
        await ViewModel.SaveLimitSourceAsync(key);
    }
    private async void OnRemoveLimitSourceClicked(object sender, RoutedEventArgs e)
    {
        LimitManagementKey.Password = "";
        await ViewModel.SaveLimitSourceAsync("", remove: true);
    }
}
