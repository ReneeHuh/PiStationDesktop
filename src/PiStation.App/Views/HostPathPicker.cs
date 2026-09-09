using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using PiStation.Protocol.Models;

namespace PiStation.App.Views;

internal static class HostPathPicker
{
    public static async Task<string?> PickAsync(XamlRoot root, string title,
        Func<BrowseHostPathRequest, CancellationToken, Task<HostPathPage>> browse, string? initialPath = null,
        bool allowDirectories = true, Func<ContentDialog, Task<ContentDialogResult>>? showDialog = null)
    {
        using var lifetime = new CancellationTokenSource();
        var path = new TextBox { Header = "Folder on the host (empty lists drives)", Text = initialPath ?? "" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var entries = new ListView { DisplayMemberPath = "DisplayName", SelectionMode = ListViewSelectionMode.Single, Height = 300 };
        var open = new Button { Content = "Open folder" };
        var up = new Button { Content = "Up" };
        var more = new Button { Content = "Load more", IsEnabled = false };
        AutomationProperties.SetAutomationId(path, "HostFolderInput");
        AutomationProperties.SetAutomationId(status, "HostBrowserStatus");
        AutomationProperties.SetAutomationId(entries, "HostPathEntries");
        AutomationProperties.SetAutomationId(open, "OpenHostFolderButton");
        AutomationProperties.SetAutomationId(up, "HostFolderUpButton");
        AutomationProperties.SetAutomationId(more, "LoadMoreHostEntriesButton");
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        controls.Children.Add(open); controls.Children.Add(up); controls.Children.Add(more);
        var panel = new StackPanel { Spacing = 8, MinWidth = 440 };
        panel.Children.Add(path); panel.Children.Add(controls); panel.Children.Add(entries); panel.Children.Add(status);
        var dialog = new ContentDialog { XamlRoot = root, Title = title, Content = panel,
            PrimaryButtonText = "Select", CloseButtonText = "Cancel", IsPrimaryButtonEnabled = false };
        AutomationProperties.SetAutomationId(dialog, "HostPathPickerDialog");
        HostPathPage? page = null;
        var busy = false;
        string? selectedPath = null;
        bool IsLoadedPath() => page is not null && string.Equals(path.Text.Trim(), page.Path ?? "", StringComparison.OrdinalIgnoreCase);
        void UpdateSelection()
        {
            dialog.IsPrimaryButtonEnabled = !busy && IsLoadedPath() &&
                (entries.SelectedItem is HostPathEntry selected ? allowDirectories || !selected.IsDirectory : allowDirectories && page?.Path is not null);
            more.IsEnabled = !busy && IsLoadedPath() && page?.NextOffset is not null;
        }
        async Task LoadAsync(string? folder, int offset = 0)
        {
            if (busy) return;
            busy = true; open.IsEnabled = up.IsEnabled = more.IsEnabled = false; UpdateSelection();
            status.Text = "Reading host folder…";
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var result = await browse(new(folder, offset), timeout.Token);
                if (lifetime.IsCancellationRequested) return;
                var previous = offset > 0 ? (entries.ItemsSource as IEnumerable<HostPathEntry>) ?? [] : [];
                entries.ItemsSource = previous.Concat(result.Entries).OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                entries.SelectedItem = null;
                page = result; path.Text = result.Path ?? "";
                status.Text = "Select a file" + (allowDirectories ? " or folder" : "") + ". Double-click a folder or press Enter to open it.";
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                page = null; entries.ItemsSource = null;
                status.Text = error is OperationCanceledException ? "Host browsing was canceled or timed out." : error.Message;
            }
            finally
            {
                busy = false; open.IsEnabled = up.IsEnabled = true;
                more.IsEnabled = page?.NextOffset is not null; UpdateSelection();
            }
        }
        open.Click += async (_, _) => await LoadAsync(path.Text);
        up.Click += async (_, _) => await LoadAsync(page?.ParentPath);
        more.Click += async (_, _) => { if (page?.NextOffset is { } offset) await LoadAsync(page.Path, offset); };
        path.KeyDown += async (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; await LoadAsync(path.Text); } };
        path.TextChanged += (_, _) => UpdateSelection();
        entries.SelectionChanged += (_, _) => UpdateSelection();
        entries.DoubleTapped += async (_, _) => { if (IsLoadedPath() && entries.SelectedItem is HostPathEntry { IsDirectory: true } entry) await LoadAsync(entry.Path); };
        entries.KeyDown += async (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter && IsLoadedPath() && entries.SelectedItem is HostPathEntry { IsDirectory: true } entry)
            {
                args.Handled = true;
                await LoadAsync(entry.Path);
            }
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            selectedPath = (entries.SelectedItem as HostPathEntry)?.Path ?? (allowDirectories ? page?.Path : null);
            args.Cancel = selectedPath is null || busy || !IsLoadedPath() ||
                (!allowDirectories && entries.SelectedItem is not HostPathEntry { IsDirectory: false });
        };
        dialog.Opened += async (_, _) => await LoadAsync(initialPath);
        var result = await (showDialog?.Invoke(dialog) ?? dialog.ShowAsync().AsTask());
        await lifetime.CancelAsync();
        return result == ContentDialogResult.Primary ? selectedPath : null;
    }
}
