using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class ComposerDurabilityTests
{
    [Fact]
    public async Task ContextAndStashedAttachmentsSurviveSwitchesRestartAndRestore()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions();
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var projects = new ProjectService(database);
        var project = await projects.AddAsync(new AddProjectRequest(directory.CreateDirectory("project")));
        var first = await projects.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        var second = await projects.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        var draft = await database.GetOrCreateThreadDraftAsync(first.ThreadId);
        ComposerContext[] context = [new("citation-1", "response", "Selected response", "Relevant text", first.ThreadId, "message-1", StartLine: 2, EndLine: 3)];
        var updated = await database.UpdateThreadDraftAsync(first.ThreadId, draft.DraftId, draft.Revision, "Review this", context);
        var attachment = new DraftAttachment(database.EnvironmentId, first.ThreadId, draft.DraftId, AttachmentId.New(), "notes.txt", "text/plain", 4, "hash", Path.Combine(options.AttachmentRoot, "notes.txt"), DateTimeOffset.UtcNow);
        var attached = await database.AddDraftAttachmentAsync(first.ThreadId, draft.DraftId, updated.Draft!.Revision, attachment, 8);
        var stash = await database.SavePromptStashAsync(new SavePromptStashRequest(project.ProjectId, first.ThreadId, "Review this", DraftId: draft.DraftId, ExpectedRevision: attached.Draft!.Revision));
        await database.ClearThreadDraftAsync(first.ThreadId, draft.DraftId, attached.Draft.Revision, [attachment.AttachmentId]);
        Assert.True(await database.IsAttachmentReferencedAsync(attachment.ServerPath));
        var other = await database.GetOrCreateThreadDraftAsync(second.ThreadId);
        Assert.Empty(other.Context ?? []);
        Assert.Empty(other.Attachments);
        var restarted = new HostDatabase(options);
        await restarted.InitializeAsync();
        await restarted.RestorePromptStashAsync(second.ThreadId, other.DraftId, other.Revision, stash.StashId);
        var restored = await restarted.GetOrCreateThreadDraftAsync(second.ThreadId);
        Assert.Equal("Review this", restored.Text);
        Assert.Equal(context, restored.Context);
        Assert.Equal(second.ThreadId, Assert.Single(restored.Attachments).ThreadId);
        await restarted.DeletePromptStashAsync(stash.StashId);
        Assert.True(await restarted.IsAttachmentReferencedAsync(attachment.ServerPath));
        await restarted.ClearThreadDraftAsync(second.ThreadId, restored.DraftId, restored.Revision, restored.Attachments.Select(static item => item.AttachmentId).ToArray());
        Assert.False(await restarted.IsAttachmentReferencedAsync(attachment.ServerPath));
    }

    [Fact]
    public async Task StaleSavesAndRestoreIntoNonemptyDraftCannotOverwriteContext()
    {
        using var directory = new HostTestDirectory();
        var database = new HostDatabase(directory.CreateOptions());
        await database.InitializeAsync();
        var projects = new ProjectService(database);
        var project = await projects.AddAsync(new AddProjectRequest(directory.CreateDirectory("project")));
        var thread = await projects.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        var draft = await database.GetOrCreateThreadDraftAsync(thread.ThreadId);
        ComposerContext[] context = [new("first", "file", "source.cs", "saved range")];
        await database.UpdateThreadDraftAsync(thread.ThreadId, draft.DraftId, draft.Revision, "Keep me", context);
        var stale = await database.UpdateThreadDraftAsync(thread.ThreadId, draft.DraftId, draft.Revision, "stale", []);
        Assert.Equal("Keep me", stale.Draft!.Text);
        Assert.Equal(context, stale.Draft.Context);
        var stash = await database.SavePromptStashAsync(new SavePromptStashRequest(project.ProjectId, thread.ThreadId, "Another prompt"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.RestorePromptStashAsync(thread.ThreadId, draft.DraftId, stale.Draft.Revision, stash.StashId));
        Assert.Equal("Keep me", (await database.GetOrCreateThreadDraftAsync(thread.ThreadId)).Text);
    }

    [Fact]
    public async Task UnknownCostIsNotReportedAsZeroAndKnownCostSurvivesRestart()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions();
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var projects = new ProjectService(database);
        var project = await projects.AddAsync(new AddProjectRequest(directory.CreateDirectory("project")));
        var thread = await projects.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        await database.AppendUsageAsync(thread.ThreadId, "fake", "priced", 10, 20, 0, 30, 0.125m);
        var restarted = new HostDatabase(options);
        await restarted.InitializeAsync();
        Assert.Equal(0.125m, (await restarted.GetUsageSummaryAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue)).EstimatedCost);
        await restarted.AppendUsageAsync(thread.ThreadId, "fake", "unknown", 1, 2, 0, 3, null);
        var usage = await restarted.GetUsageSummaryAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.Null(usage.EstimatedCost);
        Assert.Equal(33, usage.TotalTokens);
        Assert.Equal(0.125m, usage.Breakdown.Single(static item => item.Model == "priced").EstimatedCost);
        Assert.Null(usage.Breakdown.Single(static item => item.Model == "unknown").EstimatedCost);
    }
}
