using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using PiStation.Host.Hosting;
using PiStation.Host.Errors;
using PiStation.Host.Persistence;
using PiStation.Host.Threads;
using PiStation.Protocol;
using PiStation.Protocol.Commands;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Tests;

public sealed class PiSessionNavigationTests
{
    [Fact]
    public async Task SwitchReplayAndRestartKeepSessionDraftAttachmentsAndAlternateBranches()
    {
        using var directory = new HostTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var options = directory.CreateOptions();
        ThreadDescriptor thread;
        NavigatePiSessionRequest last;
        await using (var host = await EmbeddedEnvironmentHost.StartAsync(options))
        {
            thread = await ImportAsync(host, directory, timeout.Token);
            await using var connection = HostTestConnection.Create(host);
            await connection.StartAsync(timeout.Token);
            var draft = await host.Environment.GetThreadDraftAsync(thread.ThreadId, timeout.Token);
            var context = new ComposerContext("context", "file", "Selected file", "selection");
            await host.Environment.ExecuteThreadCommandAsync(new(ProtocolVersion.Current, thread.EnvironmentId, ClientId.New(), CommandId.New(), thread.ThreadId, null, null,
                new ThreadSaveDraftCommand(draft.DraftId, draft.Revision, "Keep my unsent draft", [context])), timeout.Token);
            draft = await host.Environment.GetThreadDraftAsync(thread.ThreadId, timeout.Token);
            var attachment = new DraftAttachment(thread.EnvironmentId, thread.ThreadId, draft.DraftId, AttachmentId.New(), "notes.txt", "text/plain", 4,
                new string('A', 64), directory.GetPath("notes.txt"), DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(attachment.ServerPath, "keep", timeout.Token);
            var database = new HostDatabase(options);
            await database.InitializeAsync(timeout.Token);
            await database.AddDraftAttachmentAsync(thread.ThreadId, draft.DraftId, draft.Revision, attachment, 8, timeout.Token);
            var before = await host.Environment.GetThreadDraftAsync(thread.ThreadId, timeout.Token);
            var snapshot = await host.Environment.InspectPiSessionAsync(thread.ThreadId, timeout.Token);
            var request = new NavigatePiSessionRequest(Guid.NewGuid(), thread.ThreadId, "a1", snapshot.Revision);
            var switched = await connection.InvokeAsync<NavigatePiSessionResult>("NavigatePiSession", request, timeout.Token);
            Assert.Equal(2, switched.Snapshot.ActiveMessageCount);
            Assert.False(switched.Cancelled);
            Assert.Equal(thread.PiSessionFile, switched.Snapshot.Path);
            var replay = await connection.InvokeAsync<NavigatePiSessionResult>("NavigatePiSession", request, timeout.Token);
            Assert.Equal(switched.Snapshot.Revision, replay.Snapshot.Revision);
            await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<NavigatePiSessionResult>("NavigatePiSession", request with { EntryId = "a2" }, timeout.Token));
            await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<NavigatePiSessionResult>("NavigatePiSession", request with { OperationId = Guid.NewGuid() }, timeout.Token));
            var after = await host.Environment.GetThreadDraftAsync(thread.ThreadId, timeout.Token);
            Assert.Equal(before.Text, after.Text);
            Assert.Equal(before.Revision, after.Revision);
            Assert.Equal(before.DraftId, after.DraftId);
            Assert.Equal(before.Attachments, after.Attachments);
            Assert.Equal(before.Context, after.Context);
            Assert.Equal("keep", await File.ReadAllTextAsync(attachment.ServerPath, timeout.Token));
            Assert.DoesNotContain("Second answer", await TranscriptAsync(host, thread.ThreadId, timeout.Token));
            last = request with { OperationId = Guid.NewGuid(), EntryId = "a2", ExpectedRevision = switched.Snapshot.Revision };
            var returned = await connection.InvokeAsync<NavigatePiSessionResult>("NavigatePiSession", last, timeout.Token);
            Assert.Equal(4, returned.Snapshot.ActiveMessageCount);
            Assert.Contains(returned.Snapshot.Entries, entry => entry.Id == "a1");
            Assert.Contains("Second answer", await TranscriptAsync(host, thread.ThreadId, timeout.Token));
            Assert.Single(await host.Environment.ListThreadsAsync(thread.ProjectId, timeout.Token));
        }
        await using var restarted = await EmbeddedEnvironmentHost.StartAsync(options);
        Assert.Contains("Second answer", await TranscriptAsync(restarted, thread.ThreadId, timeout.Token));
        var restored = await restarted.Environment.NavigatePiSessionAsync(last, timeout.Token);
        Assert.Equal(4, restored.Snapshot.ActiveMessageCount);
        var toUser = last with { OperationId = Guid.NewGuid(), EntryId = "u1", ExpectedRevision = restored.Snapshot.Revision };
        var user = await restarted.Environment.NavigatePiSessionAsync(toUser, timeout.Token);
        Assert.Equal("First question", user.EditorText);
        Assert.Equal(0, user.Snapshot.ActiveMessageCount);
        Assert.DoesNotContain("First question", await TranscriptAsync(restarted, thread.ThreadId, timeout.Token));
        Assert.Equal("Keep my unsent draft", (await restarted.Environment.GetThreadDraftAsync(thread.ThreadId, timeout.Token)).Text);
        Assert.Contains(user.Snapshot.Entries, entry => entry.Id == "a2");
    }

    [Fact]
    public async Task CancellationAndHookFailureKeepTheSavedBranchAndAllowRecovery()
    {
        using var directory = new HostTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateOptions());
        var thread = await ImportAsync(host, directory, timeout.Token);
        var snapshot = await host.Environment.InspectPiSessionAsync(thread.ThreadId, timeout.Token);
        var request = new NavigatePiSessionRequest(Guid.NewGuid(), thread.ThreadId, "a1", snapshot.Revision, true, "test:wait-for-cancel");
        await using var live = host.Environment.SubscribeThreadAsync(thread.ThreadId, null, timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await live.MoveNextAsync());
        var navigation = host.Environment.NavigatePiSessionAsync(request, timeout.Token);
        do { Assert.True(await live.MoveNextAsync()); }
        while (live.Current is not ThreadEventEnvelope { Event: RuntimeStateChangedEvent { State: ThreadRuntimeState.Hydrating } });
        await Assert.ThrowsAsync<HostOperationException>(() => host.Environment.ExecuteThreadCommandAsync(new(ProtocolVersion.Current,
            thread.EnvironmentId, ClientId.New(), CommandId.New(), thread.ThreadId, null, null,
            new ThreadStartTurnCommand("Do not run during navigation")), timeout.Token));
        var canceled = false;
        while (!navigation.IsCompleted && !canceled)
        {
            await Task.Delay(50, timeout.Token);
            canceled = await host.Environment.CancelPiSessionNavigationAsync(new(thread.ThreadId, request.OperationId), timeout.Token);
        }
        Assert.True(canceled);
        Assert.True((await navigation).Cancelled);
        Assert.Equal(snapshot.Revision, (await host.Environment.InspectPiSessionAsync(thread.ThreadId, timeout.Token)).Revision);
        await Assert.ThrowsAnyAsync<Exception>(() => host.Environment.NavigatePiSessionAsync(request with
            { OperationId = Guid.NewGuid(), CustomInstructions = "test:fail" }, timeout.Token));
        var recovered = await host.Environment.InspectPiSessionAsync(thread.ThreadId, timeout.Token);
        Assert.Equal(4, recovered.ActiveMessageCount);
        Assert.Contains("Second answer", await TranscriptAsync(host, thread.ThreadId, timeout.Token));
        Assert.False(await host.Environment.CancelPiSessionNavigationAsync(new(thread.ThreadId, Guid.NewGuid()), timeout.Token));
    }

    [Fact]
    public async Task MissingSdkBridgeLeavesTheOriginalSessionRecoverable()
    {
        using var directory = new HostTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateOptions("navigation-unavailable"));
        var thread = await ImportAsync(host, directory, timeout.Token);
        var snapshot = await host.Environment.InspectPiSessionAsync(thread.ThreadId, timeout.Token);
        var error = await Assert.ThrowsAsync<HostOperationException>(() => host.Environment.NavigatePiSessionAsync(
            new(Guid.NewGuid(), thread.ThreadId, "a1", snapshot.Revision), timeout.Token));
        Assert.Contains("unavailable", error.Message);
        Assert.Equal(4, (await host.Environment.InspectPiSessionAsync(thread.ThreadId, timeout.Token)).ActiveMessageCount);
    }

    [Fact]
    public void HydrationUsesOnlyActiveAncestryAndExcludesAnotherBranchsCheckpoints()
    {
        var entries = JsonSerializer.Deserialize<JsonElement[]>("""
            [
              {"type":"message","id":"u","parentId":null,"message":{"role":"user","content":"shared"}},
              {"type":"message","id":"a","parentId":"u","message":{"role":"assistant","content":"old branch"}},
              {"type":"message","id":"b","parentId":"u","message":{"role":"assistant","content":"chosen branch"}},
              {"type":"branch_summary","id":"summary","parentId":"b","summary":"Retained context"},
              {"type":"custom","id":"marker","parentId":"summary","customType":"pistation.branch-navigation","data":{}}
            ]
            """)!;
        var current = ThreadProjectionReducer.Create(EnvironmentId.New(), ThreadId.New(), "session");
        var checkpoint = new ThreadCheckpoint(TurnId.New(), 1, "ref", ThreadCheckpointStatus.Ready, [], null, "a", DateTimeOffset.UtcNow);
        var hydrated = ThreadProjectionReducer.Hydrate(current, entries, "marker", "session.jsonl", checkpoints: [checkpoint]);
        var text = string.Join('\n', hydrated.Timeline.OfType<MessageTimelineItem>().Select(item => item.Text));
        Assert.Contains("chosen branch", text);
        Assert.DoesNotContain("old branch", text);
        Assert.Contains("Branch summary\nRetained context", text);
        Assert.Empty(hydrated.Checkpoints);
        Assert.Empty(ThreadProjectionReducer.Hydrate(current, entries, null, "session.jsonl").Timeline);
    }

    private static async Task<ThreadDescriptor> ImportAsync(EmbeddedEnvironmentHost host, HostTestDirectory directory, CancellationToken token)
    {
        var project = await host.Environment.AddProjectAsync(new(directory.CreateDirectory("project")), token);
        var source = directory.GetPath("source.jsonl");
        await File.WriteAllTextAsync(source, PiSessionIntegrationTests.Fixture(project.CanonicalPath), token);
        return await host.Environment.CopyPiSessionAsync(new(Guid.NewGuid(), project.ProjectId, source), token);
    }

    private static async Task<string> TranscriptAsync(EmbeddedEnvironmentHost host, ThreadId threadId, CancellationToken token)
        => string.Join('\n', (await ProjectionAsync(host, threadId, token)).Timeline.OfType<MessageTimelineItem>().Select(item => item.Text));

    private static async Task<ThreadProjection> ProjectionAsync(EmbeddedEnvironmentHost host, ThreadId threadId, CancellationToken token)
    {
        await using var stream = host.Environment.SubscribeThreadPassiveAsync(threadId, null, token).GetAsyncEnumerator(token);
        Assert.True(await stream.MoveNextAsync());
        return Assert.IsType<ThreadSnapshotEnvelope>(stream.Current).Projection;
    }
}
