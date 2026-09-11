using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class ThreadNavigationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private static ThreadDescriptor Thread(ProjectId project, string title) =>
        new(EnvironmentId.New(), ThreadId.New(), project, title, "session", null, Now, Now);

    [Fact]
    public void AllProjectsShowsOtherProjectsAndGroupFilterIncludesCheckouts()
    {
        var project = ProjectId.New();
        var checkout = ProjectId.New();
        var threads = new[] { Thread(project, "main"), Thread(checkout, "checkout"), Thread(ProjectId.New(), "other") };
        Assert.Equal(3, ThreadNavigation.Select(threads, [], null, "", ThreadInboxShelf.Active, false, Now).Count);
        var filtered = ThreadNavigation.Select(threads, [], new HashSet<ProjectId> { project, checkout }, "", ThreadInboxShelf.Active, false, Now);
        Assert.Equal(2, filtered.Count);
        Assert.DoesNotContain(filtered, thread => thread.Title == "other");
    }

    [Fact]
    public void NewerLiveArchiveWinsBeforeShelfAndSearchFiltering()
    {
        var original = Thread(ProjectId.New(), "old title");
        var archived = original with { Revision = original.Revision + 1, IsArchived = true, Title = "new title" };
        Assert.Empty(ThreadNavigation.Select([original], [archived], null, "old", ThreadInboxShelf.Active, false, Now));
        Assert.Equal(archived, Assert.Single(ThreadNavigation.Select([original], [archived], null, "NEW", ThreadInboxShelf.Archived, false, Now)));
    }

    [Fact]
    public void SearchMatchesBranchWithoutLeakingUnrelatedLiveRows()
    {
        var branch = Thread(ProjectId.New(), "task") with { BranchName = "feature/sidebar" };
        var unrelated = Thread(ProjectId.New(), "unrelated");
        Assert.Equal(branch, Assert.Single(ThreadNavigation.Select([branch], [unrelated], null, " SIDEBAR ", ThreadInboxShelf.Active, false, Now)));
    }
}
