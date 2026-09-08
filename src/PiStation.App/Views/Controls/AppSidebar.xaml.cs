using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PiStation.App.ViewModels;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using Windows.System;

namespace PiStation.App.Views.Controls;

public sealed partial class AppSidebar : UserControl
{
    private bool _isCollapsed;
    private ThreadId? _renamingThreadId;

    public AppSidebar(ShellViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _isCollapsed = ViewModel.Layout.IsSidebarCollapsed;
        InitializeComponent();
        VisualStateManager.GoToState(this, _isCollapsed ? nameof(Collapsed) : nameof(Expanded), false);
        ViewModel.Layout.PropertyChanged += OnLayoutPropertyChanged;
    }

    public event EventHandler? AddProjectRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? CollapsedChanged;

    public ShellViewModel ViewModel { get; }

    public bool IsCollapsed => _isCollapsed;

    public void ToggleCollapsed() => ViewModel.Layout.IsSidebarCollapsed = !_isCollapsed;

    public void FocusNewProjectButton() =>
        (_isCollapsed ? CollapsedNewProjectButton : NewProjectButton).Focus(FocusState.Programmatic);

    public void FocusSettingsButton() =>
        (_isCollapsed ? CollapsedSettingsButton : SettingsButton).Focus(FocusState.Programmatic);

    public void SynchronizeSelection()
    {
        ProjectSelector.SelectedItem = ViewModel.Workspace.SelectedProject;
        ThreadTabList.SelectedItem = ViewModel.Workspace.SelectedThread;
    }

