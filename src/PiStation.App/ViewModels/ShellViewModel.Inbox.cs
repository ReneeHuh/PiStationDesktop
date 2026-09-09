using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using PiStation.ClientRuntime;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    private DispatcherQueueTimer? _inboxTimer;
    private ThreadInboxShelf _inboxShelf;
    private readonly SemaphoreSlim _projectGroupRefreshGate = new(1, 1);
    public ObservableCollection<ProjectGroupViewModel> ProjectGroups { get; } = [];
    public ThreadInboxShelf InboxShelf
    {
        get => _inboxShelf;
        private set => SetProperty(ref _inboxShelf, value);
    }

    private void InitializeInbox()
    {
        _inboxTimer = _dispatcherQueue.CreateTimer();
        _inboxTimer.Interval = TimeSpan.FromSeconds(30);
        _inboxTimer.Tick += OnInboxTimer;
        _inboxTimer.Start();
    }

    private void OnThreadMetadataChanged(object? sender, ThreadMetadataChangedEventArgs args)
    {
        if (sender is not ThreadMetadataStore store || !ReferenceEquals(_client?.ThreadMetadata, store))
        {
            return;
        }

        // Metadata events arrive from the runtime thread. Apply the complete project
        // snapshot on the UI thread so sidebar counts and rows change immediately,
        // without using the current thread search as a filter.
        RunOnUiThread(() =>
        {
            if (!ReferenceEquals(_client?.ThreadMetadata, store))
            {
                return;
            }

            var group = ProjectGroups.FirstOrDefault(item => item.Members.Any(project => project.ProjectId == args.ProjectId));
            group?.Apply(group.Members.SelectMany(project => store.GetProjectThreads(project.ProjectId, includeArchived: true)).ToArray(), InboxShelf, Layout.Sidebar);
            if (store.GetCurrent(args.ThreadId) is { } readMetadata) ApplyReadMetadata(readMetadata);

            if (store.GetCurrent(args.ThreadId) is { } updated && SelectedThread?.ThreadId == args.ThreadId)
            {
                SelectedThread = updated;
            }
            else if (args.Thread is null && SelectedThread?.ThreadId == args.ThreadId)
            {
                SelectedThread = null;
            }
        });
    }

    private async void OnInboxTimer(DispatcherQueueTimer sender, object args)
    {
        if (_client is null) return;
        await RefreshProjectGroupsAsync().ConfigureAwait(false);
        await QueueThreadListRefreshAsync(false, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task SetInboxShelfAsync(ThreadInboxShelf shelf)
    {
        InboxShelf = shelf;
        RunOnUiThread(() =>
        {
            foreach (var group in ProjectGroups)
            {
                group.Apply(group.AllThreads, InboxShelf, Layout.Sidebar);
            }
        });
        await SetShowingArchivedThreadsAsync(shelf == ThreadInboxShelf.Archived).ConfigureAwait(false);
        await RefreshProjectGroupsAsync().ConfigureAwait(false);
        await QueueThreadListRefreshAsync(false, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RefreshProjectGroupsAsync()
    {
        if (_client is null || !await _projectGroupRefreshGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var client = _client;
            if (client is null)
            {
                return;
            }

            var projects = await client.ListProjectsAsync().ConfigureAwait(false);
            var allThreads = new List<ThreadDescriptor>();
            foreach (var project in projects)
            {
                int? offset = 0;
                while (offset is { } pageOffset)
                {
                    var page = await client.SearchThreadsAsync(new(project.ProjectId, string.Empty, true, ThreadLifecycleDefaults.MaximumSearchLimit, pageOffset)).ConfigureAwait(false);
                    allThreads.AddRange(page.Threads); offset = page.NextOffset;
                }
            }
            await RunOnUiThreadAsync(() =>
            {
                if (!ReferenceEquals(_client, client))
                {
                    return;
                }

                ApplyProjectGroups(projects, allThreads);
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RunOnUiThread(() => ThreadListStatus = $"Project inbox could not refresh: {exception.Message}");
        }
        finally
        {
            _projectGroupRefreshGate.Release();
        }
    }

    private void ApplyProjectGroups(IReadOnlyList<ProjectDescriptor> projects, IReadOnlyList<ThreadDescriptor> allThreads)
    {
        var preferences = Layout.Sidebar;
        var groups = new List<ProjectGroupViewModel>();
        foreach (var members in projects.GroupBy(project => preferences.GroupByRepository ? project.RepositoryKey ?? project.ProjectId.Value : project.ProjectId.Value, StringComparer.OrdinalIgnoreCase))
        {
            var group = ProjectGroups.FirstOrDefault(item => item.GroupKey == members.Key) ?? new ProjectGroupViewModel(members.First()) { GroupKey = members.Key };
            group.SetMembers(members.ToArray());
            if (IsRemote && _client is { } iconClient) _ = group.LoadRemoteIconAsync(iconClient);
            group.Apply(allThreads.Where(thread => members.Any(project => project.ProjectId == thread.ProjectId)).DistinctBy(thread => thread.ThreadId).ToArray(), InboxShelf, preferences);
            groups.Add(group);
        }
        IEnumerable<ProjectGroupViewModel> ordered = preferences.ProjectSort switch
        {
            1 => groups.OrderByDescending(group => group.AllThreads.Select(thread => thread.UpdatedUtc).DefaultIfEmpty(group.Project.CreatedUtc).Max()),
            2 => groups.OrderByDescending(group => group.Project.CreatedUtc),
            3 => groups.OrderBy(group => preferences.ProjectOrder?.ToList().IndexOf(group.Project.ProjectId.Value) is >= 0 and var rank ? rank : int.MaxValue),
            _ => groups.OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase),
        };
        UpdateCatalogCollection(ProjectGroups, ordered.ToArray(), group => group.GroupKey);
    }

    public async Task SelectGroupedThreadAsync(ThreadDescriptor thread)
    {
        var project = Workspace.Projects.FirstOrDefault(item => item.ProjectId == thread.ProjectId);
        if (project is null) return;
        if (SelectedProject?.ProjectId != project.ProjectId) await SelectProjectAsync(project).ConfigureAwait(false);
        await SelectThreadAsync(thread).ConfigureAwait(false);
    }
}
