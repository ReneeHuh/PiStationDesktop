using PiStation.App.ViewModels;
using PiStation.ClientRuntime;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.CommandSystem.Tests;

public sealed class EditingRecoveryTests
{
    [Fact]
    public void GitRefreshDetectsBranchChangesEvenWhenTheCommitIsUnchanged()
    {
        var changes = new WorkbenchChangesViewModel();
        var snapshot = new GetProjectChangesResult(ProjectId.New(), true, "main", null, 0, 0, [], false,
            StatusToken: "same", HeadSha: "same-head");
        changes.Apply(snapshot);
        Assert.True(changes.Matches(snapshot));
        Assert.False(changes.Matches(snapshot with { BranchName = "feature" }));
        Assert.False(changes.Matches(snapshot with { AheadCount = 1, UpstreamName = "origin/main" }));
    }

    [Fact]
    public void ReloadCannotOverwriteEditsMadeAfterItStarted()
    {
        var project = ProjectId.New();
        var document = new WorkbenchFileDocumentViewModel("a.cs");
        Assert.True(document.IsReadOnly);
        document.ApplyText(new(project, "a.cs", "host", 4, false, false, "r1"));
        Assert.False(document.IsReadOnly);
        var version = document.EditVersion;
        document.Content = "new local edits";
        document.ApplyTextIfUnchanged(new(project, "a.cs", "changed remotely", 16, false, false, "r2"), version);
        Assert.Equal("new local edits", document.Content);
        Assert.Equal("r1", document.Revision);
        Assert.True(document.IsDirty);
    }

    [Fact]
    public void SaveAcknowledgementKeepsEditsTypedWhileSaveWasInFlight()
    {
        var project = ProjectId.New();
        var document = new WorkbenchFileDocumentViewModel("a.cs");
        document.ApplyText(new(project, "a.cs", "original", 8, false, false, "r1"));
        document.Content = "sent";
        document.IsSaving = true;
        document.Content = "sent plus newer changes";
        document.ApplySaved(new(project, "a.cs", 4, "r2"), "sent");
        Assert.True(document.IsDirty);
        Assert.True(document.CanSave);
        Assert.Equal("r2", document.Revision);
        Assert.Equal("sent plus newer changes", document.Content);
        document.ApplySaved(new(project, "a.cs", 23, "r3"), document.Content);
        Assert.False(document.IsDirty);
    }

    [Fact]
    public async Task InactiveContextEditsAreRecoveredAndLateSaveCannotResurrectDiscardedTab()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiStation-editing-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new EditingRecoveryStore(root);
            var files = new WorkbenchFilesViewModel(store);
            files.SwitchContext("one", true);
            var document = files.OpenDocument("a.cs");
            var project = ProjectId.New();
            document.ApplyText(new(project, "a.cs", "host", 4, false, false, "r1"));
            document.Content = "local edits";
            files.SwitchContext("two", true);
            Assert.True(files.HasUnsavedChanges);
            await store.FlushAsync();

            var reopenedStore = new EditingRecoveryStore(root);
            var reopened = new WorkbenchFilesViewModel(reopenedStore);
            reopened.SetWriteAccess(false);
            reopened.SwitchContext("one", true);
            var restored = Assert.Single(reopened.OpenDocuments);
            Assert.Equal("local edits", restored.Content);
            Assert.Equal("r1", restored.Revision);
            Assert.True(restored.IsDirty);
            Assert.True(restored.IsReadOnly);
            reopened.CloseDocument(restored);
            restored.ApplySaved(new(project, "a.cs", 4, "r2"), "older edit");
            await reopenedStore.FlushAsync();
            Assert.Empty(new EditingRecoveryStore(root).LoadFiles("one"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
