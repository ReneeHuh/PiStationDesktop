using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

public enum ThreadInboxShelf { Active, Settled, Snoozed, Archived }

public static class ThreadInbox
{
    public static ThreadInboxShelf Shelf(ThreadDescriptor thread, DateTimeOffset now) => thread.IsArchived
        ? ThreadInboxShelf.Archived
        : thread.SnoozedUntilUtc > now ? ThreadInboxShelf.Snoozed
        : thread.IsSettled ? ThreadInboxShelf.Settled : ThreadInboxShelf.Active;

    public static ThreadDescriptor Normalize(ThreadDescriptor thread, DateTimeOffset now) =>
        thread.SnoozedUntilUtc <= now ? thread with { SnoozedUntilUtc = null } : thread;

    public static IReadOnlyList<ThreadDescriptor> Select(IEnumerable<ThreadDescriptor> threads, ThreadInboxShelf shelf, DateTimeOffset now) =>
        ThreadOrdering.Apply(threads.Where(thread => Shelf(thread, now) == shelf).Select(thread => Normalize(thread, now))).ToArray();
}
