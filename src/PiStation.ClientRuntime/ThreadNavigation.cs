using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

/// <summary>Projects a single inbox across project catalogs and the live workspace.</summary>
public static class ThreadNavigation
{
    public static IReadOnlyList<ThreadDescriptor> Select(
        IEnumerable<ThreadDescriptor> catalog,
        IEnumerable<ThreadDescriptor> live,
        IReadOnlySet<ProjectId>? projects,
        string query,
        ThreadInboxShelf shelf,
        bool sortByCreated,
        DateTimeOffset now)
    {
        // Apply live updates before filtering: archiving or renaming must remove stale catalog matches.
        var latest = new Dictionary<ThreadId, ThreadDescriptor>();
        foreach (var thread in catalog.Concat(live))
            if (!latest.TryGetValue(thread.ThreadId, out var previous) || thread.Revision >= previous.Revision)
                latest[thread.ThreadId] = thread;
        query = query.Trim();
        var matches = latest.Values.Where(thread =>
            (projects is null || projects.Contains(thread.ProjectId)) &&
            (query.Length == 0 || thread.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             (thread.BranchName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)));
        var visible = ThreadInbox.Select(matches, shelf, now);
        return sortByCreated
            ? visible.OrderByDescending(thread => thread.IsPinned).ThenBy(thread => thread.PinnedOrder)
                .ThenByDescending(thread => thread.CreatedUtc).ToArray()
            : visible;
    }
}
