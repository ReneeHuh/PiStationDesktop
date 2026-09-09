using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using PiStation.Host.Hosting;
using PiStation.PiRpc.Sessions;
using PiStation.Protocol;
using PiStation.Protocol.Commands;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class PiSessionIntegrationTests
{
    [Fact]
    public async Task ImportForkExportAndReplayPreserveSourceDraftModelAndRestartIdentity()
    {
        using var directory = new HostTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var options = directory.CreateOptions();
        var projectPath = directory.CreateDirectory("project");
        var source = directory.GetPath("cli-session.jsonl");
        var original = Fixture(projectPath);
        await File.WriteAllTextAsync(source, original, timeout.Token);
        ThreadDescriptor imported;
        ThreadDescriptor fork;
        CopyPiSessionRequest import;
        await using (var host = await EmbeddedEnvironmentHost.StartAsync(options))
        {
            await using var connection = HostTestConnection.Create(host);
            await connection.StartAsync(timeout.Token);
            var project = await host.Environment.AddProjectAsync(new(projectPath), timeout.Token);
            var browser = await connection.InvokeAsync<PiSessionBrowserResult>("BrowsePiSessions", new BrowsePiSessionsRequest(directory.Path), timeout.Token);
            var candidate = Assert.Single(browser.Sessions);
            import = new(Guid.NewGuid(), project.ProjectId, source, ExpectedRevision: candidate.Revision, Title: "Imported history");
            imported = await connection.InvokeAsync<ThreadDescriptor>("CopyPiSession", import, timeout.Token);
            var replay = await connection.InvokeAsync<ThreadDescriptor>("CopyPiSession", import, timeout.Token);
            Assert.Equal(imported.ThreadId, replay.ThreadId);
            Assert.Equal(imported.ThreadId.Value, imported.PiSessionId);
            var draft = await host.Environment.GetThreadDraftAsync(imported.ThreadId, timeout.Token);
            await host.Environment.ExecuteThreadCommandAsync(new(ProtocolVersion.Current, imported.EnvironmentId, ClientId.New(), CommandId.New(), imported.ThreadId, null, null,
                new ThreadSaveDraftCommand(draft.DraftId, draft.Revision, "Keep the original unsent draft")), timeout.Token);
            var snapshot = await connection.InvokeAsync<PiSessionSnapshot>("InspectPiSession", imported.ThreadId, timeout.Token);
            Assert.Equal(4, snapshot.ActiveMessageCount);
            Assert.Equal(new PiModelSelection("fake", "fake-fast"), snapshot.Model);
            Assert.Null(snapshot.Cost);
            var forkPoint = snapshot.Entries.Single(entry => entry.Id == "a1");
            Assert.True(forkPoint.CanFork);
            var forkRequest = new CopyPiSessionRequest(Guid.NewGuid(), project.ProjectId, SourceThreadId: imported.ThreadId, EntryId: forkPoint.Id,
                ExpectedRevision: snapshot.Revision, Title: "Independent fork");
            fork = await connection.InvokeAsync<ThreadDescriptor>("CopyPiSession", forkRequest, timeout.Token);
            Assert.NotEqual(imported.PiSessionId, fork.PiSessionId);
            Assert.NotEqual(imported.PiSessionFile, fork.PiSessionFile);
            var forkSnapshot = await connection.InvokeAsync<PiSessionSnapshot>("InspectPiSession", fork.ThreadId, timeout.Token);
            Assert.Equal(2, forkSnapshot.ActiveMessageCount);
            Assert.Equal(snapshot.Model, forkSnapshot.Model);
            Assert.Equal("Keep the original unsent draft", (await host.Environment.GetThreadDraftAsync(imported.ThreadId, timeout.Token)).Text);
            Assert.Empty((await host.Environment.GetThreadDraftAsync(fork.ThreadId, timeout.Token)).Text);
            var configuration = await host.Environment.GetThreadPiConfigurationAsync(fork.ThreadId, timeout.Token);
            Assert.Equal(new PiModelSelection("fake", "fake-fast"), configuration.Configuration.Model);
            Assert.Equal(PiThinkingLevel.Off, configuration.Configuration.ThinkingLevel);
            var jsonl = directory.GetPath("export.jsonl");
            var html = directory.GetPath("export.html");
            await connection.InvokeAsync<PiSessionExportResult>("ExportPiSession", new ExportPiSessionRequest(fork.ThreadId, jsonl, PiSessionExportFormat.Jsonl), timeout.Token);
            await connection.InvokeAsync<PiSessionExportResult>("ExportPiSession", new ExportPiSessionRequest(imported.ThreadId, html, PiSessionExportFormat.Html), timeout.Token);
            Assert.Equal(2, (await PiSessionDocument.ReadAsync(jsonl, timeout.Token)).Branch().Count(entry => entry["type"]?.ToString() == "message"));
            Assert.Contains("Second answer", await File.ReadAllTextAsync(html, timeout.Token));
            Assert.Equal(original, await File.ReadAllTextAsync(source, timeout.Token));
            await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<ThreadDescriptor>("CopyPiSession", import with { Title = "Changed replay" }, timeout.Token));
            await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<ThreadDescriptor>("CopyPiSession", forkRequest with { OperationId = Guid.NewGuid(), ExpectedRevision = "stale" }, timeout.Token));
            await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<PiSessionExportResult>("ExportPiSession", new ExportPiSessionRequest(fork.ThreadId, imported.PiSessionFile!, PiSessionExportFormat.Jsonl), timeout.Token));
            Assert.Equal(2, (await host.Environment.ListThreadsAsync(project.ProjectId, timeout.Token)).Count);
        }
        await using var restarted = await EmbeddedEnvironmentHost.StartAsync(options);
        Assert.Equal(imported.ThreadId, (await restarted.Environment.CopyPiSessionAsync(import, timeout.Token)).ThreadId);
        var originalState = await restarted.Environment.InspectPiSessionAsync(imported.ThreadId, timeout.Token);
        var forkState = await restarted.Environment.InspectPiSessionAsync(fork.ThreadId, timeout.Token);
        Assert.Equal(4, originalState.ActiveMessageCount);
        Assert.Equal(2, forkState.ActiveMessageCount);
        Assert.Equal("Keep the original unsent draft", (await restarted.Environment.GetThreadDraftAsync(imported.ThreadId, timeout.Token)).Text);
        Assert.Empty((await restarted.Environment.GetThreadDraftAsync(fork.ThreadId, timeout.Token)).Text);
        Assert.Equal(original, await File.ReadAllTextAsync(source, timeout.Token));
    }

    [Fact]
    public async Task InvalidImportDoesNotCreateAThreadOrOverwriteSource()
    {
        using var directory = new HostTestDirectory();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateOptions());
        var project = await host.Environment.AddProjectAsync(new(directory.CreateDirectory("project")));
        var path = directory.GetPath("bad.jsonl");
        await File.WriteAllTextAsync(path, "{invalid");
        await Assert.ThrowsAsync<InvalidDataException>(() => host.Environment.CopyPiSessionAsync(new(Guid.NewGuid(), project.ProjectId, path)));
        Assert.Empty(await host.Environment.ListThreadsAsync(project.ProjectId));
        Assert.Empty((await host.Environment.BrowsePiSessionsAsync(new(directory.Path))).Sessions);
        Assert.Equal("{invalid", await File.ReadAllTextAsync(path));
        var existing = await host.Environment.CreateThreadAsync(new(project.ProjectId));
        await File.WriteAllTextAsync(path, Fixture(project.CanonicalPath));
        await Assert.ThrowsAsync<InvalidDataException>(() => host.Environment.CopyPiSessionAsync(new(Guid.Parse(existing.ThreadId.Value), project.ProjectId, path)));
        Assert.False(File.Exists(Path.Combine(directory.CreateOptions().SessionRoot, existing.ThreadId.Value + ".jsonl")));
        Assert.Single(await host.Environment.ListThreadsAsync(project.ProjectId));
    }

    internal static string Fixture(string cwd) => new JsonObject
    {
        ["type"] = "session", ["version"] = 3, ["id"] = "22222222222222222222222222222222", ["cwd"] = cwd,
        ["timestamp"] = "2026-09-06T00:00:00Z",
    }.ToJsonString() + "\n" + """
        {"type":"model_change","id":"model","parentId":null,"provider":"fake","modelId":"fake-fast"}
        {"type":"thinking_level_change","id":"thinking","parentId":"model","thinkingLevel":"off"}
        {"type":"message","id":"u1","parentId":"thinking","timestamp":"2026-09-06T00:00:01Z","message":{"role":"user","content":"First question"}}
        {"type":"message","id":"a1","parentId":"u1","timestamp":"2026-09-06T00:00:02Z","message":{"role":"assistant","content":[{"type":"text","text":"First answer"}],"stopReason":"stop"}}
        {"type":"message","id":"u2","parentId":"a1","timestamp":"2026-09-06T00:00:03Z","message":{"role":"user","content":"Second question"}}
        {"type":"message","id":"a2","parentId":"u2","timestamp":"2026-09-06T00:00:04Z","message":{"role":"assistant","content":[{"type":"text","text":"Second answer"}],"stopReason":"stop"}}
        """ + "\n";
}
