using System.Globalization;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class ThreadMetadataStoreTests
{
    [Fact]
    public void StoreRejectsOlderRevisionsAndOlderEqualRevisionSnapshots()
    {
        var store = new ThreadMetadataStore();
        var threadId = ThreadId.Parse("thread-1");
        var first = CreateThread(threadId, "First", revision: 1, updatedMinute: 1);
        var newerTimestamp = CreateThread(threadId, "Fresh", revision: 1, updatedMinute: 2);
        var olderTimestamp = CreateThread(threadId, "Stale timestamp", revision: 1, updatedMinute: 0);
        var olderRevision = CreateThread(threadId, "Stale revision", revision: 0, updatedMinute: 3);
        var newerRevision = CreateThread(threadId, "Newest", revision: 2, updatedMinute: 0);

        Assert.Equal(ThreadMetadataApplyResult.Applied, store.Apply(first));
        Assert.Equal(ThreadMetadataApplyResult.Applied, store.Apply(newerTimestamp));
        Assert.Equal(ThreadMetadataApplyResult.Ignored, store.Apply(olderTimestamp));
        Assert.Equal(ThreadMetadataApplyResult.Ignored, store.Apply(olderRevision));
        Assert.Equal(ThreadMetadataApplyResult.Applied, store.Apply(newerRevision));

        var current = store.GetCurrent(threadId);
        Assert.Equal("Newest", current?.Title);
        Assert.Equal(2, current?.Revision);
    }

    [Fact]
    public void StoreUsesAuthoritativeLifecycleOrderingAndArchivedFiltering()
    {
        var store = new ThreadMetadataStore();
        var projectId = ProjectId.Parse("project-1");
        var active = CreateThread(
            ThreadId.Parse("thread-active"),
            "Active",
            updatedMinute: 3,
            projectId: projectId);
        var pinned = CreateThread(
            ThreadId.Parse("thread-pinned"),
            "Pinned",
            updatedMinute: 1,
            isPinned: true,
            projectId: projectId);
        var archived = CreateThread(
            ThreadId.Parse("thread-archived"),
            "Archived",
            updatedMinute: 4,
            isArchived: true,
            isPinned: true,
            projectId: projectId);

        store.Apply(active);
        store.Apply(archived);
        store.Apply(pinned);

        Assert.Equal(
            [pinned.ThreadId, active.ThreadId],
            store.GetProjectThreads(projectId).Select(static thread => thread.ThreadId));
        Assert.Equal(
            [pinned.ThreadId, active.ThreadId, archived.ThreadId],
            store.GetProjectThreads(projectId, includeArchived: true)
                .Select(static thread => thread.ThreadId));
    }

    private static ThreadDescriptor CreateThread(
        ThreadId threadId,
        string title,
        long revision = 0,
        int updatedMinute = 0,
        bool isArchived = false,
        bool isPinned = false,
        ProjectId? projectId = null) => new(
        EnvironmentId.Parse("environment-1"),
        threadId,
        projectId ?? ProjectId.Parse("project-1"),
        title,
        $"session-{threadId.Value}",
        null,
        DateTimeOffset.Parse("2026-09-02T12:00:00Z", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(
            $"2026-09-02T12:{updatedMinute:00}:00Z",
            CultureInfo.InvariantCulture),
        revision,
        isArchived,
        isPinned);
}
