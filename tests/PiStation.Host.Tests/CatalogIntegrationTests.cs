using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Protocol.Models;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Tests;

public sealed class CatalogIntegrationTests
{
    [Fact]
    public async Task CatalogPreservesProjectDefaultsAndReplaysInboxAndContextOnlyDraftChanges()
    {
        using var directory = new HostTestDirectory();
        var database = new HostDatabase(directory.CreateOptions());
        await database.InitializeAsync();
        var service = new ProjectService(database);
        var project = await service.AddAsync(new(directory.CreateDirectory("project")));
        project = await service.UpdateDefaultsAsync(new(project.ProjectId, ThreadWorkspaceMode.Worktree,
            new("provider", "model"), PiThinkingLevel.High, "supervised", true,
            [new("test", "Test", "dotnet test", ProjectScriptIcon.Play, true)], "emoji:🚀", true));
        var thread = await service.CreateThreadAsync(new(project.ProjectId));
        await database.UpdateThreadInboxAsync(thread.ThreadId, thread.Revision, isSettled: false,
            pinnedOrder: 7, updatePinnedOrder: true, titleKind: ThreadTitleKind.Manual);
        var full = await ReadAsync(database, null);
        var syncedProject = Assert.Single(full.SelectMany(batch => batch.Projects));
        Assert.Equal(project.DefaultModel, syncedProject.DefaultModel);
        Assert.Equal(project.DefaultThinkingLevel, syncedProject.DefaultThinkingLevel);
        Assert.Equal(project.DefaultRuntimeModeId, syncedProject.DefaultRuntimeModeId);
        Assert.Equal(project.Icon, syncedProject.Icon);
        Assert.Equal(project.Scripts, syncedProject.Scripts);
        Assert.True(syncedProject.AutoPullDefaultBranch);
        var syncedThread = Assert.Single(full.SelectMany(batch => batch.Threads));
        Assert.False(syncedThread.IsSettled);
        Assert.Equal(7, syncedThread.PinnedOrder);
        Assert.Equal(ThreadTitleKind.Manual, syncedThread.TitleKind);
        Assert.False(syncedThread.HasUnsentDraft);

        var draft = await database.GetOrCreateThreadDraftAsync(thread.ThreadId);
        await database.UpdateThreadDraftAsync(thread.ThreadId, draft.DraftId, draft.Revision, "",
            [new("context", "file", "File", "unsent context")]);
        await database.SetThreadSettlementAutomaticallyAsync(thread.ThreadId, true);
        var completed = await database.RecordCompletionAsync(thread.ThreadId);
        var delta = await ReadAsync(database, new(full[^1].Epoch, full[^1].Sequence));
        Assert.DoesNotContain(delta, batch => batch.Reset);
        syncedThread = Assert.Single(delta.SelectMany(batch => batch.Threads));
        Assert.True(syncedThread.HasUnsentDraft);
        Assert.Equal(completed, syncedThread.CompletionSequence);
        Assert.True(syncedThread.IsSettled);
        await database.SetReadStateAsync(thread.ThreadId, completed, false);
        var read = await ReadAsync(database, new(delta[^1].Epoch, delta[^1].Sequence));
        Assert.Equal(completed, Assert.Single(read.SelectMany(batch => batch.Threads)).ReadCompletionSequence);
    }

    private static async Task<List<CatalogBatch>> ReadAsync(HostDatabase database, CatalogCursor? cursor)
    {
        var batches = new List<CatalogBatch>();
        await foreach (var batch in database.ReadCatalogAsync(cursor)) batches.Add(batch);
        Assert.True(batches[^1].Complete);
        return batches;
    }
}
