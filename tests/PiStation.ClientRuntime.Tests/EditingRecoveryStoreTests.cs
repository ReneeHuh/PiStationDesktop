using System.Text;

namespace PiStation.ClientRuntime.Tests;

public sealed class EditingRecoveryStoreTests
{
    [Fact]
    public async Task LatestEditsSurviveRestartAndAreProtectedAtRest()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        var root = directory.CreateDirectory("recovery");
        var store = new EditingRecoveryStore(root);
        for (var i = 0; i < 30; i++) store.SaveFile(new("project:thread", "a.cs", "private code " + i, "revision"));
        var draft = new RecoveredDraft("thread", "draft", "host text", "private unsent prompt");
        store.SaveDraft(draft);
        await store.FlushAsync();

        var reopened = new EditingRecoveryStore(root);
        Assert.Equal("private code 29", Assert.Single(reopened.LoadFiles("project:thread")).Content);
        Assert.Equal(draft, reopened.LoadDraft("thread"));
        foreach (var file in Directory.EnumerateFiles(root))
            Assert.DoesNotContain("private", Encoding.UTF8.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscardRemovesOnlyTheChosenFileOrDraftAcrossRestarts()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        var root = directory.CreateDirectory("recovery");
        var store = new EditingRecoveryStore(root);
        store.SaveFile(new("one", "a.cs", "first", "r1"));
        store.SaveFile(new("two", "a.cs", "second", "r2"));
        store.SaveDraft(new("one", "draft", "", "unsent"));
        await store.FlushAsync();
        store.RemoveFile("one", "A.cs");
        store.RemoveDraft("one");
        await store.FlushAsync();

        var reopened = new EditingRecoveryStore(root);
        Assert.Empty(reopened.LoadFiles("one"));
        Assert.Null(reopened.LoadDraft("one"));
        Assert.Equal("second", Assert.Single(reopened.LoadFiles("two")).Content);
    }

    [Fact]
    public async Task FailedFlushKeepsPendingTextAndCanBeRetried()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        var root = directory.CreateDirectory("recovery");
        var store = new EditingRecoveryStore(root);
        var draft = new RecoveredDraft("thread", "draft", "baseline", "keep me");
        store.SaveDraft(draft);
        await store.FlushAsync();
        var file = Assert.Single(Directory.EnumerateFiles(root));
        File.Delete(file);
        Directory.CreateDirectory(file);
        store.SaveDraft(draft with { Text = "newer text" });
        await Assert.ThrowsAsync<IOException>(() => store.FlushAsync());
        Assert.Equal("newer text", store.LoadDraft("thread")!.Text);
        Directory.Delete(file);
        await store.FlushAsync();
        Assert.Equal("newer text", new EditingRecoveryStore(root).LoadDraft("thread")!.Text);
    }
}
