using System.Text;
using PiStation.PiRpc.Sessions;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class PiSessionTreePresentationTests
{
    [Fact]
    public void FilterReparentsRowsAndFoldingAndBranchJumpsWorkAcrossPages()
    {
        var document = PiSessionDocument.Parse(Encoding.UTF8.GetBytes(PiSessionIntegrationTests.Fixture("C:/project") + "\n" + """{"type":"message","id":"u3","parentId":"a1","message":{"role":"user","content":"Other branch"}}"""));
        var id = ThreadId.New();
        var all = EnvironmentService.CreateSessionSnapshot(id, "test.jsonl", document);
        var branch = Assert.Single(all.Entries, entry => entry.IsBranchPoint);
        var folded = EnvironmentService.CreateSessionSnapshot(id, "test.jsonl", document, collapsedEntryIds: [branch.Id]);
        Assert.True(folded.Entries.Count < all.Entries.Count);
        Assert.DoesNotContain(folded.Entries, entry => entry.VisibleParentId == branch.Id);
        var jump = EnvironmentService.CreateSessionSnapshot(id, "test.jsonl", document, limit: 1,
            anchorEntryId: all.Entries[0].Id, branchDirection: 1);
        Assert.Equal(branch.Id, jump.SelectedEntryId);
        Assert.Equal(branch.Id, Assert.Single(jump.Entries).Id);
        var users = EnvironmentService.CreateSessionSnapshot(id, "test.jsonl", document, filter: PiSessionTreeFilter.UserOnly);
        Assert.Equal(0, users.Entries[0].Depth);
        Assert.Null(users.Entries[0].VisibleParentId);
        Assert.All(users.Entries.Skip(1), entry => Assert.Equal(users.Entries[0].Id, entry.VisibleParentId));
        var copy = EnvironmentService.CreateSessionSnapshot(id, "test.jsonl", document, anchorEntryId: "a1", includeEntryText: true);
        Assert.False(string.IsNullOrWhiteSpace(copy.SelectedEntryText));
    }
}
