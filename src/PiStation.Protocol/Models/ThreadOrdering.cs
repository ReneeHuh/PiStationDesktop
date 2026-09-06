namespace PiStation.Protocol.Models;

public static class ThreadOrdering
{
    public static IOrderedEnumerable<ThreadDescriptor> Apply(IEnumerable<ThreadDescriptor> threads) => threads
        .OrderBy(static thread => thread.IsArchived)
        .ThenByDescending(static thread => thread.IsPinned)
        .ThenBy(static thread => thread.IsPinned ? thread.PinnedOrder ?? long.MaxValue : long.MaxValue)
        .ThenBy(static thread => thread.IsSettled)
        .ThenByDescending(static thread => thread.UpdatedUtc)
        .ThenBy(static thread => thread.ThreadId.Value, StringComparer.Ordinal);
}
