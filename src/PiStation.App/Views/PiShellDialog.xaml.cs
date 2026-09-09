using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PiStation.App.ViewModels;

namespace PiStation.App.Views;

public sealed partial class PiShellDialog : ContentDialog
{
    public PiShellDialog(ShellViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
    public ShellViewModel ViewModel { get; }
    private async void OnRun(object sender, RoutedEventArgs args) => await ViewModel.RunPiShellAsync();
    private async void OnCancel(object sender, RoutedEventArgs args) => await ViewModel.CancelPiShellAsync();
}
