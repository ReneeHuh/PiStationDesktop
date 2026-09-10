using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class PullRequestInboxTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-10T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static SourceControlRepository Repository(SourceControlProvider provider = SourceControlProvider.GitHub, string name = "repo") =>
        new(provider, provider == SourceControlProvider.GitHub ? "github.com" : "gitlab.com", "owner", name,
            $"https://{(provider == SourceControlProvider.GitHub ? "github.com" : "gitlab.com")}/owner/{name}", "remote", "main", true);
    private static PullRequestDescriptor Pr(SourceControlRepository repo, string number = "1", string title = "Fix build", DateTimeOffset? updated = null) =>
        new(repo.Provider, $"{repo.Owner}/{repo.Name}", number, title, repo.WebUrl + "/pull/" + number, PullRequestState.Open,
            "alice", "fix/build", "main", false, ["bug", "backend"], [], PullRequestCheckState.Unknown, updated ?? Now);
    private static ProjectDescriptor Project(string environment, string id = "project") => new(new(environment), new(id), "C:/repo", id, Now);
    private static PullRequestInboxSource Source(string environment, SourceControlRepository repo,
        Func<ListPullRequestsRequest, CancellationToken, Task<ListPullRequestsResult>>? list = null,
        Func<DetectSourceControlRequest, CancellationToken, Task<SourceControlRepository>>? detect = null,
        IReadOnlyList<ProjectDescriptor>? projects = null) =>
        new(environment, environment, _ => Task.FromResult(projects ?? (IReadOnlyList<ProjectDescriptor>)[Project(environment)]),
            detect ?? ((_, _) => Task.FromResult(repo)), list ?? ((_, _) => Task.FromResult(new ListPullRequestsResult(repo, [Pr(repo)]))));

    [Fact]
    public async Task SameRepositoryAndNumberInDifferentEnvironmentsKeepSeparateTargets()
    {
        var repo = Repository();
        var page = await PullRequestInbox.StartAsync([Source("local", repo), Source("remote", repo)], new());
        Assert.Equal(2, page.Rows.Count);
        Assert.NotEqual(page.Rows[0].Key, page.Rows[1].Key);
        Assert.All(page.Rows, row => { Assert.Equal(new ProjectId("project"), row.Target.ProjectId); Assert.Null(row.Target.ThreadId); });
        Assert.Contains(page.Rows, row => row.SourceId == "remote");
    }

    [Fact]
    public async Task FailedPageRetainsItsCursorAndDoesNotDiscardOrReloadSuccessfulRepositories()
    {
        var repo = Repository();
        var goodCalls = 0;
        var attempts = new List<int>();
        var fail = true;
        var good = Source("good", repo, (_, _) => { goodCalls++; return Task.FromResult(new ListPullRequestsResult(repo, [Pr(repo)])); });
        var flaky = Source("flaky", repo, (request, _) =>
        {
            attempts.Add(request.Offset);
            if (request.Offset == 100 && fail) throw new IOException("secret token must not be displayed");
            return Task.FromResult(new ListPullRequestsResult(repo, [Pr(repo, request.Offset == 0 ? "2" : "3")], request.Offset == 0 ? 100 : null));
        });
        var page = await PullRequestInbox.StartAsync([good, flaky], new());
        page = await PullRequestInbox.ContinueAsync(page);
        Assert.Equal(2, page.Rows.Count);
        Assert.True(page.HasFailures);
        Assert.DoesNotContain("secret", page.Summary, StringComparison.OrdinalIgnoreCase);
        fail = false;
        page = await PullRequestInbox.ContinueAsync(page, retryFailures: true);
        Assert.Equal(3, page.Rows.Count);
        Assert.False(page.HasFailures);
        Assert.Equal([0, 100, 100], attempts);
        Assert.Equal(1, goodCalls);
    }

    [Fact]
    public async Task EmptyLocallyFilteredPageStillHasContinuationAndLaterMatch()
    {
        var repo = Repository(SourceControlProvider.GitLab);
        var source = Source("local", repo, (request, _) =>
        {
            Assert.Null(request.Filters);
            return Task.FromResult(new ListPullRequestsResult(repo,
                [Pr(repo, title: request.Offset == 0 ? "Unrelated" : "Needle")], request.Offset == 0 ? 100 : null));
        });
        var page = await PullRequestInbox.StartAsync([source], new(Filters: new(Query: "needle")));
        Assert.Empty(page.Rows);
        Assert.True(page.HasMore);
        page = await PullRequestInbox.ContinueAsync(page);
        Assert.Single(page.Rows);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task UnsupportedFiltersExcludeOnlyAffectedProviders()
    {
        var gh = Repository(); var gl = Repository(SourceControlProvider.GitLab);
        var filters = new PullRequestListFilters(Involvement: PullRequestInvolvement.Authored);
        var github = Source("github", gh, (request, _) => { Assert.Same(filters, request.Filters); return Task.FromResult(new ListPullRequestsResult(gh, [Pr(gh)])); });
        var gitlab = Source("gitlab", gl, (_, _) => throw new InvalidOperationException("Must not send an unsupported filter"));
        var page = await PullRequestInbox.StartAsync([github, gitlab], new(Filters: filters));
        Assert.Single(page.Rows);
        Assert.Contains("require GitHub", page.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangedRepositoryStopsContinuationWithoutDispatchingList()
    {
        var repo = Repository(); var changed = false; var lists = 0;
        var source = Source("local", repo, (_, _) => { lists++; return Task.FromResult(new ListPullRequestsResult(repo, [Pr(repo)], 100)); },
            (_, _) => Task.FromResult(changed ? Repository(name: "other") : repo));
        var page = await PullRequestInbox.StartAsync([source], new());
        changed = true;
        page = await PullRequestInbox.ContinueAsync(page);
        Assert.Equal(1, lists);
        Assert.Single(page.Rows);
        Assert.False(page.HasMore);
        Assert.Contains("repository changed", page.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-100)]
    public async Task InvalidProviderContinuationCannotLoop(int next)
    {
        var repo = Repository();
        var page = await PullRequestInbox.StartAsync([Source("local", repo, (_, _) => Task.FromResult(new ListPullRequestsResult(repo, [Pr(repo)], next)))], new());
        Assert.False(page.HasMore);
        Assert.True(page.HasFailures);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public async Task DuplicateRowsUpdateAndHaveStableRecencyOrder()
    {
        var repo = Repository();
        var source = Source("local", repo, (request, _) => Task.FromResult(request.Offset == 0
            ? new ListPullRequestsResult(repo, [Pr(repo), Pr(repo, "2", updated: Now.AddHours(1))], 100)
            : new ListPullRequestsResult(repo, [Pr(repo, title: "Updated", updated: Now.AddHours(2))])));
        var initial = await PullRequestInbox.StartAsync([source], new());
        var continued = await PullRequestInbox.ContinueAsync(initial);
        Assert.Equal(2, continued.Rows.Count);
        Assert.Equal("Updated", continued.Rows[0].PullRequest.Title);
        Assert.Equal("Fix build", initial.Rows.Single(row => row.PullRequest.Number == "1").PullRequest.Title);
    }

    [Fact]
    public async Task CancellationPropagatesInsteadOfBecomingProviderFailure()
    {
        var repo = Repository();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = Source("local", repo, async (_, token) =>
        {
            started.SetResult(); await Task.Delay(Timeout.Infinite, token); return new(repo, []);
        });
        var read = PullRequestInbox.StartAsync([source], new(), cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Fact]
    public async Task RepositoryReadsHaveBoundedConcurrency()
    {
        var repo = Repository(); var active = 0; var maximum = 0;
        var source = Source("local", repo, async (_, token) =>
        {
            var now = Interlocked.Increment(ref active);
            int old;
            do { old = maximum; } while (now > old && Interlocked.CompareExchange(ref maximum, now, old) != old);
            await Task.Delay(10, token); Interlocked.Decrement(ref active); return new(repo, []);
        }, projects: Enumerable.Range(0, 16).Select(index => Project("local", index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray());
        await PullRequestInbox.StartAsync([source], new());
        Assert.InRange(maximum, 2, 4);
    }

    [Fact]
    public async Task PageLimitEndsWithAnExplicitNotice()
    {
        var repo = Repository();
        var source = Source("local", repo, (request, _) => Task.FromResult(new ListPullRequestsResult(repo, [Pr(repo)], request.Offset + 100)));
        var page = await PullRequestInbox.StartAsync([source], new());
        for (var i = 1; i < PullRequestInbox.MaximumPages; i++) page = await PullRequestInbox.ContinueAsync(page);
        Assert.False(page.HasMore);
        Assert.Contains("Reached 10 pages", page.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnvironmentAndProviderScopesDoNotDispatchExcludedRepositoryLists()
    {
        var repo = Repository();
        var excluded = Source("excluded", repo) with { ListProjects = _ => throw new InvalidOperationException("Must not discover") };
        var selected = Source("selected", repo, (_, _) => throw new InvalidOperationException("Must not list"));
        var page = await PullRequestInbox.StartAsync([excluded, selected], new(Provider: SourceControlProvider.GitLab, Environment: "selected"));
        Assert.Empty(page.Rows);
        Assert.False(page.HasFailures);
    }

    [Fact]
    public void LocalFilterLabelsDraftAuthorAndStateHaveExplicitSemantics()
    {
        var pr = Pr(Repository(SourceControlProvider.GitLab)) with { IsDraft = true, State = PullRequestState.Draft };
        Assert.True(PullRequestInbox.Matches(pr, PullRequestState.Open, new(Author: "ALICE", LabelGroups: [["BUG", "enhancement"], ["backend"]])));
        Assert.False(PullRequestInbox.Matches(pr, PullRequestState.Open, new(Draft: PullRequestDraftFilter.Hide)));
        Assert.False(PullRequestInbox.Matches(pr, null, new(ExcludedLabels: ["BUG"])));
        Assert.False(PullRequestInbox.Matches(pr, PullRequestState.Closed, new()));
    }

    [Fact]
    public void AzureLabelFilterIsUnsupportedRatherThanAFakeEmptyResult()
    {
        Assert.NotNull(PullRequestInbox.FilterLimitation(SourceControlProvider.AzureDevOps, new(LabelGroups: [["bug"]])));
        Assert.NotNull(PullRequestInbox.FilterLimitation(SourceControlProvider.AzureDevOps, new(ExcludedLabels: ["bug"])));
        Assert.Null(PullRequestInbox.FilterLimitation(SourceControlProvider.GitLab, new(LabelGroups: [["bug"]])));
    }
}
