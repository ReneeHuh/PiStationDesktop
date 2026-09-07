using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class ThreadReadStateTests
{
    [Fact]
    public async Task CompletionAndExplicitUnreadSurviveRestartWithoutChangingActivityOrSettlement()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions();
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var projects = new ProjectService(database);
        var project = await projects.AddAsync(new(directory.CreateDirectory("project")));
        var thread = await projects.CreateThreadAsync(new(project.ProjectId));
        async Task<ThreadDescriptor> Read(HostDatabase db) => await db.EnrichThreadDescriptorAsync((await db.GetThreadAsync(thread.ThreadId))!);
        var initial = await Read(database);
        Assert.False(initial.IsUnread);
        await database.SetReadStateAsync(thread.ThreadId, 0, true);
        Assert.False((await Read(database)).IsUnread);
        Assert.Equal(1, await database.RecordCompletionAsync(thread.ThreadId));
        var unread = await Read(database);
        Assert.True(unread.IsUnread);
        Assert.Equal("Unread", unread.UnreadLabel);
        Assert.Equal(initial.UpdatedUtc, unread.UpdatedUtc);
        Assert.Equal(initial.IsSettled, unread.IsSettled);
        Assert.True(unread.Revision > initial.Revision);
        await database.SetReadStateAsync(thread.ThreadId, 1, false);
        var read = await Read(database);
        Assert.False(read.IsUnread);
        await database.SetReadStateAsync(thread.ThreadId, 1, false);
        Assert.Equal(read.Revision, (await Read(database)).Revision);
        await database.SetReadStateAsync(thread.ThreadId, 1, true);
        var restarted = new HostDatabase(options);
        await restarted.InitializeAsync();
        var restored = await Read(restarted);
        Assert.True(restored.IsUnread);
        Assert.Equal(1, restored.CompletionSequence);
        Assert.Equal(0, restored.ReadCompletionSequence);
        await restarted.SetReadStateAsync(thread.ThreadId, 1, false);
        Assert.False((await Read(restarted)).IsUnread);
    }

    [Fact]
    public async Task StaleReadCannotClearLaterCompletionAndFutureReadCannotPreclearWork()
    {
        using var directory = new HostTestDirectory();
        var database = new HostDatabase(directory.CreateOptions());
        await database.InitializeAsync();
        var projects = new ProjectService(database);
        var project = await projects.AddAsync(new(directory.CreateDirectory("project")));
        var thread = await projects.CreateThreadAsync(new(project.ProjectId));
        await database.RecordCompletionAsync(thread.ThreadId);
        await database.RecordCompletionAsync(thread.ThreadId);
        await database.SetReadStateAsync(thread.ThreadId, 1, false);
        var metadata = await database.EnrichThreadDescriptorAsync((await database.GetThreadAsync(thread.ThreadId))!);
        Assert.True(metadata.IsUnread);
        Assert.Equal(1, metadata.ReadCompletionSequence);
        await database.SetReadStateAsync(thread.ThreadId, long.MaxValue, false);
        Assert.True((await database.EnrichThreadDescriptorAsync((await database.GetThreadAsync(thread.ThreadId))!)).IsUnread);
        await database.SetReadStateAsync(thread.ThreadId, 2, false);
        await database.SetReadStateAsync(thread.ThreadId, 1, false);
        Assert.False((await database.EnrichThreadDescriptorAsync((await database.GetThreadAsync(thread.ThreadId))!)).IsUnread);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => database.SetReadStateAsync(thread.ThreadId, -1, false));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => database.SetReadStateAsync(ThreadId.New(), 0, false));
    }
}