    private void OnAddProjectClicked(object sender, RoutedEventArgs e) =>
        AddProjectRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettingsClicked(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private async void OnProjectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.IsRefreshingCatalog ||
            (ProjectSelector.SelectedItem as ProjectDescriptor)?.ProjectId == ViewModel.Workspace.SelectedProject?.ProjectId) return;
        await ViewModel.SelectProjectAsync(ProjectSelector.SelectedItem as ProjectDescriptor);
    }

    private async void OnThreadSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.IsRefreshingCatalog) return;
        if (ThreadTabList.SelectedItem is ThreadDescriptor thread)
        {
            if (thread.ThreadId == ViewModel.Workspace.SelectedThread?.ThreadId) return;
            await ViewModel.SelectThreadAsync(thread);
        }
        else if (ViewModel.Workspace.SelectedThread is null)
        {
            await ViewModel.SelectThreadAsync(null);
        }
    }

    private async void OnNewThreadClicked(object sender, RoutedEventArgs e)
    {
        var project = ViewModel.Workspace.SelectedProject;
        var setupScript = project?.Scripts?.FirstOrDefault(static candidate => candidate.RunOnWorktreeCreate);
        if (project?.DefaultWorkspaceMode == ThreadWorkspaceMode.Worktree &&
            setupScript is not null &&
            !project.AreRepositoryScriptsTrusted)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Trust this repository setup script?",
                Content = $"{setupScript.Name}\n\n{setupScript.Command}\n\nThe command comes from t3.json and will run in the new worktree.",
                PrimaryButtonText = "Trust and continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            try
            {
                await ViewModel.TrustSelectedProjectScriptsAsync();
            }
            catch (Exception exception)
            {
                ViewModel.ReportRuntimeError($"Could not save script trust: {exception.Message}");
                return;
            }
        }

        await ViewModel.CreateThreadAsync();
        ThreadTabList.SelectedItem = ViewModel.Workspace.SelectedThread;
    }

    private void OnThreadSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!string.Equals(ThreadSearchInput.Text, ViewModel.Workspace.ThreadSearchQuery, StringComparison.Ordinal))
        {
            ViewModel.UpdateThreadSearchQuery(ThreadSearchInput.Text);
        }
    }

    private void OnClearThreadSearchClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearThreadSearch();
        ThreadSearchInput.Focus(FocusState.Programmatic);
    }

    private async void OnArchivedThreadsToggleClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.SetShowingArchivedThreadsAsync(ArchivedThreadsToggle.IsChecked == true);
    }

    private void OnToggleSidebarClicked(object sender, RoutedEventArgs e)
    {
        ToggleCollapsed();
    }

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellLayoutViewModel.IsSidebarCollapsed) ||
            _isCollapsed == ViewModel.Layout.IsSidebarCollapsed)
        {
            return;
        }

        _isCollapsed = ViewModel.Layout.IsSidebarCollapsed;
        VisualStateManager.GoToState(this, _isCollapsed ? nameof(Collapsed) : nameof(Expanded), true);
        CollapsedChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRenameThreadClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveThread(sender) is { } thread)
        {
            BeginThreadRename(thread);
        }
    }

    private async void OnToggleThreadPinClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveThread(sender) is not { } thread)
        {
            return;
        }

        await ViewModel.SetThreadPinnedAsync(thread, !thread.IsPinned);
        ThreadTabList.SelectedItem = ViewModel.Workspace.SelectedThread;
    }

    private async void OnToggleThreadArchiveClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveThread(sender) is not { } thread)
        {
            return;
        }

        CancelThreadRename();
        await ViewModel.SetThreadArchivedAsync(thread, !thread.IsArchived);
        ThreadTabList.SelectedItem = ViewModel.Workspace.SelectedThread;
    }

    private void OnThreadRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement row || ResolveThread(sender) is not { } thread)
        {
            return;
        }

        var rename = new MenuFlyoutItem
        {
            Text = "Rename",
            Tag = thread.ThreadId.Value,
            Icon = new FontIcon { Glyph = "\uE70F" },
        };
        AutomationProperties.SetAutomationId(rename, "ContextRenameThreadMenuItem");
        AutomationProperties.SetName(rename, "Rename thread");
        rename.Click += OnRenameThreadClicked;

        var pin = new MenuFlyoutItem
        {
            Text = thread.IsPinned ? "Unpin" : "Pin",
            Tag = thread.ThreadId.Value,
            Icon = new FontIcon { Glyph = "\uE718" },
        };
        AutomationProperties.SetAutomationId(pin, "ContextToggleThreadPinMenuItem");
        AutomationProperties.SetName(pin, "Change thread pin");
        pin.Click += OnToggleThreadPinClicked;

        var archive = new MenuFlyoutItem
        {
            Text = thread.IsArchived ? "Restore" : "Archive",
            Tag = thread.ThreadId.Value,
            Icon = new FontIcon { Glyph = "\uE7B8" },
        };
        AutomationProperties.SetAutomationId(archive, "ContextToggleThreadArchiveMenuItem");
        AutomationProperties.SetName(archive, "Change thread archive state");
        archive.Click += OnToggleThreadArchiveClicked;

        var flyout = new MenuFlyout();
        flyout.Items.Add(rename);
        flyout.Items.Add(pin);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(archive);
        flyout.ShowAt(row);
        e.Handled = true;
    }

    private async void OnCommitThreadRenameClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveThread(sender) is { } thread &&
            FindThreadRowElement<TextBox>(thread, "ThreadRenameInput") is { } input)
        {
            await CommitThreadRenameAsync(thread, input);
        }
    }

    private void OnCancelThreadRenameClicked(object sender, RoutedEventArgs e) => CancelThreadRename();

    private async void OnThreadRenameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CancelThreadRename();
            return;
        }

        if (e.Key == VirtualKey.Enter && sender is TextBox input && ResolveThread(sender) is { } thread)
        {
            e.Handled = true;
            await CommitThreadRenameAsync(thread, input);
        }
    }

    private ThreadDescriptor? ResolveThread(object sender)
    {
        if (sender is FrameworkElement { DataContext: ThreadDescriptor thread })
        {
            return thread;
        }

        return sender is FrameworkElement { Tag: string threadId }
            ? ViewModel.FindThread(threadId)
            : null;
    }

    private void BeginThreadRename(ThreadDescriptor thread)
    {
        CancelThreadRename();
        var title = FindThreadRowElement<TextBlock>(thread, "ThreadTitleText");
        var editor = FindThreadRowElement<Grid>(thread, "ThreadRenameEditor");
        var input = FindThreadRowElement<TextBox>(thread, "ThreadRenameInput");
        if (title is null || editor is null || input is null)
        {
            return;
        }

        _renamingThreadId = thread.ThreadId;
        title.Visibility = Visibility.Collapsed;
        editor.Visibility = Visibility.Visible;
        input.Text = thread.Title;
        input.SelectAll();
        input.Focus(FocusState.Programmatic);
    }

    private void CancelThreadRename()
    {
        if (_renamingThreadId is not { } threadId)
        {
            return;
        }

        var thread = ViewModel.FindThread(threadId.Value);
        if (thread is not null)
        {
            if (FindThreadRowElement<TextBlock>(thread, "ThreadTitleText") is { } title)
            {
                title.Visibility = Visibility.Visible;
            }

            if (FindThreadRowElement<Grid>(thread, "ThreadRenameEditor") is { } editor)
            {
                editor.Visibility = Visibility.Collapsed;
            }
        }

        _renamingThreadId = null;
    }

    private async Task CommitThreadRenameAsync(ThreadDescriptor thread, TextBox input)
    {
        if (await ViewModel.RenameThreadAsync(thread, input.Text))
        {
            _renamingThreadId = null;
            ThreadTabList.SelectedItem = ViewModel.Workspace.SelectedThread;
        }
        else
        {
            input.SelectAll();
            input.Focus(FocusState.Programmatic);
        }
    }

    private T? FindThreadRowElement<T>(ThreadDescriptor thread, string name)
        where T : FrameworkElement
    {
        var visibleThread = ViewModel.Workspace.Threads.FirstOrDefault(item => item.ThreadId == thread.ThreadId);
        if (visibleThread is null ||
            ThreadTabList.ContainerFromItem(visibleThread) is not ListViewItem container ||
            container.ContentTemplateRoot is not FrameworkElement templateRoot)
        {
            return null;
        }

        return templateRoot.FindName(name) as T;
    }
}
