using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PiStation.App.ViewModels;
using PiStation.ClientRuntime;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using Windows.System;

namespace PiStation.App.Views.Controls;

public sealed partial class AppSidebar : UserControl
{
    private bool _isCollapsed;
    private ThreadId? _renamingThreadId;
    private TextBlock? _renamingTitle;
    private Grid? _renamingEditor;
    private int _renameSessionVersion;

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
        ProjectSelector.SelectedItem = ViewModel.ProjectGroups.FirstOrDefault(group => group.Project.ProjectId == ViewModel.Workspace.SelectedProject?.ProjectId);
        ThreadTabList.SelectedItem = ViewModel.Workspace.SelectedThread;
    }

    private void OnAddProjectClicked(object sender, RoutedEventArgs e) =>
        AddProjectRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettingsClicked(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private async void OnProjectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.IsRefreshingCatalog ||
            (ProjectSelector.SelectedItem as ProjectGroupViewModel)?.Project.ProjectId == ViewModel.Workspace.SelectedProject?.ProjectId) return;
        await ViewModel.SelectProjectAsync((ProjectSelector.SelectedItem as ProjectGroupViewModel)?.Project);
    }

    private async void OnGroupedProjectClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProjectGroupViewModel group }) await ViewModel.SelectProjectAsync(group.Project);
    }

    private async void OnGroupedThreadClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ThreadDescriptor thread) await ViewModel.SelectGroupedThreadAsync(thread);
    }

    private async void OnInboxShelfChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && InboxShelfSelector.SelectedIndex >= 0)
            await ViewModel.SetInboxShelfAsync((ThreadInboxShelf)InboxShelfSelector.SelectedIndex);
    }

    private async void OnThreadSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.IsRefreshingCatalog) return;
        if (ThreadTabList.SelectedItems.Count == 1 && ThreadTabList.SelectedItem is ThreadDescriptor thread)
        {
            if (thread.ThreadId == ViewModel.Workspace.SelectedThread?.ThreadId) return;
            await ViewModel.SelectThreadAsync(thread);
        }
        else if (ThreadTabList.SelectedItems.Count == 0 && ViewModel.Workspace.SelectedThread is null)
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

    private async void OnToggleThreadSettledClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveThread(sender) is { } thread)
        {
            await ViewModel.SetThreadSettledAsync(thread, !thread.IsSettled);
            SynchronizeSelection();
        }
    }

    private async void OnToggleThreadSnoozeClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveThread(sender) is { } thread)
        {
            var until = thread.SnoozedUntilUtc is null ? DateTimeOffset.Now.AddDays(1) : (DateTimeOffset?)null;
            await ViewModel.SetThreadSnoozedAsync(thread, until);
            SynchronizeSelection();
        }
    }

    private async void OnRegenerateThreadTitleClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveThread(sender) is { } thread)
        {
            await ViewModel.RegenerateThreadTitleAsync(thread);
            SynchronizeSelection();
        }
    }

    private async void OnMovePinnedThreadUpClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveThread(sender) is { } thread)
        {
            await ViewModel.MovePinnedThreadAsync(thread, -1);
            SynchronizeSelection();
        }
    }

    private async void OnMovePinnedThreadDownClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveThread(sender) is { } thread)
        {
            await ViewModel.MovePinnedThreadAsync(thread, 1);
            SynchronizeSelection();
        }
    }

    private async void OnDeleteThreadClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveThread(sender) is not { } thread || !await ConfirmDeleteAsync([thread]))
        {
            return;
        }

        await ViewModel.DeleteThreadAsync(thread);
        SynchronizeSelection();
    }

    private async void OnBulkSettleClicked(object sender, RoutedEventArgs e) =>
        await RunBulkOperationAsync(ThreadBulkOperation.Settle);

    private async void OnBulkUnsettleClicked(object sender, RoutedEventArgs e) =>
        await RunBulkOperationAsync(ThreadBulkOperation.Unsettle);

    private async void OnCheckoutProjectClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectDescriptor project }) await ViewModel.SelectProjectAsync(project);
    }
    private void OnShowAllProjectTasks(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectGroupViewModel group }) group.ShowAll();
    }
    private async void OnMoveProjectUp(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectGroupViewModel group }) await ViewModel.MoveProjectAsync(group, -1);
    }
    private async void OnMoveProjectDown(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectGroupViewModel group }) await ViewModel.MoveProjectAsync(group, 1);
    }
    private async void OnLoadMoreThreadsClicked(object sender, RoutedEventArgs e) => await ViewModel.LoadMoreThreadsAsync();

    private async void OnBulkSnoozeClicked(object sender, RoutedEventArgs e) =>
        await RunBulkOperationAsync(ThreadBulkOperation.Snooze, DateTimeOffset.Now.AddDays(1));

    private async void OnBulkUnsnoozeClicked(object sender, RoutedEventArgs e) =>
        await RunBulkOperationAsync(ThreadBulkOperation.Unsnooze);

    private async void OnBulkPinClicked(object sender, RoutedEventArgs e) =>
        await RunBulkOperationAsync(ThreadBulkOperation.Pin);

    private async void OnBulkUnpinClicked(object sender, RoutedEventArgs e) =>
        await RunBulkOperationAsync(ThreadBulkOperation.Unpin);

    private async void OnBulkArchiveClicked(object sender, RoutedEventArgs e) =>
        await RunBulkOperationAsync(ThreadBulkOperation.Archive);

    private async void OnBulkDeleteClicked(object sender, RoutedEventArgs e)
    {
        var threads = SelectedThreads();
        if (threads.Length != 0 && await ConfirmDeleteAsync(threads))
        {
            await ViewModel.ApplyThreadBulkOperationAsync(threads, ThreadBulkOperation.Delete);
            SynchronizeSelection();
        }
    }

    private async Task RunBulkOperationAsync(
        ThreadBulkOperation operation,
        DateTimeOffset? snoozedUntilUtc = null)
    {
        var threads = SelectedThreads();
        if (threads.Length == 0)
        {
            return;
        }

        await ViewModel.ApplyThreadBulkOperationAsync(threads, operation, snoozedUntilUtc);
        SynchronizeSelection();
    }

    private ThreadDescriptor[] SelectedThreads() =>
        ThreadTabList.SelectedItems.OfType<ThreadDescriptor>().ToArray();

    private async void OnMarkUnreadClicked(object sender, RoutedEventArgs e)
    {
        if (ResolveThread(sender) is { } thread) await ViewModel.MarkThreadsUnreadAsync([thread]);
    }

    private async void OnBulkMarkUnreadClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.MarkThreadsUnreadAsync(SelectedThreads());

    private async Task<bool> ConfirmDeleteAsync(ThreadDescriptor[] threads)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = threads.Length == 1 ? $"Delete {threads[0].Title}?" : $"Delete {threads.Length} threads?",
            Content = "Thread history, drafts, attachments, and Pi Station metadata will be removed. Project files are not deleted.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
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

        var settled = new MenuFlyoutItem
        {
            Text = thread.IsSettled ? "Unsettle" : "Settle",
            Tag = thread.ThreadId.Value,
            Icon = new FontIcon { Glyph = "\uE73E" },
        };
        settled.Click += OnToggleThreadSettledClicked;

        var snooze = new MenuFlyoutItem
        {
            Text = thread.SnoozedUntilUtc is null ? "Snooze for one day" : "Unsnooze",
            Tag = thread.ThreadId.Value,
            Icon = new FontIcon { Glyph = "\uE823" },
        };
        snooze.Click += OnToggleThreadSnoozeClicked;

        var regenerate = new MenuFlyoutItem
        {
            Text = "Regenerate title",
            Tag = thread.ThreadId.Value,
            Icon = new FontIcon { Glyph = "\uE72C" },
        };
        regenerate.Click += OnRegenerateThreadTitleClicked;

        var delete = new MenuFlyoutItem
        {
            Text = "Delete",
            Tag = thread.ThreadId.Value,
            Icon = new FontIcon { Glyph = "\uE74D" },
        };
        delete.Click += OnDeleteThreadClicked;

        var flyout = new MenuFlyout();
        var copyReference = new MenuFlyoutItem { Text = "Copy thread reference" };
        copyReference.Click += (_, _) =>
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(thread.PullRequest?.Url ?? thread.ThreadId.Value);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        };
        flyout.Items.Add(copyReference);
        if (thread.WorkspaceMode == ThreadWorkspaceMode.Worktree && thread.ProjectId == ViewModel.Workspace.SelectedProject?.ProjectId)
        {
            var reuse = new MenuFlyoutItem { Text = "New thread in this worktree" };
            reuse.Click += async (_, _) => await ViewModel.CreateThreadInWorkspaceAsync(
                ThreadWorkspaceMode.Worktree, reuseWorktreeFromThreadId: thread.ThreadId);
            flyout.Items.Add(reuse);
        }
        if (thread.PullRequest is not null)
        {
            var unlink = new MenuFlyoutItem { Text = "Unlink pull request" };
            unlink.Click += async (_, _) => await ViewModel.UnlinkPullRequestAsync(thread);
            flyout.Items.Add(unlink);
        }
        var unread = new MenuFlyoutItem { Text = "Mark unread", Tag = thread.ThreadId.Value, IsEnabled = thread.CompletionSequence > 0 };
        AutomationProperties.SetAutomationId(unread, "ContextMarkThreadUnreadMenuItem");
        unread.Click += OnMarkUnreadClicked;
        flyout.Items.Add(unread);
        flyout.Items.Add(rename);
        flyout.Items.Add(pin);
        flyout.Items.Add(settled);
        flyout.Items.Add(snooze);
        flyout.Items.Add(regenerate);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(archive);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(delete);
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
        _renameSessionVersion++;
        _renamingTitle = title;
        _renamingEditor = editor;
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

        HideRenameVisuals(_renamingTitle, _renamingEditor);

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
        _renamingTitle = null;
        _renamingEditor = null;
    }

    private static void HideRenameVisuals(TextBlock? title, Grid? editor)
    {
        if (title is not null)
        {
            title.Visibility = Visibility.Visible;
        }

        if (editor is not null)
        {
            editor.Visibility = Visibility.Collapsed;
        }
    }

    private void HideThreadRenameEditor(ThreadId threadId)
    {
        var thread = ViewModel.FindThread(threadId.Value);
        if (thread is null)
        {
            return;
        }

        HideRenameVisuals(
            FindThreadRowElement<TextBlock>(thread, "ThreadTitleText"),
            FindThreadRowElement<Grid>(thread, "ThreadRenameEditor"));
    }

    private async Task CommitThreadRenameAsync(ThreadDescriptor thread, TextBox input)
    {
        var sessionVersion = _renameSessionVersion;
        if (await ViewModel.RenameThreadAsync(thread, input.Text))
        {
            if (_renamingThreadId == thread.ThreadId && _renameSessionVersion == sessionVersion)
            {
                CancelThreadRename();
                // A successful rename can refresh and recycle the ListView container after
                // the original editor was hidden. Hide the newly materialized template too,
                // but only while this rename session is still current.
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_renameSessionVersion == sessionVersion)
                    {
                        HideThreadRenameEditor(thread.ThreadId);
                    }
                });
            }
            ThreadTabList.SelectedItem = ViewModel.Workspace.SelectedThread;
        }
        else if (_renamingThreadId == thread.ThreadId && _renameSessionVersion == sessionVersion)
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
