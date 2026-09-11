using System.Net;
using System.Text.Json;
using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Host.SourceControl;
using PiStation.Host.Workspaces;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Serialization;
using PiStation.TestFixtures;

namespace PiStation.Host.Tests;

public sealed class BitbucketHostingTests
{
    [Fact]
    public async Task CloudListAndReviewUseRestWithBoundedSectionsAndCapabilities()
    {
        using var f = await Fixture.Create();
        var list = await f.Service.ListPullRequestsAsync(new(f.Workspace, Filters: new(Query: "quoted \" search")));
        Assert.Single(list.PullRequests);
        Assert.Contains(f.Http.Reads, path => path.Contains("q=", StringComparison.Ordinal));
        var review = await f.Read();
        Assert.Equal(BitbucketHttpFixture.Head, review.HeadCommitId);
        Assert.Single(review.Files); Assert.Single(review.Commits); Assert.Single(review.Checks); Assert.Single(review.Discussions);
        Assert.False(review.Files[0].PatchUnavailable);
        Assert.Equal(PullRequestCheckState.Passed, review.PullRequest.Checks);
        Assert.True(review.Capabilities!.InlineComments);
        Assert.Equal(3, review.Capabilities.Verdicts.Count);
        Assert.False(review.Advanced!.CanUpdateBranch);
        Assert.False(review.Advanced.CanEnableAutoMerge);
        Assert.False(review.CanReact);
        Assert.False(review.Capabilities.RemoveLabels);
    }

