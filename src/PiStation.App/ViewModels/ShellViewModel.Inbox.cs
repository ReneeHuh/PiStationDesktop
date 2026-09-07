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

            var group = ProjectGroups.FirstOrDefault(item => item.Project.ProjectId == args.ProjectId);
            group?.Apply(store.GetProjectThreads(args.ProjectId, includeArchived: true), InboxShelf);
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
                group.Apply(group.AllThreads, InboxShelf);
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
            foreach (var project in projects)
            {
                await client.SearchThreadsAsync(new SearchThreadsRequest(project.ProjectId, string.Empty,
                    IncludeArchived: true, Limit: ThreadLifecycleDefaults.MaximumSearchLimit)).ConfigureAwait(false);
                await RunOnUiThreadAsync(() =>
                {
                    if (!ReferenceEquals(_client, client))
                    {
                        return;
                    }

                    var group = ProjectGroups.FirstOrDefault(item => item.Project.ProjectId == project.ProjectId);
                    if (group is null) { group = new ProjectGroupViewModel(project); ProjectGroups.Add(group); }
                    group.Apply(client.ThreadMetadata.GetProjectThreads(project.ProjectId, includeArchived: true), InboxShelf);
                }).ConfigureAwait(false);
            }
            await RunOnUiThreadAsync(() =>
            {
                if (!ReferenceEquals(_client, client))
                {
                    return;
                }

                foreach (var removed in ProjectGroups.Where(group => !projects.Any(project => project.ProjectId == group.Project.ProjectId)).ToArray())
                    ProjectGroups.Remove(removed);
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

    public async Task SelectGroupedThreadAsync(ThreadDescriptor thread)
    {
        var project = Workspace.Projects.FirstOrDefault(item => item.ProjectId == thread.ProjectId);
        if (project is null) return;
        if (SelectedProject?.ProjectId != project.ProjectId) await SelectProjectAsync(project).ConfigureAwait(false);
        await SelectThreadAsync(thread).ConfigureAwait(false);
    }
}
