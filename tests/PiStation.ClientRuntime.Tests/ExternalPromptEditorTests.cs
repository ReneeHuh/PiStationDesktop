using System.Text;
using PiStation.Host.Hosting;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class ExternalPromptEditorTests
{
    private static readonly string[] QuotedArguments = ["", "C:\\folder", "a\"b"];
    private static ThreadDraft Draft(string text = "Unsent original") => new(EnvironmentId.New(), ThreadId.New(), DraftId.New(), text, 1, DateTimeOffset.UtcNow, []);

    [Fact]
    public async Task RealEditorProcessRoundTripsUnicodeAndRecoversAfterRestart()
    {
        using var directory = new ClientTestDirectory();
        var root = directory.CreateDirectory("editor space & punctuation");
        var store = new ExternalPromptEditor(root);
        var draft = Draft();
        var edit = store.Create(draft);
        var preferences = new ExternalEditorPreferences(directory.CreateHostOptions().PiInstallation!.ExecutablePath, "--prompt-editor-probe");
        using (var process = store.Launch(edit, preferences))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
        }
        var restored = new ExternalPromptEditor(root);
        Assert.Equal(edit, restored.Load(draft.EnvironmentId, draft.ThreadId));
        Assert.Equal("Edited outside PiStation.\nSecond line 😀", await restored.ReadTextAsync(edit));
        Assert.Equal(preferences, restored.LoadPreferences());
        Assert.DoesNotContain(draft.Text, Encoding.UTF8.GetString(File.ReadAllBytes(Assert.Single(Directory.GetFiles(root, "*.protected")))));
        Assert.Null(restored.Load(EnvironmentId.New(), draft.ThreadId));
        Assert.Null(restored.Load(draft.EnvironmentId, ThreadId.New()));
        var another = restored.Create(Draft("Other prompt"));
        restored.Discard(edit);
        Assert.False(File.Exists(restored.GetFilePath(edit)));
        Assert.Equal("Other prompt", await restored.ReadTextAsync(another));
    }

    [Fact]
    public async Task FailedAndMissingEditorsRetainTheDraftAndHandoff()
    {
        using var directory = new ClientTestDirectory();
        var store = new ExternalPromptEditor(directory.CreateDirectory("editor"));
        var draft = Draft();
        var edit = store.Create(draft);
        Assert.Throws<System.ComponentModel.Win32Exception>(() => store.Launch(edit, new(Path.Combine(directory.Path, "missing.exe"))));
        using var process = store.Launch(edit, new(directory.CreateHostOptions().PiInstallation!.ExecutablePath, "--prompt-editor-probe --fail"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(23, process.ExitCode);
        Assert.Equal(draft.Text, await store.ReadTextAsync(edit));
        Assert.Equal(edit, store.Load(draft.EnvironmentId, draft.ThreadId));
        Assert.Equal(edit.Id, store.Create(draft with { Text = "Newer text" }).Id);
    }

    [Fact]
    public async Task TextImportPreservesLatestContextAndDetectsLocalAndHostConflicts()
    {
        using var directory = new ClientTestDirectory();
        var store = new ExternalPromptEditor(directory.CreateDirectory("editor"));
        var original = Draft("line\r\nnext");
        var edit = store.Create(original);
        var context = new ComposerContext("context", "file", "Code", "selected source");
        var latest = original with { Revision = 2, Context = [context] };
        var saves = 0;
        Task<ThreadDraft> Load(ThreadId _, CancellationToken token) => Task.FromResult(latest);
        Task<ThreadDraft> Save(ThreadDraft expected, string text, CancellationToken token)
        {
            Assert.Equal(latest.Revision, expected.Revision);
            Assert.Equal(context, Assert.Single(expected.Context!));
            saves++;
            latest = expected with { Text = text, Revision = expected.Revision + 1 };
            return Task.FromResult(latest);
        }
        var conflict = await ExternalPromptEditor.ApplyAsync(edit, "new", original with { Text = "New local work" }, Load, Save);
        Assert.False(conflict.Applied);
        Assert.Equal(0, saves);
        latest = latest with { Text = "New host work" };
        Assert.False((await ExternalPromptEditor.ApplyAsync(edit, "new", original, Load, Save)).Applied);
        latest = latest with { Text = "line\nnext" };
        var applied = await ExternalPromptEditor.ApplyAsync(edit, "new", original, Load, Save);
        Assert.True(applied.Applied);
        Assert.Equal(context, Assert.Single(applied.CurrentDraft.Context!));
        // A crash between host save and local cleanup must not save again.
        Assert.True((await ExternalPromptEditor.ApplyAsync(edit, "new", original, Load, Save)).Applied);
        Assert.Equal(1, saves);
        await File.WriteAllTextAsync(store.GetFilePath(edit), "A later editor autosave");
        var rebased = store.RecordApplied(edit, applied.CurrentDraft.Text);
        Assert.Equal("A later editor autosave", await store.ReadTextAsync(rebased));
        Assert.Equal("new", store.Load(edit.EnvironmentId, edit.ThreadId)!.OriginalText);
        Assert.False((await ExternalPromptEditor.ApplyAsync(edit, "new", original with { DraftId = DraftId.New() }, Load, Save)).Applied);
        Assert.False((await ExternalPromptEditor.ApplyAsync(edit, "new", original with { EnvironmentId = EnvironmentId.New() }, Load, Save)).Applied);
    }

    [Fact]
    public async Task ReadRejectsOversizeNullInvalidEncodingAndMissingFilesWithoutDiscardingRecovery()
    {
        using var directory = new ClientTestDirectory();
        var store = new ExternalPromptEditor(directory.CreateDirectory("editor"));
        var original = Draft();
        var edit = store.Create(original);
        var path = store.GetFilePath(edit);
        foreach (var value in new[] { new string('x', ExternalPromptEditor.MaximumTextLength + 1), "hello\0world" })
        {
            await File.WriteAllTextAsync(path, value);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadTextAsync(edit));
        }
        await File.WriteAllBytesAsync(path, [0xff, 0xff, 0xff]);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => store.ReadTextAsync(edit));
        await File.WriteAllTextAsync(path, "BOM 😀\r\n", Encoding.Unicode);
        Assert.Equal("BOM 😀\n", await store.ReadTextAsync(edit));
        File.Delete(path);
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.ReadTextAsync(edit));
        Assert.Equal(edit, store.Load(original.EnvironmentId, original.ThreadId));
    }

    [Fact]
    public void CommandArgumentsPreserveSpacesQuotesAndShellCharactersAsLiteralData()
    {
        var start = ExternalPromptEditor.CreateStartInfo(new("C:\\Program Files\\Editor\\Editor.exe", "--wait --title \"a & b\" \"C:\\work space\""), "C:\\hand off\\a & b.md");
        Assert.False(start.UseShellExecute);
        Assert.Equal("C:\\Program Files\\Editor\\Editor.exe", start.FileName);
        Assert.Equal("--wait", start.ArgumentList[0]);
        Assert.Equal("a & b", start.ArgumentList[2]);
        Assert.Equal("C:\\hand off\\a & b.md", start.ArgumentList[^1]);
        Assert.Throws<ArgumentException>(() => ExternalPromptEditor.SplitArguments("\"unterminated"));
        Assert.Equal(QuotedArguments, ExternalPromptEditor.SplitArguments("\"\" C:\\folder \"a\\\"b\""));
    }

    [Fact]
    public async Task HostRoundTripPreservesAttachmentsAndContextAndRejectsConcurrentSaves()
    {
        using var directory = new ClientTestDirectory();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions());
        await using var client = new EnvironmentClient(new ClientRuntimeOptions { HubAddress = host.HubAddress, BearerCredential = host.BearerCredential });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await client.ConnectAsync(timeout.Token);
        var project = await client.AddProjectAsync(new(directory.CreateDirectory("project")), timeout.Token);
        var thread = await client.CreateThreadAsync(new(project.ProjectId), timeout.Token);
        var draft = await client.GetThreadDraftAsync(thread.ThreadId, timeout.Token);
        var context = new ComposerContext("context", "file", "Code", "selected source");
        draft = (await client.SaveThreadDraftAsync(thread.ThreadId, draft.DraftId, draft.Revision, "Original", [context], timeout.Token)).Draft!;
        var store = new ExternalPromptEditor(directory.CreateDirectory("editor"));
        var edit = store.Create(draft);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("attachment body"));
        var attached = (await client.UploadDraftAttachmentAsync(thread.ThreadId, draft.DraftId, draft.Revision, "notes.txt", "text/plain", content, content.Length, timeout.Token)).Draft!;
        async Task<ThreadDraft> Save(ThreadDraft expected, string text, CancellationToken token) =>
            (await client.SaveThreadDraftAsync(expected.ThreadId, expected.DraftId, expected.Revision, text, expected.Context, token)).Draft!;
        var applied = await ExternalPromptEditor.ApplyAsync(edit, "Edited", draft, client.GetThreadDraftAsync, Save, timeout.Token);
        Assert.True(applied.Applied);
        Assert.Equal(attached.Attachments, applied.CurrentDraft.Attachments);
        Assert.Equal(context, Assert.Single(applied.CurrentDraft.Context!));
        Assert.Equal("Edited", (await client.GetThreadDraftAsync(thread.ThreadId, timeout.Token)).Text);
        store.Discard(edit);
        var next = store.Create(applied.CurrentDraft);
        async Task<ThreadDraft> Race(ThreadDraft expected, string text, CancellationToken token)
        {
            await Save(expected, "Concurrent update", token);
            return await Save(expected, text, token);
        }
        await Assert.ThrowsAnyAsync<Exception>(() => ExternalPromptEditor.ApplyAsync(next, "Must not overwrite", applied.CurrentDraft, client.GetThreadDraftAsync, Race, timeout.Token));
        Assert.Equal("Concurrent update", (await client.GetThreadDraftAsync(thread.ThreadId, timeout.Token)).Text);
        Assert.Equal(next, store.Load(next.EnvironmentId, next.ThreadId));
    }
}
