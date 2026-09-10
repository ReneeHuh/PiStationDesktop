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

public sealed class ProviderPullRequestReviewTests
{
    [Theory]
    [InlineData(SourceControlProvider.GitLab)]
    [InlineData(SourceControlProvider.AzureDevOps)]
    public async Task ProviderDetailsExposeOnlySupportedReviewAndAdvancedControls(SourceControlProvider provider)
    {
        using var fixture = await Fixture.Create(provider);
        var value = await fixture.Read();
        Assert.Equal("Review fixture", value.PullRequest.Title);
        Assert.Single(value.Discussions);
        Assert.True(value.CanEditDetails);
        Assert.True(value.Advanced!.CanMerge);
        Assert.True(value.Advanced.CanEnableAutoMerge);
        Assert.False(value.Advanced.CanRevert);
        Assert.False(value.Advanced.CanApproveWorkflows);
        if (provider == SourceControlProvider.GitLab)
        {
            Assert.True(value.Capabilities!.Diff);
            Assert.Equal([PullRequestReviewEvent.Comment, PullRequestReviewEvent.Approve], value.Capabilities.Verdicts);
            Assert.Equal([PullRequestUpdateMethod.Rebase], value.Advanced.UpdateMethods);
            Assert.Equal(PullRequestCheckState.Passed, value.PullRequest.Checks);
            Assert.Single(value.Checks); // Older pipeline failure must not become this head's failure.
            Assert.Equal(["alice", "bob"], value.PullRequest.Reviewers);
            Assert.True(Assert.Single(value.Discussions[0].Comments).CanReact);
        }
        else
        {
            Assert.Empty(value.Files);
            Assert.Empty(value.Capabilities!.Verdicts);
            Assert.False(value.Capabilities.Diff);
            Assert.False(value.Advanced.CanUpdateBranch);
            Assert.False(value.Discussions[0].CanReply);
            Assert.Contains("read-only", value.Notice);
            Assert.Contains("/station/", PullRequestReviewDefaults.RepositoryKey(value.Repository));
        }
        Assert.All(fixture.Commands.Calls, call => Assert.Contains(provider == SourceControlProvider.GitLab ? "--hostname" : "--organization", call.Arguments));
    }

