using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Streaming;

namespace PiStation.ClientRuntime.Tests;

public sealed class CatalogStoreTests
{
    [Fact]
    public void PagesCommitAtomicallyAndResumeKeepsNewerThreadMetadata()
    {
        var environment = EnvironmentId.New();
        var project = new ProjectDescriptor(environment, ProjectId.New(), "fixture", "Project", DateTimeOffset.UnixEpoch);
        var thread = new ThreadDescriptor(environment, ThreadId.New(), project.ProjectId, "First", "session", null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var store = new CatalogStore();
        var metadata = new ThreadMetadataStore();
        CatalogBatch Batch(long sequence, bool reset = false, bool complete = false, ProjectDescriptor[]? projects = null,
            ThreadDescriptor[]? threads = null, ThreadId[]? removed = null) =>
            new(environment, "epoch", sequence, reset, complete, projects ?? [], threads ?? [], [], removed ?? []);
        store.Apply(Batch(1, reset: true, projects: [project]), environment, metadata);
        store.Apply(Batch(1, threads: [thread]), environment, metadata);
        Assert.Empty(store.Projects);
        Assert.Null(store.Cursor);
        Assert.False(store.IsSynchronized);
        store.Apply(Batch(1, complete: true), environment, metadata);
        Assert.Single(store.Threads);
        Assert.True(store.IsSynchronized);
        metadata.Apply(thread with { Revision = 2, Title = "Latest command reply" });
        store.Apply(Batch(2, complete: true, threads: [thread with { Revision = 1, Title = "Older catalog event" }]), environment, metadata);
        Assert.Equal("Latest command reply", Assert.Single(store.Threads).Title);
        store.Apply(Batch(3, reset: true), environment, metadata);
        store.AbandonTransfer();
        Assert.Single(store.Threads);
        Assert.Equal(2, store.Cursor!.Sequence);
        store.Apply(Batch(4, complete: true, removed: [thread.ThreadId]), environment, metadata);
        Assert.Empty(store.Threads);
        Assert.Null(metadata.GetCurrent(thread.ThreadId));
        var replyOnly = thread with { ThreadId = ThreadId.New(), Title = "Created and deleted between catalog polls" };
        metadata.Apply(replyOnly);
        store.Apply(Batch(5, removed: [replyOnly.ThreadId]), environment, metadata);
        Assert.NotNull(metadata.GetCurrent(replyOnly.ThreadId));
        store.Apply(Batch(5, complete: true), environment, metadata);
        Assert.Null(metadata.GetCurrent(replyOnly.ThreadId));
        Assert.Throws<InvalidDataException>(() => store.Apply(Batch(6) with { EnvironmentId = EnvironmentId.New() }, environment, metadata));
    }
}
