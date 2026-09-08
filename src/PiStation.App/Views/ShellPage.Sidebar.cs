using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private async void OnConfigureSidebar(object sender, RoutedEventArgs e)
    {
        var saved = ViewModel.Layout.Sidebar;
        var grouping = new CheckBox { Content = "Group checkouts of the same repository", IsChecked = saved.GroupByRepository };
        var projectSort = new ComboBox { Header = "Project order", ItemsSource = new[] { "Name", "Last updated", "Created", "Manual" }, SelectedIndex = saved.ProjectSort };
        var threadSort = new ComboBox { Header = "Thread order", ItemsSource = new[] { "Last updated", "Created" }, SelectedIndex = saved.ThreadSort };
        var previewCount = new NumberBox { Header = "Thread previews per project", Minimum = 1, Maximum = 15, Value = saved.PreviewCount };
        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        foreach (var element in new UIElement[] { grouping, projectSort, threadSort, previewCount }) panel.Children.Add(element);
        SettingsDialog.Hide();
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Sidebar", Content = panel, PrimaryButtonText = "Save", CloseButtonText = "Cancel" };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.SaveSidebarPreferencesAsync(saved with { GroupByRepository = grouping.IsChecked == true,
                ProjectSort = projectSort.SelectedIndex, ThreadSort = threadSort.SelectedIndex, PreviewCount = double.IsFinite(previewCount.Value) ? (int)previewCount.Value : 6 });
        await OpenSettingsAsync();
    }
}
