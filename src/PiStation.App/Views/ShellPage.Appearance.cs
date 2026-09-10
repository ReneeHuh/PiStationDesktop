using Microsoft.UI.Xaml;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private bool _appearanceIgnoreWhitespace;
    private void OnResetAppearanceClicked(object sender, RoutedEventArgs e) => ViewModel.Layout.ResetAppearance();
    private async void RefreshAppearanceDiff()
    {
        if (_appearanceIgnoreWhitespace == ViewModel.Layout.DiffIgnoreWhitespace) return;
        _appearanceIgnoreWhitespace = ViewModel.Layout.DiffIgnoreWhitespace;
        try { await ViewModel.RefreshAppearanceDiffAsync(); }
        catch (Exception error) { ViewModel.ReportRuntimeError(error); }
    }
}
