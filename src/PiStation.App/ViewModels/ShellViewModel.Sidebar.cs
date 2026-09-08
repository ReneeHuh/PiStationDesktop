using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    private IEnumerable<ThreadDescriptor> SortSidebarThreads(IEnumerable<ThreadDescriptor> threads) => Layout.Sidebar.ThreadSort == 1
        ? threads.OrderByDescending(thread => thread.IsPinned).ThenBy(thread => thread.PinnedOrder)
            .ThenByDescending(thread => thread.CreatedUtc).ThenBy(thread => thread.ThreadId.Value, StringComparer.Ordinal)
        : ThreadOrdering.Apply(threads);

    public async Task SaveSidebarPreferencesAsync(SidebarPreferences preferences)
    {
        Layout.ConfigureSidebar(preferences);
        await RefreshProjectGroupsAsync().ConfigureAwait(false);
        await QueueThreadListRefreshAsync(false, CancellationToken.None).ConfigureAwait(false);
    }
    public async Task MoveProjectAsync(ProjectGroupViewModel group, int offset)
    {
        var ids = ProjectGroups.Select(item => item.Project.ProjectId.Value).ToList();
        var index = ids.IndexOf(group.Project.ProjectId.Value);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= ids.Count) return;
        (ids[index], ids[target]) = (ids[target], ids[index]);
        await SaveSidebarPreferencesAsync(Layout.Sidebar with { ProjectSort = 3, ProjectOrder = ids });
    }
}