    [Fact]
    public async Task GitLabPaginationIsBoundToRepositoryHeadBaseAndPageKind()
    {
        using var fixture = await Fixture.Create();
        fixture.Commands.FileCount = 101;
        var first = await fixture.Read();
        var cursor = Assert.Single(first.NextPages!);
        Assert.Equal(100, first.Files.Count);
        var second = await fixture.Service.GetPullRequestReviewAsync(new(fixture.Workspace, "7", cursor));
        Assert.Equal("src/File100.cs", Assert.Single(second.Files).Path);
        Assert.Empty(second.NextPages!);
        foreach (var forged in new[] { cursor with { Repository = "other/repo" }, cursor with { HeadCommitId = "other" }, cursor with { BaseCommitId = "other" }, cursor with { Kind = PullRequestReviewPageKind.Reviews }, cursor with { Cursor = "-1" }, cursor with { Cursor = "31" } })
            await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.GetPullRequestReviewAsync(new(fixture.Workspace, "7", forged)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitLabReviewPinsRenameAndContextPositionsAndApprovesLast(bool context)
    {
        using var fixture = await Fixture.Create();
        fixture.Commands.ContextDiff = context;
        fixture.Commands.Renamed = true;
        var result = await fixture.Service.SubmitPullRequestReviewAsync(fixture.Review(PullRequestReviewEvent.Approve));
        Assert.True(result.Succeeded, result.Message);
        var writes = fixture.Commands.Writes.ToArray();
        Assert.Equal(3, writes.Length);
        Assert.EndsWith("/discussions", writes[0].Path);
        using var payload = JsonDocument.Parse(writes[0].Input!);
        var position = payload.RootElement.GetProperty("position");
        Assert.Equal("src/Old.cs", position.GetProperty("old_path").GetString());
        Assert.Equal(ProviderReviewCommands.Head, position.GetProperty("head_sha").GetString());
        Assert.Equal(3, position.GetProperty("new_line").GetInt32());
        Assert.Equal(context, position.TryGetProperty("old_line", out _));
        Assert.EndsWith("/notes", writes[1].Path);
        Assert.EndsWith("/approve", writes[2].Path);
        Assert.Equal(new PullRequestWriteProgress(3, 3, "Approval"), result.ReviewProgress);
    }

    [Fact]
    public async Task PartiallySubmittedReviewHasDurableProgressAndIsNeverReplayed()
    {
        using var fixture = await Fixture.Create();
        fixture.Commands.FailedWrite = 2;
        var request = fixture.Review(PullRequestReviewEvent.Approve);
        var runner = new HostingOperationRunner(fixture.Database);
        Task<SourceControlOperationResult> Run() => runner.RunAsync(request.OperationId, "Review", JsonSerializer.Serialize(request, ProtocolJsonContext.Default.SubmitPullRequestReviewRequest), ct => fixture.Service.SubmitPullRequestReviewAsync(request, ct));
        var first = await Run();
        Assert.Equal(CommandReceiptState.DispatchUncertain, first.State);
        Assert.Equal(1, first.ReviewProgress!.CompletedSteps);
        Assert.Equal(3, first.ReviewProgress.TotalSteps);
        Assert.DoesNotContain("secret", first.Message);
        Assert.Equal(JsonSerializer.Serialize(first, ProtocolJsonContext.Default.SourceControlOperationResult), JsonSerializer.Serialize(await Run(), ProtocolJsonContext.Default.SourceControlOperationResult));
        Assert.Equal(2, fixture.Commands.Writes.Count);
        Assert.DoesNotContain(fixture.Commands.Writes, write => write.Path.EndsWith("/approve", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("head")]
    [InlineData("base")]
    [InlineData("foreign")]
    [InlineData("collapsed")]
    [InlineData("verdict")]
    [InlineData("identity")]
    public async Task InvalidOrUnreviewableTargetsAreRejectedBeforeAnyWrite(string kind)
    {
        using var fixture = await Fixture.Create();
        var request = fixture.Review();
        if (kind == "head") fixture.Commands.CurrentHead = "different";
        if (kind == "base") fixture.Commands.OnDetailRead = count => { if (count == 2) fixture.Commands.CurrentBase = "different"; };
        if (kind == "foreign") fixture.Commands.ForeignRepository = true;
        if (kind == "collapsed") fixture.Commands.Collapsed = true;
        if (kind == "verdict") request = request with { Event = PullRequestReviewEvent.RequestChanges };
        if (kind == "identity") fixture.Commands.MissingViewer = true;
        var result = await fixture.Service.SubmitPullRequestReviewAsync(request);
        Assert.False(result.Succeeded);
        Assert.Equal(CommandReceiptState.Rejected, result.State);
        Assert.Empty(fixture.Commands.Writes);
    }

    [Fact]
    public async Task RevisionChangeAfterFirstCommentStopsBeforeSummaryAndApproval()
    {
        using var fixture = await Fixture.Create();
        fixture.Commands.OnWrite = _ => fixture.Commands.CurrentHead = "changed";
        var result = await fixture.Service.SubmitPullRequestReviewAsync(fixture.Review(PullRequestReviewEvent.Approve));
        Assert.Equal(CommandReceiptState.DispatchUncertain, result.State);
        Assert.Equal(1, result.ReviewProgress!.CompletedSteps);
        Assert.Single(fixture.Commands.Writes);
    }

    [Fact]
    public async Task MissingOptionalReadIsReportedWithoutErasingTheOtherDetails()
    {
        using var fixture = await Fixture.Create();
        fixture.Commands.FailedDiscussionRead = true;
        var value = await fixture.Read();
        Assert.Single(value.Files);
        Assert.Empty(value.Discussions);
        Assert.True(value.IsTruncated);
        Assert.Contains("Threads could not be loaded", value.Notice);
    }

    [Fact]
    public async Task GitLabRepliesAndResolutionValidateMembershipAndSupportIdempotentState()
    {
        using var fixture = await Fixture.Create();
        var reply = await fixture.Service.ReplyPullRequestThreadAsync(new(fixture.Target(), "thread1", "true", CommandId.New()));
        Assert.True(reply.Succeeded, reply.Message);
        Assert.Contains("\"body\":\"true\"", Assert.Single(fixture.Commands.Writes).Input);
        var resolved = await fixture.Service.SetPullRequestThreadResolvedAsync(new(fixture.Target(), "thread1", true, CommandId.New()));
        Assert.True(resolved.Succeeded, resolved.Message);
        fixture.Commands.Resolved = true;
        Assert.True((await fixture.Service.SetPullRequestThreadResolvedAsync(new(fixture.Target(), "thread1", true, CommandId.New()))).Succeeded);
        Assert.Equal(2, fixture.Commands.Writes.Count);
        fixture.Commands.WrongThread = true;
        Assert.False((await fixture.Service.ReplyPullRequestThreadAsync(new(fixture.Target(), "thread1", "hello", CommandId.New()))).Succeeded);
        Assert.Equal(2, fixture.Commands.Writes.Count);
    }

    [Theory]
    [InlineData(SourceControlProvider.GitLab)]
    [InlineData(SourceControlProvider.AzureDevOps)]
    public async Task DetailsEditsUseJsonAndRejectConcurrentTextChanges(SourceControlProvider provider)
    {
        using var fixture = await Fixture.Create(provider);
        var request = new ManagePullRequestRequest(fixture.Target(), PullRequestManagementAction.EditDetails,
            Title: "--literal title", Body: "true\nsecond line", ExpectedTitle: "Review fixture", ExpectedBody: "Description", OperationId: CommandId.New());
        var result = await fixture.Service.ManagePullRequestAsync(request);
        Assert.True(result.Succeeded, result.Message);
        using var payload = JsonDocument.Parse(Assert.Single(fixture.Commands.Writes).Input!);
        Assert.Equal("--literal title", payload.RootElement.GetProperty("title").GetString());
        Assert.Equal("true\nsecond line", payload.RootElement.GetProperty("description").GetString());
        fixture.Commands.Title = "Changed elsewhere";
        Assert.False((await fixture.Service.ManagePullRequestAsync(request with { OperationId = CommandId.New() })).Succeeded);
        Assert.Single(fixture.Commands.Writes);
    }

    [Theory]
    [InlineData(SourceControlProvider.GitLab)]
    [InlineData(SourceControlProvider.AzureDevOps)]
    public async Task SupportedMergeAndAutoMergeActionsUseExplicitRevisionAndDestination(SourceControlProvider provider)
    {
        using var fixture = await Fixture.Create(provider);
        foreach (var action in new[] { PullRequestManagementAction.Merge, PullRequestManagementAction.EnableAutoMerge, PullRequestManagementAction.DisableAutoMerge })
        {
            fixture.Commands.AutoMerge = action == PullRequestManagementAction.DisableAutoMerge;
            var result = await fixture.Service.ManagePullRequestAsync(new(fixture.Target(), action, MergeMethod: PullRequestMergeMethod.Squash, OperationId: CommandId.New()));
            Assert.True(result.Succeeded, result.Message);
        }
        Assert.Equal(3, fixture.Commands.Writes.Count);
        Assert.Contains(ProviderReviewCommands.Head, fixture.Commands.Writes.First().Input);
        if (provider == SourceControlProvider.AzureDevOps)
        {
            var invoke = Assert.Single(fixture.Commands.Calls, call => call.Arguments.Contains("--in-file"));
            Assert.False(File.Exists(invoke.Arguments[Array.IndexOf(invoke.Arguments, "--in-file") + 1]));
            Assert.Contains("project=Team Project", invoke.Arguments);
            Assert.Contains("repositoryId=" + ProviderReviewCommands.RepositoryId, invoke.Arguments);
        }
    }

    [Theory]
    [InlineData("ff")]
    [InlineData("rebase_merge")]
    public async Task GitLabProjectRulesNarrowMergeAndUpdateMethodsAndPermissions(string method)
    {
        using var fixture = await Fixture.Create();
        fixture.Commands.MergeMethod = method;
        fixture.Commands.SquashOption = "never";
        Assert.Equal([PullRequestMergeMethod.Rebase], (await fixture.Read()).Advanced!.MergeMethods);
        Assert.False((await fixture.Service.ManagePullRequestAsync(new(fixture.Target(), PullRequestManagementAction.UpdateBranch, UpdateMethod: PullRequestUpdateMethod.Merge))).Succeeded);
        Assert.True((await fixture.Service.ManagePullRequestAsync(new(fixture.Target(), PullRequestManagementAction.UpdateBranch, UpdateMethod: PullRequestUpdateMethod.Rebase))).Succeeded);
        fixture.Commands.CanMerge = false;
        Assert.False((await fixture.Service.ManagePullRequestAsync(new(fixture.Target(), PullRequestManagementAction.EnableAutoMerge, MergeMethod: PullRequestMergeMethod.Rebase))).Succeeded);
        Assert.Single(fixture.Commands.Writes);
        fixture.Commands.MergeMethod = "unrecognized";
        fixture.Commands.SquashOption = "unrecognized";
        Assert.Empty((await fixture.Read()).Advanced!.MergeMethods);
    }

    [Fact]
    public async Task GitLabCommentEditsAndReviewerRemovalUseProviderIdentities()
    {
        using var fixture = await Fixture.Create();
        var result = await fixture.Service.ManagePullRequestAsync(new(fixture.Target(), PullRequestManagementAction.EditComment, ItemId: "41", ExpectedBody: "Please check", Body: "Updated"));
        Assert.True(result.Succeeded, result.Message);
        fixture.Commands.ForeignComment = true;
        Assert.False((await fixture.Service.ManagePullRequestAsync(new(fixture.Target(), PullRequestManagementAction.EditComment, ItemId: "41", ExpectedBody: "Please check", Body: "Updated"))).Succeeded);
        var removed = await fixture.Service.ManagePullRequestAsync(new(fixture.Target(), PullRequestManagementAction.RemoveReviewer, ItemId: "alice"));
        Assert.True(removed.Succeeded, removed.Message);
        using var payload = JsonDocument.Parse(fixture.Commands.Writes.Last().Input!);
        Assert.Equal(13, Assert.Single(payload.RootElement.GetProperty("reviewer_ids").EnumerateArray()).GetInt32());
    }

    [Theory]
    [InlineData(PullRequestManagementAction.UpdateBranch)]
    [InlineData(PullRequestManagementAction.Revert)]
    [InlineData(PullRequestManagementAction.ApproveWorkflow)]
    [InlineData(PullRequestManagementAction.EditComment)]
    [InlineData(PullRequestManagementAction.SetReaction)]
    public async Task AzureRejectsUnsupportedAdvancedActionsBeforeDispatch(PullRequestManagementAction action)
    {
        using var fixture = await Fixture.Create(SourceControlProvider.AzureDevOps);
        Assert.False((await fixture.Service.ManagePullRequestAsync(new(fixture.Target(), action))).Succeeded);
        Assert.False((await fixture.Service.SubmitPullRequestReviewAsync(fixture.Review())).Succeeded);
        Assert.Empty(fixture.Commands.Writes);
    }

    [Fact]
    public async Task GitLabReadsAwardsTogetherAndRemovesOnlyTheViewersMatchingAward()
    {
        using var fixture = await Fixture.Create();
        fixture.Commands.ReactionEnabled = true;
        var snapshot = await fixture.Read();
        Assert.Equal(1, fixture.Commands.AwardReads);
        Assert.True(Assert.Single(snapshot.Reactions!).ViewerHasReacted);
        Assert.True(Assert.Single(Assert.Single(snapshot.Discussions[0].Comments).Reactions!).ViewerHasReacted);
        Assert.DoesNotContain(fixture.Commands.Calls, call => call.Arguments.Any(argument => argument.Contains("award_emoji", StringComparison.Ordinal)));
        var result = await fixture.Service.ManagePullRequestAsync(new(fixture.Target(), PullRequestManagementAction.SetReaction,
            ItemId: "41", Reaction: PullRequestReactionContent.ThumbsUp, Reacted: false));
        Assert.True(result.Succeeded, result.Message);
        Assert.EndsWith("/notes/41/award_emoji/77", Assert.Single(fixture.Commands.Writes).Path);
    }

    [Fact]
    public async Task IncompleteAwardReadsAreBoundedAndDoNotInventAnEmptyReactionState()
    {
        using var fixture = await Fixture.Create();
        fixture.Commands.EndlessAwardPages = true;
        var snapshot = await fixture.Read();
        Assert.Equal(10, fixture.Commands.AwardReads);
        Assert.False(Assert.Single(snapshot.Discussions[0].Comments).CanReact);
        Assert.Contains("Some comment reactions", snapshot.Notice);
        fixture.Commands.TruncatedAwards = true;
        snapshot = await fixture.Read();
        Assert.False(snapshot.CanReact);
        Assert.Contains("Reactions could not be loaded", snapshot.Notice);
    }

    [Fact]
    public async Task InvalidProviderAcknowledgmentKeepsTheWriteUncertain()
    {
        using var fixture = await Fixture.Create();
        fixture.Commands.InvalidWriteResponse = true;
        var result = await fixture.Service.SubmitPullRequestReviewAsync(fixture.Review());
        Assert.Equal(CommandReceiptState.DispatchUncertain, result.State);
        Assert.Equal(0, result.ReviewProgress!.CompletedSteps);
        Assert.Single(fixture.Commands.Writes);
    }

    [Fact]
    public async Task TextChangedBetweenPreparationAndDispatchIsNotOverwritten()
    {
        using var fixture = await Fixture.Create();
        fixture.Commands.OnDetailRead = count => { if (count >= 4) fixture.Commands.Title = "Concurrent edit"; };
        var result = await fixture.Service.ManagePullRequestAsync(new(fixture.Target(), PullRequestManagementAction.EditDetails,
            Title: "Updated", Body: "Updated", ExpectedTitle: "Review fixture", ExpectedBody: "Description"));
        Assert.False(result.Succeeded);
        Assert.Empty(fixture.Commands.Writes);
    }

    [Fact]
    public async Task AzureRejectsAnotherOrganizationEvenWhenProjectRepositoryAndNumberMatch()
    {
        using var fixture = await Fixture.Create(SourceControlProvider.AzureDevOps);
        var target = fixture.Target();
        await ProviderReviewCommands.GitAsync(fixture.Root, "remote", "set-url", "origin", fixture.Commands.RemoteUrl.Replace("/station/", "/another/", StringComparison.Ordinal));
        var result = await fixture.Service.ManagePullRequestAsync(new(target, PullRequestManagementAction.Merge, MergeMethod: PullRequestMergeMethod.Merge));
        Assert.Equal(CommandReceiptState.Rejected, result.State);
        Assert.Empty(fixture.Commands.Writes);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HostTestDirectory _directory = new();
        public string Root { get; private set; } = "";
        public WorkspaceTarget Workspace { get; private set; } = null!;
        public HostDatabase Database { get; private set; } = null!;
        public ProviderReviewCommands Commands { get; private set; } = null!;
        public SourceControlHostingService Service { get; private set; } = null!;
        public SourceControlRepository Repository { get; private set; } = null!;
        public static async Task<Fixture> Create(SourceControlProvider provider = SourceControlProvider.GitLab)
        {
            var fixture = new Fixture { Commands = new(provider) };
            fixture.Root = fixture._directory.CreateDirectory("repo");
            await fixture.Commands.InitializeRepositoryAsync(fixture.Root);
            fixture.Database = new(fixture._directory.CreateOptions());
            await fixture.Database.InitializeAsync();
            var projects = new ProjectService(fixture.Database);
            fixture.Workspace = new((await projects.AddAsync(new(fixture.Root))).ProjectId);
            fixture.Service = new(new ThreadWorkspaceResolver(fixture.Database), projects, fixture.Commands.RunAsync);
            fixture.Repository = await fixture.Service.DetectAsync(new(fixture.Workspace));
            return fixture;
        }
        public Task<PullRequestReviewSnapshot> Read() => Service.GetPullRequestReviewAsync(new(Workspace, "7"));
        public PullRequestReviewTarget Target() => new(Workspace, PullRequestReviewDefaults.RepositoryKey(Repository), "7", ProviderReviewCommands.Head);
        public SubmitPullRequestReviewRequest Review(PullRequestReviewEvent verdict = PullRequestReviewEvent.Comment) => new(Target(), verdict, "Summary",
            [new("src/App.cs", 3, PullRequestDiffSide.Right, "Line comment")], CommandId.New());
        public void Dispose() { Service.Dispose(); _directory.Dispose(); }
    }
}
