using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class ThreadInboxTests
{
    [Fact]
    public void SnoozeExpiresIntoThePreviousShelfAndArchiveTakesPrecedence()
    {
        var now = DateTimeOffset.UtcNow;
        var thread = new ThreadDescriptor(EnvironmentId.New(), ThreadId.New(), ProjectId.New(), "task", "session", null, now, now,
            SnoozedUntilUtc: now.AddMinutes(1));
        Assert.Equal(ThreadInboxShelf.Snoozed, ThreadInbox.Shelf(thread, now));
        Assert.Equal(ThreadInboxShelf.Active, ThreadInbox.Shelf(thread, now.AddMinutes(1)));
        Assert.Null(ThreadInbox.Normalize(thread, now.AddMinutes(1)).SnoozedUntilUtc);
        Assert.Equal(ThreadInboxShelf.Settled, ThreadInbox.Shelf(thread with { IsSettled = true }, now.AddMinutes(2)));
        Assert.Equal(ThreadInboxShelf.Archived, ThreadInbox.Shelf(thread with { IsArchived = true }, now));
    }
}