    [Theory]
    [InlineData("ssh://git@bitbucket.org/team/repo.git")]
    [InlineData("https://user:fixture-secret@bitbucket.org/team/repo.git")]
    public async Task DetectedCloudIdentityUsesHttpsWithoutExportingRemoteCredentials(string remote)
    {
        using var f = await Fixture.Create();
        await ProviderReviewCommands.GitAsync(f.Root, "remote", "set-url", "origin", remote);
        var snapshot = await f.Read();
        Assert.Equal("https://bitbucket.org/team/repo", snapshot.Repository.WebUrl);
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(snapshot.Repository), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PullRequestReviewEvent.Approve, "approve")]
    [InlineData(PullRequestReviewEvent.RequestChanges, "request-changes")]
    public async Task ReviewPostsInlineThenSummaryThenVerdict(PullRequestReviewEvent verdict, string last)
    {
        using var f = await Fixture.Create();
        var result = await f.Service.SubmitPullRequestReviewAsync(f.Review(verdict));
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(3, result.ReviewProgress!.CompletedSteps);
        var writes = f.Http.Writes.ToArray();
        Assert.EndsWith("/" + last, writes[^1].Path, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(writes[0].Body!);
        Assert.Equal(3, body.RootElement.GetProperty("inline").GetProperty("to").GetInt32());
    }

    [Fact]
    public async Task InterruptedReviewReceiptReplaysWithoutDuplicateWrites()
    {
        using var f = await Fixture.Create();
        f.Http.FailWrite = 2;
        var request = f.Review(); var runner = new HostingOperationRunner(f.Database);
        Task<SourceControlOperationResult> Run() => runner.RunAsync(request.OperationId, "Review",
            JsonSerializer.Serialize(request, ProtocolJsonContext.Default.SubmitPullRequestReviewRequest), ct => f.Service.SubmitPullRequestReviewAsync(request, ct));
        var result = await Run();
        Assert.Equal(CommandReceiptState.DispatchUncertain, result.State);
        Assert.Equal(1, result.ReviewProgress!.CompletedSteps);
        Assert.DoesNotContain("must-not-leak-secret", result.Message, StringComparison.Ordinal);
        var replay = await Run();
        Assert.Equal(result.ReviewProgress, replay.ReviewProgress);
        Assert.Equal(2, f.Http.Writes.Count);
    }

    [Fact]
    public async Task HeadChangeAfterInlinePreventsSummaryAndApproval()
    {
        using var f = await Fixture.Create();
        f.Http.AfterWrite = _ => f.Http.CurrentHead = "cccccccccccccccccccccccccccccccccccccccc";
        var result = await f.Service.SubmitPullRequestReviewAsync(f.Review());
        Assert.Equal(CommandReceiptState.DispatchUncertain, result.State);
        Assert.Single(f.Http.Writes);
        Assert.Equal(1, result.ReviewProgress!.CompletedSteps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BaseOrAccountChangeAtDispatchRejectsTheWholeReview(bool account)
    {
        using var f = await Fixture.Create();
        f.Http.OnDetailRead = count =>
        {
            if (count != 4) return;
            if (account) f.Http.CurrentViewer = BitbucketHttpFixture.Reviewer;
            else f.Http.CurrentBase = "cccccccccccccccccccccccccccccccccccccccc";
        };
        var result = await f.Service.SubmitPullRequestReviewAsync(f.Review());
        Assert.Equal(CommandReceiptState.Rejected, result.State);
        Assert.Empty(f.Http.Writes);
    }

    [Fact]
    public async Task PatchContentCannotImpersonateAnotherFilesHeader()
    {
        using var f = await Fixture.Create();
        f.Http.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/diff", StringComparison.Ordinal)
            ? new(HttpStatusCode.OK) { Content = new StringContent("diff --git a/other.cs b/other.cs\n--- a/other.cs\n+++ b/other.cs\n@@ -2 +3 @@\n-old\n+++ b/src/App.cs\n") } : null;
        Assert.True((await f.Read()).Files[0].PatchUnavailable);
        Assert.Equal(CommandReceiptState.Rejected, (await f.Service.SubmitPullRequestReviewAsync(f.Review())).State);
        Assert.Empty(f.Http.Writes);
    }

    [Theory]
    [InlineData("head")]
    [InlineData("repository")]
    [InlineData("line")]
    [InlineData("diff")]
    public async Task InvalidReviewTargetsAreRejectedBeforeWrites(string reason)
    {
        using var f = await Fixture.Create();
        var request = f.Review();
        if (reason == "head") request = request with { Target = request.Target with { HeadCommitId = "old" } };
        if (reason == "repository") request = request with { Target = request.Target with { Repository = "bitbucket.org/other/repo" } };
        if (reason == "line") request = request with { Comments = [new("src/App.cs", 500, PullRequestDiffSide.Right, "bad line")] };
        if (reason == "diff") f.Http.MissingDiff = true;
        var result = await f.Service.SubmitPullRequestReviewAsync(request);
        Assert.Equal(CommandReceiptState.Rejected, result.State);
        Assert.Empty(f.Http.Writes);
    }

    [Fact]
    public async Task RenamedLeftCommentUsesOldPathAndCoordinate()
    {
        using var f = await Fixture.Create(); f.Http.Renamed = true;
        var result = await f.Service.SubmitPullRequestReviewAsync(f.Review() with { Comments = [new("src/App.cs", 2, PullRequestDiffSide.Left, "old side")] });
        Assert.True(result.Succeeded, result.Message);
        using var body = JsonDocument.Parse(f.Http.Writes.First().Body!);
        Assert.Equal("src/Old.cs", body.RootElement.GetProperty("inline").GetProperty("path").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("inline").GetProperty("from").GetInt32());
    }

    [Fact]
    public async Task CommentsPageRemainsBoundToRepositoryAndBothRevisions()
    {
        using var f = await Fixture.Create(); f.Http.MoreComments = true;
        var first = await f.Read(); var page = Assert.Single(first.NextPages!);
        var second = await f.Service.GetPullRequestReviewAsync(new(f.Workspace, "7", page));
        Assert.Equal("42", Assert.Single(Assert.Single(second.Discussions).Comments).Id);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.GetPullRequestReviewAsync(new(f.Workspace, "7", page with { BaseCommitId = "old" })));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.GetPullRequestReviewAsync(new(f.Workspace, "7", page with { Cursor = "2|repositories/other/repo/pullrequests/7/comments?page=2" })));
    }

    [Theory]
    [InlineData(PullRequestManagementAction.SetDraft)]
    [InlineData(PullRequestManagementAction.RemoveLabel)]
    [InlineData(PullRequestManagementAction.EnableAutoMerge)]
    [InlineData(PullRequestManagementAction.DisableAutoMerge)]
    [InlineData(PullRequestManagementAction.UpdateBranch)]
    [InlineData(PullRequestManagementAction.Revert)]
    [InlineData(PullRequestManagementAction.SetReaction)]
    [InlineData(PullRequestManagementAction.ApproveWorkflow)]
    public async Task UnsupportedAdvancedActionsNeverDispatch(PullRequestManagementAction action)
    {
        using var f = await Fixture.Create();
        var result = await f.Service.ManagePullRequestAsync(new(f.Target, action, OperationId: CommandId.New()));
        Assert.Equal(CommandReceiptState.Rejected, result.State); Assert.Empty(f.Http.Writes);
    }

    [Fact]
    public async Task OwnCommentAndExpectedTextAreRequiredForEditing()
    {
        using var f = await Fixture.Create();
        var request = new ManagePullRequestRequest(f.Target, PullRequestManagementAction.EditComment, Body: "New text", ItemId: "41", ExpectedBody: "Please check");
        f.Http.ForeignComment = true;
        Assert.False((await f.Service.ManagePullRequestAsync(request)).Succeeded);
        f.Http.ForeignComment = false;
        Assert.False((await f.Service.ManagePullRequestAsync(request with { ExpectedBody = "stale" })).Succeeded);
        Assert.Empty(f.Http.Writes);
        Assert.True((await f.Service.ManagePullRequestAsync(request)).Succeeded);
    }

    [Fact]
    public async Task RepliesAndResolutionNameTheScopedComment()
    {
        using var f = await Fixture.Create();
        Assert.True((await f.Service.ReplyPullRequestThreadAsync(new(f.Target, "41", "Reply"))).Succeeded);
        Assert.True((await f.Service.SetPullRequestThreadResolvedAsync(new(f.Target, "41", true))).Succeeded);
        Assert.EndsWith("/comments/41/resolve", f.Http.Writes.Last().Path, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(f.Http.Writes.First().Body!);
        Assert.Equal(41, body.RootElement.GetProperty("parent").GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData(PullRequestMergeMethod.Merge, "merge_commit")]
    [InlineData(PullRequestMergeMethod.Squash, "squash")]
    [InlineData(PullRequestMergeMethod.Rebase, "rebase_fast_forward")]
    public async Task MergeUsesBitbucketStrategyWithoutDeletingSource(PullRequestMergeMethod method, string strategy)
    {
        using var f = await Fixture.Create();
        var result = await f.Service.ManagePullRequestAsync(new(f.Target, PullRequestManagementAction.Merge, MergeMethod: method));
        Assert.True(result.Succeeded, result.Message);
        using var body = JsonDocument.Parse(f.Http.Writes.Single().Body!);
        Assert.Equal(strategy, body.RootElement.GetProperty("merge_strategy").GetString());
        Assert.False(body.RootElement.GetProperty("close_source_branch").GetBoolean());
    }

    [Fact]
    public async Task CreationDeclineAndPublicationUseCloudEndpointsAndResumeWithoutCreation()
    {
        using var f = await Fixture.Create();
        Assert.False((await f.Service.CreatePullRequestAsync(new(f.Workspace, "New", "Body", IsDraft: true))).Succeeded);
        Assert.Empty(f.Http.Writes);
        Assert.True((await f.Service.CreatePullRequestAsync(new(f.Workspace, "New", "Body", SourceBranch: "feature", TargetBranch: "main"))).Succeeded);
        Assert.True((await f.Service.MutatePullRequestAsync(new(f.Workspace, "7", PullRequestMutationKind.Close))).Succeeded);
        Assert.False(HostingCapabilities.CanMutate(SourceControlProvider.Bitbucket, PullRequestMutationKind.Reopen));
        var publish = new PublishHostedRepositoryRequest(f.Workspace.ProjectId, SourceControlProvider.Bitbucket, "team", "repo");
        var created = await f.Service.PublishAsync(publish);
        Assert.True(created.Succeeded, created.Message);
        Assert.Equal(RepositoryPublicationStage.RemoteConfigured, created.Publication!.Stage);
        var writes = f.Http.Writes.Count;
        Assert.True((await f.Service.PublishAsync(publish with { ResumeExisting = true })).Succeeded);
        Assert.Equal(writes, f.Http.Writes.Count);
    }

    [Fact]
    public async Task MissingWriteAcknowledgementIsUncertain()
    {
        using var f = await Fixture.Create(); f.Http.InvalidAcknowledgement = true;
        var result = await f.Service.SubmitPullRequestReviewAsync(f.Review());
        Assert.Equal(CommandReceiptState.DispatchUncertain, result.State);
        Assert.Equal(0, result.ReviewProgress!.CompletedSteps);
    }

    [Fact]
    public async Task TransportRejectsCredentialRedirectsAndSanitizesProviderErrors()
    {
        using var http = new BitbucketHttpFixture(); using var client = http.Client();
        var calls = 0;
        http.Override = _ => { calls++; return new(HttpStatusCode.Found) { Headers = { Location = new("https://attacker.invalid/collect") } }; };
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync("repositories/team/repo/pullrequests/7/diff"));
        Assert.Equal(1, calls);
        http.Override = _ => new(HttpStatusCode.Forbidden) { Content = new StringContent("provider-secret") };
        var error = await Assert.ThrowsAsync<BitbucketResponseException>(() => client.JsonAsync("user"));
        Assert.DoesNotContain("provider-secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnonymousTransportCannotWrite()
    {
        using var http = new BitbucketHttpFixture(); using var client = http.Client(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.JsonAsync("repositories/team/repo/pullrequests", "POST", new { title = "No" }));
        Assert.Empty(http.Writes);
    }

    [Fact]
    public async Task TransportAcceptsOnlyBoundedSameRepositoryDiffRedirects()
    {
        using var http = new BitbucketHttpFixture(); using var client = http.Client();
        var count = 0;
        http.Override = _ => ++count == 1 ? new(HttpStatusCode.Found) { Headers = { Location = new("/2.0/repositories/team/repo/diff/revision", UriKind.Relative) } }
            : new(HttpStatusCode.OK) { Content = new StringContent("patch") };
        Assert.Equal("patch", await client.RequestAsync("repositories/team/repo/pullrequests/7/diff"));
        Assert.Equal(2, count);
        http.Override = _ => new(HttpStatusCode.Found) { Headers = { Location = new("https://api.bitbucket.org/2.0/repositories/other/repo/diff/revision") } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync("repositories/team/repo/pullrequests/7/diff"));
        http.Override = _ => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 2 * 1024 * 1024 + 1)) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync("repositories/team/repo/pullrequests/7/diff"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HostTestDirectory _directory = new();
        public BitbucketHttpFixture Http { get; } = new();
        public string Root { get; private set; } = "";
        public HostDatabase Database { get; private set; } = null!;
        public SourceControlHostingService Service { get; private set; } = null!;
        public WorkspaceTarget Workspace { get; private set; } = null!;
        public PullRequestReviewTarget Target => new(Workspace, "bitbucket.org/team/repo", "7", BitbucketHttpFixture.Head);
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); var root = f._directory.CreateDirectory("repo"); f.Root = root;
            await ProviderReviewCommands.GitAsync(root, "init", "--quiet", "--initial-branch=main");
            await ProviderReviewCommands.GitAsync(root, "remote", "add", "origin", "https://bitbucket.org/team/repo.git");
            f.Database = new(f._directory.CreateOptions()); await f.Database.InitializeAsync();
            var projects = new ProjectService(f.Database);
            f.Workspace = new((await projects.AddAsync(new(root))).ProjectId);
            f.Service = new(new ThreadWorkspaceResolver(f.Database), projects, bitbucket: f.Http.Client());
            return f;
        }
        public SubmitPullRequestReviewRequest Review(PullRequestReviewEvent verdict = PullRequestReviewEvent.Approve) => new(Target, verdict, "Summary", [new("src/App.cs", 3, PullRequestDiffSide.Right, "Inline")], CommandId.New());
        public Task<PullRequestReviewSnapshot> Read() => Service.GetPullRequestReviewAsync(new(Workspace, "7"));
        public void Dispose() { Service.Dispose(); _directory.Dispose(); }
    }
}
