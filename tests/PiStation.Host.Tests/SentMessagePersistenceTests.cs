using System.Text.Json;
using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Host.Threads;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Serialization;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Tests;

public sealed class SentMessagePersistenceTests
{
    [Fact]
    public async Task RetainedContentSurvivesDraftClearAndDatabaseRestartButRejectsStaleRevision()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions();
        var database = new HostDatabase(options);
        var environment = await database.InitializeAsync();
        var projects = new ProjectService(database);
        var project = await projects.AddAsync(new(directory.CreateDirectory("project")));
        var thread = await projects.CreateThreadAsync(new(project.ProjectId));
        var draft = await database.GetOrCreateThreadDraftAsync(thread.ThreadId);
        var attachment = new DraftAttachment(environment.EnvironmentId, thread.ThreadId, draft.DraftId,
            AttachmentId.New(), "notes.txt", "text/plain", 4, new string('A', 64), directory.GetPath("notes.txt"), DateTimeOffset.UtcNow);
        var added = await database.AddDraftAttachmentAsync(thread.ThreadId, draft.DraftId, draft.Revision, attachment, 10);
        var content = new SentMessageContent(Guid.NewGuid().ToString("N"), "prompt", [attachment],
            [new("source", "response", "Pi response", "saved quote", thread.ThreadId, "old-live-id")]);
        await database.RetainSentMessageAsync(thread.ThreadId, draft.DraftId, added.Draft!.Revision, content);
        await database.ClearThreadDraftAsync(thread.ThreadId, draft.DraftId, added.Draft.Revision, [attachment.AttachmentId]);
        Assert.True(await database.IsAttachmentReferencedAsync(attachment.ServerPath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.RetainSentMessageAsync(
            thread.ThreadId, draft.DraftId, added.Draft.Revision, content with { Id = Guid.NewGuid().ToString("N") }));
        var restarted = new HostDatabase(options);
        await restarted.InitializeAsync();
        var restored = Assert.Single(await restarted.ListSentMessagesAsync(thread.ThreadId)).Value;
        Assert.Equal(content.Id, restored.Id);
        Assert.Equal(attachment, Assert.Single(restored.Attachments));
        Assert.Equal("saved quote", Assert.Single(restored.Citations).Text);
        Assert.Empty(await restarted.ListSentMessagesAsync(ThreadId.New()));
        await restarted.DeleteThreadAsync(thread.ThreadId);
        Assert.False(await restarted.IsAttachmentReferencedAsync(attachment.ServerPath));
    }

    [Fact]
    public void HydrationUsesOnlyOwnedMetadataAndPreservesLiveContentThroughSerialization()
    {
        var content = new SentMessageContent(Guid.NewGuid().ToString("N"), "visible prompt", [],
            [new("source", "file", "source.cs:4", "quote", RelativePath: "source.cs", StartLine: 4)]);
        var projection = ThreadProjectionReducer.Create(EnvironmentId.New(), ThreadId.New(), "session");
        var started = new TurnStartedEvent(TurnId.New(), content.Text, content);
        var live = ThreadProjectionReducer.Apply(projection, started);
        var json = JsonSerializer.Serialize(live, ProtocolJsonContext.Default.ThreadProjection);
        Assert.Equal(content.Id, JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ThreadProjection)!.Messages[0].Content!.Id);
        var entry = JsonSerializer.SerializeToElement(new { type = "message", id = "persistent-entry", message = new
        { role = "user", content = SentMessageReference.Append("expanded prompt", content.Id) } });
        var restored = ThreadProjectionReducer.Hydrate(projection, [entry], "persistent-entry", null,
            sentMessages: new Dictionary<string, SentMessageContent> { [content.Id] = content });
        Assert.Equal("visible prompt", restored.Messages[0].Text);
        Assert.Equal("quote", Assert.Single(restored.Messages[0].Content!.Citations).Text);
        var unowned = ThreadProjectionReducer.Hydrate(projection, [entry], "persistent-entry", null);
        Assert.Null(unowned.Messages[0].Content);
    }
}
