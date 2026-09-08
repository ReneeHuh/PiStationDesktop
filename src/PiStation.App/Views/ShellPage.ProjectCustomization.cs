using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PiStation.Protocol.Models;
using Windows.Storage.Pickers;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private async void OnCustomizeProjectClicked(object sender, RoutedEventArgs e)
    {
        var project = ViewModel.Workspace.SelectedProject;
        if (project is null) return;
        var scripts = (project.Scripts ?? []).ToList();
        var icon = new TextBox { Header = "Icon: emoji:🚀 or image path (empty uses repository icon)", Text = project.Icon ?? "" };
        var browse = new Button { Content = "Choose image…" };
        browse.Click += async (_, _) =>
        {
            if ((Application.Current as App)?.MainWindow is not { } window) return;
            var picker = new FileOpenPicker();
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".ico", ".webp", ".gif" }) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            if (await picker.PickSingleFileAsync() is { } file) icon.Text = file.Path;
        };
        var list = new ComboBox { Header = "Scripts", DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = new TextBox { Header = "Name", MaxLength = 200 };
        var command = new TextBox { Header = "Command", AcceptsReturn = true, MaxLength = 32768, MaxHeight = 150, TextWrapping = TextWrapping.Wrap };
        var scriptIcon = new ComboBox { Header = "Script icon", ItemsSource = Enum.GetValues<ProjectScriptIcon>(), SelectedIndex = 0 };
        var setup = new CheckBox { Content = "Run when creating a worktree" };
        ProjectScript? selected = null;
        void Refresh() { var target = selected; list.ItemsSource = scripts.ToArray(); list.SelectedItem = target; }
        list.SelectionChanged += (_, _) =>
        {
            selected = list.SelectedItem as ProjectScript;
            name.Text = selected?.Name ?? ""; command.Text = selected?.Command ?? "";
            scriptIcon.SelectedItem = selected?.Icon ?? ProjectScriptIcon.Play;
            setup.IsChecked = selected?.RunOnWorktreeCreate ?? false;
        };
        var add = new Button { Content = "Add / update script" };
        var remove = new Button { Content = "Delete selected script" };
        var newScript = new Button { Content = "New script" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        bool ApplyScript()
        {
            if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(command.Text)) { status.Text = "Enter a name and command."; return false; }
            var updated = new ProjectScript(selected?.Id ?? Guid.NewGuid().ToString("N"), name.Text.Trim(), command.Text.Trim(),
                (ProjectScriptIcon)scriptIcon.SelectedItem, setup.IsChecked == true);
            if (selected is null) scripts.Add(updated); else scripts[scripts.IndexOf(selected)] = updated;
            selected = updated; Refresh(); status.Text = "Script added to the changes below. Save to apply.";
            return true;
        }
        add.Click += (_, _) => ApplyScript();
        remove.Click += (_, _) => { if (selected is not null) scripts.Remove(selected); selected = null; Refresh(); };
        newScript.Click += (_, _) => { list.SelectedItem = null; name.Text = command.Text = ""; };
        var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
        foreach (var element in new UIElement[] { icon, browse, list, name, command, scriptIcon, setup, add, newScript, remove, status }) panel.Children.Add(element);
        Refresh();
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Customize " + project.DisplayName,
            Content = new ScrollViewer { Content = panel, MaxHeight = 560 }, PrimaryButtonText = "Save", CloseButtonText = "Cancel" };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (selected is not null || !string.IsNullOrWhiteSpace(name.Text) || !string.IsNullOrWhiteSpace(command.Text))
                args.Cancel = !ApplyScript();
        };
        SettingsDialog.Hide();
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.UpdateProjectCustomizationAsync(project, scripts, string.IsNullOrWhiteSpace(icon.Text) ? null : icon.Text.Trim());
        await OpenSettingsAsync();
    }
}
