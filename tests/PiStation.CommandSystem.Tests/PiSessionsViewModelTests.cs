using PiStation.App.ViewModels;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.CommandSystem.Tests;

public sealed class PiSessionsViewModelTests
{
    [Fact]
    public void PagingPreservesSelectionAndUnsavedLabelButChangedSearchDisablesEntryActions()
    {
        var model = new PiSessionsViewModel { AllowOperations = true };
        var first = Snapshot([new("a", null, 0, "assistant", "One", true, true, "Bookmark")]) with { NextOffset = 1, IsTruncated = true };
        model.Apply(first);
        model.SelectedEntry = model.Entries[0];
        model.EntryLabel = "Unsaved rename";
        model.Apply(first with { Entries = [new("b", "a", 1, "assistant", "Two", true, true)], NextOffset = null }, append: true);
        Assert.Equal("a", model.SelectedEntry!.Entry.Id);
        Assert.Equal("Unsaved rename", model.EntryLabel);
        Assert.True(model.CanSaveLabel);
        model.SearchQuery = "missing";
        Assert.False(model.CanNavigate);
        Assert.False(model.HasMoreEntries);
        model.Apply(first with { Entries = [], SearchQuery = "missing", NextOffset = null });
        Assert.Null(model.SelectedEntry);
        model.SearchQuery = "";
        model.Apply(first);
        Assert.Equal("a", model.SelectedEntry!.Entry.Id);
        model.ClearThread();
        model.Apply(first with { ThreadId = ThreadId.New() });
        Assert.Null(model.SelectedEntry);
    }

    [Fact]
    public void RejectsPagesFromDifferentQueriesAndRevisions()
    {
        var model = new PiSessionsViewModel();
        var first = Snapshot([]);
        model.Apply(first);
        Assert.Throws<InvalidOperationException>(() => model.Apply(first with { SearchQuery = "changed" }, append: true));
        Assert.Throws<InvalidOperationException>(() => model.Apply(first with { Revision = "changed" }, append: true));
        Assert.Throws<InvalidOperationException>(() => model.Apply(first with { Filter = PiSessionTreeFilter.LabeledOnly }, append: true));
    }

    private static PiSessionSnapshot Snapshot(PiSessionTreeEntry[] entries) => new(ThreadId.New(), "session.jsonl", "revision", "b", entries,
        2, 2, null, null, null, null, false, Filter: PiSessionTreeFilter.Default);
}
