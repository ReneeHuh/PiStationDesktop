using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private void SizeSettingsForWindow()
    {
        if (XamlRoot is null) return;
        var width = Math.Clamp(XamlRoot.Size.Width - 96, 240, 900);
        SettingsNavigation.Width = width;
        SettingsNavigation.MaxHeight = Math.Clamp(XamlRoot.Size.Height - 160, 180, 620);
        SettingsNavigation.PaneDisplayMode = width < 640 ? NavigationViewPaneDisplayMode.LeftMinimal : NavigationViewPaneDisplayMode.Left;
    }
    private async void OnRefreshRuntimeHealthClicked(object sender, RoutedEventArgs e) => await ViewModel.RefreshRuntimeHealthAsync();
    private async void OnSaveRuntimeHealthClicked(object sender, RoutedEventArgs e) => await ViewModel.SaveRuntimeHealthAsync();
    private async void OnClearRuntimeHealthClicked(object sender, RoutedEventArgs e) => await ViewModel.ClearRuntimeHealthAsync();
    private void OnApplyBackgroundPresetClicked(object sender, RoutedEventArgs e) => ViewModel.RuntimeHealth.ApplyPreset();
    private async void OnReloadRuntimeHealthSettingsClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.RuntimeHealth.ApplySettings(ViewModel.RuntimeHealth.Saved);
        await ViewModel.RefreshRuntimeHealthAsync();
    }
    private async void OnTerminateDiagnosticProcessClicked(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanOperate || ViewModel.RuntimeHealth.SelectedProcess?.Sample is not { CanTerminate: true } process) return;
        var action = ViewModel.PrepareDiagnosticProcessTermination(process);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Stop this process?", PrimaryButtonText = "Stop process",
            CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock { Text = $"{process.Name} · PID {process.ProcessId}\nThis stops only the selected process. Its active Pi turn or terminal may end. The host will recheck process identity and ownership.", TextWrapping = TextWrapping.Wrap } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "DiagnosticProcessStopDialog");
        if (await ShowConnectionDialogAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary) await action();
    }
}
