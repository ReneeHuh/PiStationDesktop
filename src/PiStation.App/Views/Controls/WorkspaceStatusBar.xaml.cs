using Microsoft.UI.Xaml.Controls;
using PiStation.App.ViewModels;

namespace PiStation.App.Views.Controls;

public sealed partial class WorkspaceStatusBar : UserControl
{
    public WorkspaceStatusBar(ShellViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public ShellViewModel ViewModel { get; }
}
