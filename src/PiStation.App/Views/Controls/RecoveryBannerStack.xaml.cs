using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PiStation.App.ViewModels;

namespace PiStation.App.Views.Controls;

public sealed partial class RecoveryBannerSurface : UserControl
{
    public RecoveryBannerSurface(ShellViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public ShellViewModel ViewModel { get; }

    private async void OnReconnectClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.ReconnectAsync();

    private async void OnRestartPiClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.RestartPiAsync();

    private async void OnResolveCommandClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.ResolveUncertainCommandAsync();
}
