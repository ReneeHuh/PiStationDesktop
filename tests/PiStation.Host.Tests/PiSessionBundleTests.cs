using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiStation.Host.Persistence;
using PiStation.Host.Hosting;
using PiStation.Host.Sessions;
using PiStation.PiRpc.Sessions;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Tests;

public sealed class PiSessionBundleTests
{
    [Fact]
    public async Task ImportedBundleRemapsMessageAndCitationReferencesAndSurvivesFork()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions();
        var originalThread = ThreadId.New(); var messageId = Guid.NewGuid().ToString("N");
        var source = directory.GetPath("source.jsonl");
        var prompt = SentMessageReference.Append("Question", messageId);
        await File.WriteAllTextAsync(source,
            new JsonObject { ["type"] = "session", ["version"] = 3, ["id"] = originalThread.Value, ["cwd"] = directory.Path }.ToJsonString() + "\n" +
            new JsonObject { ["type"] = "message", ["id"] = "u1", ["parentId"] = null,
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = prompt } }.ToJsonString() + "\n");
        var file = directory.GetPath("attachment.txt"); await File.WriteAllTextAsync(file, "attachment bytes");
        var bytes = await File.ReadAllBytesAsync(file);
        var attachment = new DraftAttachment(EnvironmentId.New(), originalThread, DraftId.New(), AttachmentId.New(), "attachment.txt", "text/plain", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)), file, DateTimeOffset.UtcNow);
        var bundle = directory.GetPath("portable.zip");
        await PiSessionBundle.ExportAsync(bundle, source, await PiSessionDocument.ReadAsync(source),
            [new(messageId, "Question", [attachment], [new("context", "quote", "Earlier text", "Quoted text", originalThread, Comment: "Keep this comment")])]);
        File.Delete(file);
        await using var host = await EmbeddedEnvironmentHost.StartAsync(options);
        var project = await host.Environment.AddProjectAsync(new(directory.CreateDirectory("project")));
        var request = new CopyPiSessionRequest(Guid.NewGuid(), project.ProjectId, bundle);
        var imported = await host.Environment.CopyPiSessionAsync(request);
        var database = new HostDatabase(options); await database.InitializeAsync();
        var sent = Assert.Single(await database.ListSentMessagesAsync(imported.ThreadId)).Value;
        Assert.NotEqual(messageId, sent.Id);
        var document = await PiSessionDocument.ReadAsync(imported.PiSessionFile!);
        Assert.Equal(sent.Id, SentMessageReference.Read(document.Entries[0]["message"]!["content"]!.GetValue<string>()));
        Assert.Equal(imported.ThreadId, Assert.Single(sent.Citations).SourceThreadId);
        Assert.Equal("Keep this comment", Assert.Single(sent.Citations).Comment);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(sent.Attachments).ServerPath));
        Assert.Equal(imported.ThreadId, (await host.Environment.CopyPiSessionAsync(request)).ThreadId);
        var fork = await host.Environment.CopyPiSessionAsync(new(Guid.NewGuid(), project.ProjectId, SourceThreadId: imported.ThreadId, ExpectedRevision: document.Revision));
        var forkSent = Assert.Single(await database.ListSentMessagesAsync(fork.ThreadId)).Value;
        Assert.NotEqual(sent.Id, forkSent.Id); Assert.Equal(fork.ThreadId, Assert.Single(forkSent.Citations).SourceThreadId);
        Assert.True(File.Exists(Assert.Single(forkSent.Attachments).ServerPath));
    }

    [Fact]
    public async Task BundleRoundTripKeepsAttachmentsAndCitationCommentsAfterOriginalFilesAreGone()
    {
        using var directory = new HostTestDirectory();
        var source = directory.GetPath("session.jsonl");
        var id = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(source, new JsonObject { ["type"] = "session", ["version"] = 3, ["id"] = id, ["cwd"] = directory.Path }.ToJsonString() + "\n");
        var bytes = new byte[] { 1, 2, 3, 4 };
        var file = directory.GetPath("video.mp4");
        await File.WriteAllBytesAsync(file, bytes);
        var attachment = new DraftAttachment(EnvironmentId.New(), ThreadId.New(), DraftId.New(), AttachmentId.New(), "video.mp4", "video/mp4", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)), file, DateTimeOffset.UtcNow);
        var content = new SentMessageContent(id, "Prompt", [attachment], [new("citation", "file", "code", "source", Comment: "Please change this")]);
        var bundle = directory.GetPath("bundle.zip");
        await PiSessionBundle.ExportAsync(bundle, source, await PiSessionDocument.ReadAsync(source), [content]);
        File.Delete(file);
        var imported = await PiSessionBundle.ImportAsync(bundle, directory.CreateDirectory("imported"));
        Assert.Equal(id, imported.Document.SessionId);
        var sent = Assert.Single(imported.Messages);
        Assert.Equal("Please change this", Assert.Single(sent.Citations).Comment);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(sent.Attachments).ServerPath));
        using var archive = ZipFile.OpenRead(bundle);
        Assert.NotNull(archive.GetEntry("transcript.html"));
    }

    [Fact]
    public async Task ImportRejectsAttachmentPathTraversal()
    {
        using var directory = new HostTestDirectory();
        var path = directory.GetPath("malicious.zip");
        var attachment = new DraftAttachment(EnvironmentId.New(), ThreadId.New(), DraftId.New(), AttachmentId.New(), "file.txt", "text/plain", 0,
            Convert.ToHexString(SHA256.HashData([])), "../../outside.txt", DateTimeOffset.UtcNow);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("session.jsonl").Open()))
                writer.WriteLine(new JsonObject { ["type"] = "session", ["version"] = 3, ["id"] = Guid.NewGuid().ToString("N"), ["cwd"] = directory.Path }.ToJsonString());
            using var manifest = new StreamWriter(archive.CreateEntry("sent-content.json").Open());
            manifest.Write(JsonSerializer.Serialize(new[] { new SentMessageContent(Guid.NewGuid().ToString("N"), "", [attachment], []) }, ProtocolJsonContext.Default.SentMessageContentArray));
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => PiSessionBundle.ImportAsync(path, directory.CreateDirectory("imported")));
        Assert.False(File.Exists(directory.GetPath("outside.txt")));
    }
}
