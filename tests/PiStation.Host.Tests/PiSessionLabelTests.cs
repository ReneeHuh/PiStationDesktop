using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using PiStation.Host.Hosting;
using PiStation.PiRpc.Sessions;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class PiSessionLabelTests
{
    [Fact]
    public async Task LabelsRoundTripThroughRpcRestartCopyExportAndForkWithoutChangingConversationOrDraft()
    {
        using var directory = new HostTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var options = directory.CreateOptions();
        ThreadDescriptor thread;
        SetPiSessionLabelRequest request;
        string revision;
        await using (var host = await EmbeddedEnvironmentHost.StartAsync(options))
        {
            var project = await host.Environment.AddProjectAsync(new(directory.CreateDirectory("project")), token);
            var source = directory.GetPath("original.jsonl");
            var original = PiSessionIntegrationTests.Fixture(project.CanonicalPath);
            await File.WriteAllTextAsync(source, original, token);
            thread = await host.Environment.CopyPiSessionAsync(new(Guid.NewGuid(), project.ProjectId, source), token);
            var beforeDraft = await host.Environment.GetThreadDraftAsync(thread.ThreadId, token);
            var snapshot = await host.Environment.InspectPiSessionAsync(thread.ThreadId, token);
            request = new(Guid.NewGuid(), thread.ThreadId, "a1", snapshot.Revision, "<Design decision>");
            await using var connection = HostTestConnection.Create(host);
            await connection.StartAsync(token);
            snapshot = await connection.InvokeAsync<PiSessionSnapshot>("SetPiSessionLabel", request, token);
            Assert.Equal(4, snapshot.ActiveMessageCount);
            Assert.Equal("<Design decision>", snapshot.Entries.Single(entry => entry.Id == "a1").Label);
            Assert.NotNull(snapshot.Entries.Single(entry => entry.Id == "a1").LabelTimestamp);
            var labeled = await connection.InvokeAsync<PiSessionSnapshot>("InspectPiSessionPage", new PiSessionPageRequest(thread.ThreadId,
                Filter: PiSessionTreeFilter.LabeledOnly, SearchQuery: "DESIGN assistant"), token);
            Assert.Equal("a1", Assert.Single(labeled.Entries).Id);
            Assert.Equal(1, labeled.MatchingEntries);
            var replay = await connection.InvokeAsync<PiSessionSnapshot>("SetPiSessionLabel", request, token);
            Assert.Equal(snapshot.Revision, replay.Revision);
            await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<PiSessionSnapshot>("SetPiSessionLabel", request with { Label = "conflict" }, token));
            await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<PiSessionSnapshot>("SetPiSessionLabel", request with { OperationId = Guid.NewGuid() }, token));
            await Assert.ThrowsAsync<InvalidDataException>(() => host.Environment.SetPiSessionLabelAsync(request with { Label = "line\nbreak" }, token));
            await Assert.ThrowsAsync<InvalidDataException>(() => host.Environment.SetPiSessionLabelAsync(request with { Label = new string('x', 257) }, token));
            await Assert.ThrowsAsync<InvalidDataException>(() => host.Environment.SetPiSessionLabelAsync(request with { OperationId = Guid.NewGuid(), ExpectedRevision = snapshot.Revision, EntryId = "missing" }, token));
            var afterDraft = await host.Environment.GetThreadDraftAsync(thread.ThreadId, token);
            Assert.Equal(beforeDraft.DraftId, afterDraft.DraftId);
            Assert.Equal(beforeDraft.Revision, afterDraft.Revision);
            Assert.Equal(beforeDraft.Text, afterDraft.Text);
            Assert.Equal(beforeDraft.Context, afterDraft.Context);
            Assert.Equal(beforeDraft.Attachments, afterDraft.Attachments);
            await Assert.ThrowsAsync<ArgumentException>(() => host.Environment.InspectPiSessionPageAsync(new(thread.ThreadId, SearchQuery: new string('x', 1025)), token));
            await Assert.ThrowsAsync<ArgumentException>(() => host.Environment.InspectPiSessionPageAsync(new(thread.ThreadId, Filter: (PiSessionTreeFilter)999), token));
            await Assert.ThrowsAsync<InvalidDataException>(() => host.Environment.InspectPiSessionPageAsync(new(thread.ThreadId, Offset: 1, ExpectedRevision: request.ExpectedRevision), token));
            foreach (var format in new[] { PiSessionExportFormat.Jsonl, PiSessionExportFormat.Html, PiSessionExportFormat.Bundle })
            {
                var destination = directory.GetPath(format switch { PiSessionExportFormat.Jsonl => "export.jsonl", PiSessionExportFormat.Html => "export.html", _ => "export.zip" });
                await host.Environment.ExportPiSessionAsync(new(thread.ThreadId, destination, format), token);
                if (format == PiSessionExportFormat.Html) Assert.Contains("&lt;Design decision&gt;", await File.ReadAllTextAsync(destination, token));
                else
                {
                    var imported = await host.Environment.CopyPiSessionAsync(new(Guid.NewGuid(), project.ProjectId, destination), token);
                    Assert.Equal("<Design decision>", (await PiSessionDocument.ReadAsync(imported.PiSessionFile!, token)).Labels()["a1"].Label);
                }
            }
            var fork = await host.Environment.CopyPiSessionAsync(new(Guid.NewGuid(), project.ProjectId, SourceThreadId: thread.ThreadId, EntryId: "a1", ExpectedRevision: snapshot.Revision), token);
            var forked = await host.Environment.InspectPiSessionAsync(fork.ThreadId, token);
            Assert.Equal(2, forked.ActiveMessageCount);
            Assert.Equal("<Design decision>", forked.Entries.Single(entry => entry.Id == "a1").Label);
            Assert.Equal(original, await File.ReadAllTextAsync(source, token));
            revision = snapshot.Revision;
        }
        await using var restarted = await EmbeddedEnvironmentHost.StartAsync(options);
        var restored = await restarted.Environment.SetPiSessionLabelAsync(request, token);
        Assert.Equal(revision, restored.Revision);
        var renamed = await restarted.Environment.SetPiSessionLabelAsync(request with { OperationId = Guid.NewGuid(), ExpectedRevision = revision, Label = "Renamed" }, token);
        var replayedOldEdit = await restarted.Environment.SetPiSessionLabelAsync(request, token);
        Assert.Equal(renamed.Revision, replayedOldEdit.Revision);
        Assert.Equal("Renamed", replayedOldEdit.Entries.Single(entry => entry.Id == "a1").Label);
        var cleared = await restarted.Environment.SetPiSessionLabelAsync(request with { OperationId = Guid.NewGuid(), ExpectedRevision = renamed.Revision, Label = "  " }, token);
        Assert.Null(cleared.Entries.Single(entry => entry.Id == "a1").Label);
        Assert.Empty((await restarted.Environment.InspectPiSessionPageAsync(new(thread.ThreadId, Filter: PiSessionTreeFilter.LabeledOnly), token)).Entries);
        Assert.Equal(4, cleared.ActiveMessageCount);
    }

    [Theory]
    [InlineData(PiSessionTreeFilter.Default, "u1,a1,u2,a2,r,e,c,s")]
    [InlineData(PiSessionTreeFilter.NoTools, "u1,a1,u2,a2,e,c,s")]
    [InlineData(PiSessionTreeFilter.UserOnly, "u1,u2")]
    [InlineData(PiSessionTreeFilter.LabeledOnly, "a1")]
    [InlineData(PiSessionTreeFilter.All, "model,thinking,u1,a1,u2,a2,r,e,c,s,x,l")]
    public void PiFiltersKeepErrorsAndHideIntermediateAssistantToolCalls(PiSessionTreeFilter filter, string expected)
    {
        var document = Parse(PiSessionIntegrationTests.Fixture("C:\\project") + "\n" + """
            {"type":"message","id":"t","parentId":"a2","message":{"role":"assistant","content":[{"type":"toolCall","name":"read"}],"stopReason":"toolUse"}}
            {"type":"message","id":"r","parentId":"t","message":{"role":"toolResult","content":[{"type":"text","text":"Tool output"}]}}
            {"type":"message","id":"e","parentId":"r","message":{"role":"assistant","content":[],"stopReason":"error"}}
            {"type":"custom_message","id":"c","parentId":"e","customType":"notice","content":"Custom notice"}
            {"type":"branch_summary","id":"s","parentId":"c","summary":"Branch memory"}
            {"type":"custom","id":"x","parentId":"s","customType":"state"}
            {"type":"label","id":"l","parentId":"x","targetId":"a1","label":"Design"}
            """);
        var snapshot = EnvironmentService.CreateSessionSnapshot(ThreadId.New(), "fixture", document, filter: filter);
        Assert.Equal(expected.Split(','), snapshot.Entries.Select(entry => entry.Id));
    }

    [Fact]
    public void SearchRunsBeforePagingAndMatchesFullTextAllWordsLabelsAndInactiveBranches()
    {
        var text = new StringBuilder(PiSessionIntegrationTests.Fixture("C:\\project"));
        for (var i = 0; i < 1200; i++) text.AppendLine().Append(System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "message", id = "extra" + i, parentId = "a1", message = new { role = "user", content = new string('x', 300) + " needle " + i },
        }));
        var document = Parse(text.ToString());
        var threadId = ThreadId.New();
        var first = EnvironmentService.CreateSessionSnapshot(threadId, "fixture", document, limit: 1000, filter: PiSessionTreeFilter.UserOnly, searchQuery: "NEEDLE user");
        Assert.Equal(1200, first.MatchingEntries);
        Assert.Equal(1000, first.Entries.Count);
        Assert.Equal(1000, first.NextOffset);
        Assert.DoesNotContain("needle", first.Entries[0].Preview);
        var next = EnvironmentService.CreateSessionSnapshot(threadId, "fixture", document, offset: first.NextOffset!.Value, limit: 1000,
            filter: first.Filter, searchQuery: first.SearchQuery);
        Assert.Equal(200, next.Entries.Count);
        Assert.Null(next.NextOffset);
        Assert.Equal("extra1000", next.Entries[0].Id);
        Assert.Equal("extra1199", Assert.Single(EnvironmentService.CreateSessionSnapshot(threadId, "fixture", document,
            searchQuery: "needle", activeBranchOnly: true).Entries).Id);
        Assert.Empty(EnvironmentService.CreateSessionSnapshot(threadId, "fixture", document, searchQuery: "needle missing").Entries);
    }

    [Fact]
    public void ForkUsesLatestSessionWideLabelIncludingClearsFromOtherBranches()
    {
        var document = Parse(PiSessionIntegrationTests.Fixture("C:\\project") + "\n" + """
            {"type":"label","id":"l1","parentId":"a1","targetId":"a1","label":"Old"}
            {"type":"message","id":"forkpoint","parentId":"l1","message":{"role":"assistant","content":"Fork here","stopReason":"stop"}}
            {"type":"label","id":"l2","parentId":"a2","targetId":"a1"}
            {"type":"label","id":"l3","parentId":"l2","targetId":"u1","label":"Retain"}
            """);
        var fork = PiSessionDocument.Parse(document.Copy(Guid.NewGuid().ToString("N"), "C:\\project", "source", "forkpoint"));
        Assert.False(fork.Labels().ContainsKey("a1"));
        Assert.Equal("Retain", fork.Labels()["u1"].Label);
    }

    private static PiSessionDocument Parse(string text) => PiSessionDocument.Parse(Encoding.UTF8.GetBytes(text));
}
