using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PiStation.App.ViewModels;
using PiStation.ClientRuntime;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.App.Views.Controls;

public sealed partial class AppSidebar
{
    private ObservableCollection<ThreadDescriptor> NavigationThreads { get; } = [];
    private readonly HashSet<ProjectGroupViewModel> _navigationGroups = [];
    private bool _allProjects = true;
    private ProjectId? _projectFilterId;
    private bool _refreshingNavigation;
    private bool _navigationRefreshQueued;
    private bool _navigationReleased;

    private void InitializeNavigation()
    {
        ThreadTabList.ItemsSource = NavigationThreads;
        ViewModel.ProjectGroups.CollectionChanged += OnNavigationCatalogChanged;
        ViewModel.Workspace.Threads.CollectionChanged += OnNavigationCatalogChanged;
        ViewModel.Workspace.PropertyChanged += OnNavigationPropertyChanged;
        ViewModel.PropertyChanged += OnNavigationPropertyChanged;
        ObserveNavigationGroups();
        QueueNavigationRefresh();
    }

    private void ReleaseNavigation()
    {
        _navigationReleased = true;
        ViewModel.ProjectGroups.CollectionChanged -= OnNavigationCatalogChanged;
        ViewModel.Workspace.Threads.CollectionChanged -= OnNavigationCatalogChanged;
        ViewModel.Workspace.PropertyChanged -= OnNavigationPropertyChanged;
        ViewModel.PropertyChanged -= OnNavigationPropertyChanged;
        foreach (var group in _navigationGroups) group.PropertyChanged -= OnNavigationPropertyChanged;
        _navigationGroups.Clear();
    }

    private void ObserveNavigationGroups()
    {
        foreach (var group in _navigationGroups.Where(group => !ViewModel.ProjectGroups.Contains(group)).ToArray())
        { group.PropertyChanged -= OnNavigationPropertyChanged; _navigationGroups.Remove(group); }
        foreach (var group in ViewModel.ProjectGroups)
            if (_navigationGroups.Add(group)) group.PropertyChanged += OnNavigationPropertyChanged;
    }

    private void OnNavigationCatalogChanged(object? sender, NotifyCollectionChangedEventArgs args)
    { ObserveNavigationGroups(); QueueNavigationRefresh(); }

    private void OnNavigationPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is "Summary" or "SelectedProject" or "SelectedThread" or "ThreadSearchQuery" or "InboxShelf" or "IsRefreshingCatalog") QueueNavigationRefresh();
    }

    private void QueueNavigationRefresh()
    {
        if (_navigationReleased || _navigationRefreshQueued) return;
        _navigationRefreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _navigationRefreshQueued = false;
            if (_navigationReleased || ViewModel.IsRefreshingCatalog) return;
            var filter = ViewModel.ProjectGroups.FirstOrDefault(group => group.Members.Any(project => project.ProjectId == _projectFilterId));
            if (!_allProjects && filter is null) _allProjects = true;
            ProjectFilterLabel.Text = _allProjects ? "All projects" : filter!.DisplayName;
            var ordered = ThreadNavigation.Select(
                ViewModel.ProjectGroups.SelectMany(group => group.AllThreads), ViewModel.Workspace.Threads,
                _allProjects ? null : filter!.Members.Select(project => project.ProjectId).ToHashSet(),
                ViewModel.Workspace.ThreadSearchQuery, ViewModel.InboxShelf,
                ViewModel.Layout.Sidebar.ThreadSort == 1, DateTimeOffset.UtcNow);
            ThreadListStatusText.Visibility = ordered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ThreadListStatusText.Text = ViewModel.Workspace.ThreadSearchQuery.Length != 0
                ? "No matching threads"
                : ViewModel.Workspace.Projects.Count == 0 ? "Add a project to start" : $"No {ViewModel.InboxShelf.ToString().ToLowerInvariant()} threads";
            _refreshingNavigation = true;
            try
            {
                var selectedIds = ThreadTabList.SelectedItems.OfType<ThreadDescriptor>().Select(thread => thread.ThreadId).ToHashSet();
                // Reconcile by identity so streaming metadata never clears the entire list or its scroll anchor.
                for (var index = 0; index < ordered.Count; index++)
                {
                    var existing = NavigationThreads.Select((thread, position) => (thread, position)).FirstOrDefault(item => item.thread.ThreadId == ordered[index].ThreadId);
                    if (existing.thread is null) NavigationThreads.Insert(index, ordered[index]);
                    else { if (existing.position != index) NavigationThreads.Move(existing.position, index); if (NavigationThreads[index] != ordered[index]) NavigationThreads[index] = ordered[index]; }
                }
                while (NavigationThreads.Count > ordered.Count) NavigationThreads.RemoveAt(NavigationThreads.Count - 1);
                if (selectedIds.Count > 1)
                {
                    foreach (var thread in NavigationThreads.Where(thread => selectedIds.Contains(thread.ThreadId)))
                        if (!ThreadTabList.SelectedItems.Contains(thread)) ThreadTabList.SelectedItems.Add(thread);
                }
                else ThreadTabList.SelectedItem = NavigationThreads.FirstOrDefault(thread => thread.ThreadId == ViewModel.Workspace.SelectedThread?.ThreadId);
            }
            finally { _refreshingNavigation = false; }
        });
    }

    private void OnAllProjectsClicked(object sender, RoutedEventArgs args)
    { _allProjects = true; ProjectSelector.SelectedItem = null; ProjectFilterFlyout.Hide(); QueueNavigationRefresh(); }

    private void OnThreadProjectLabelLoaded(object sender, RoutedEventArgs args) => UpdateThreadProjectLabel(sender);
    private void OnThreadProjectLabelChanged(FrameworkElement sender, DataContextChangedEventArgs args) => UpdateThreadProjectLabel(sender);
    private void UpdateThreadProjectLabel(object sender)
    {
        if (sender is TextBlock { DataContext: ThreadDescriptor thread } label)
            label.Text = ViewModel.Workspace.Projects.FirstOrDefault(project => project.ProjectId == thread.ProjectId)?.DisplayName ?? "Project";
    }
}
