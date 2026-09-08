using System.Diagnostics;
using System.Text.Json;
using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Host.SourceControl;
using PiStation.Host.Workspaces;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Tests;

public sealed class PullRequestManagementTests
{
    [Theory]
    [InlineData(PullRequestManagementAction.EditDetails, "updatePullRequest")]
    [InlineData(PullRequestManagementAction.SetDraft, "convertPullRequestToDraft")]
    [InlineData(PullRequestManagementAction.EditComment, "updateIssueComment")]
    [InlineData(PullRequestManagementAction.DeleteComment, "deleteIssueComment")]
    [InlineData(PullRequestManagementAction.RemoveLabel, "/labels/bug%2Fparser")]
    [InlineData(PullRequestManagementAction.RemoveReviewer, "/requested_reviewers")]
    [InlineData(PullRequestManagementAction.Merge, "mergePullRequest")]
    [InlineData(PullRequestManagementAction.EnableAutoMerge, "enablePullRequestAutoMerge")]
    [InlineData(PullRequestManagementAction.DisableAutoMerge, "disablePullRequestAutoMerge")]
    [InlineData(PullRequestManagementAction.UpdateBranch, "updatePullRequestBranch")]
    [InlineData(PullRequestManagementAction.Revert, "revertPullRequest")]
    [InlineData(PullRequestManagementAction.SetReaction, "addReaction")]
    [InlineData(PullRequestManagementAction.ApproveWorkflow, "/actions/runs/91/approve")]
    public async Task OperationsUseExplicitHostAndBoundPayloadAndReplayOnce(PullRequestManagementAction action, string expected)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.AutoMerge = action == PullRequestManagementAction.DisableAutoMerge;
        fixture.State = action == PullRequestManagementAction.Revert ? "MERGED" : "OPEN";
        var request = fixture.Request(action);
        var runner = new HostingOperationRunner(fixture.Database);
        var body = JsonSerializer.Serialize(request, ProtocolJsonContext.Default.ManagePullRequestRequest);
        var first = await runner.RunAsync(request.OperationId, "Manage pull request", body, token => fixture.Service.ManagePullRequestAsync(request, token));
        var replay = await runner.RunAsync(request.OperationId, "Manage pull request", body, token => fixture.Service.ManagePullRequestAsync(request, token));
        Assert.True(first.Succeeded, first.Message);
        Assert.Equal(first, replay);
        var write = Assert.Single(fixture.Writes);
        Assert.Contains("--hostname", write.Arguments);
        Assert.Contains("github.example", write.Arguments);
        Assert.Contains(expected, string.Join(" ", write.Arguments) + write.Input);
        if (action == PullRequestManagementAction.EditDetails)
        {
            using var input = JsonDocument.Parse(write.Input!);
            Assert.Equal(request.Title, input.RootElement.GetProperty("variables").GetProperty("title").GetString());
            Assert.Equal("PR_NODE", input.RootElement.GetProperty("variables").GetProperty("id").GetString());
        }
        if (action == PullRequestManagementAction.RemoveReviewer)
        {
            using var input = JsonDocument.Parse(write.Input!);
            Assert.Empty(input.RootElement.GetProperty("reviewers").EnumerateArray());
            Assert.Equal("core-team", input.RootElement.GetProperty("team_reviewers")[0].GetString());
        }
    }

    [Theory]
    [InlineData("head")]
    [InlineData("title")]
    [InlineData("body")]
    [InlineData("permission")]
    [InlineData("repository")]
    public async Task ConflictingDetailsAndPermissionsAreRejectedBeforeAnyWrite(string mismatch)
    {
        using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request(PullRequestManagementAction.EditDetails);
        request = mismatch switch
        {
            "head" => request with { Target = request.Target with { HeadCommitId = "old" } },
            "title" => request with { ExpectedTitle = "old" },
            "body" => request with { ExpectedBody = "old" },
            "repository" => request with { Target = request.Target with { Repository = "github.example/other/repo" } },
            _ => request
        };
        fixture.CanUpdate = mismatch != "permission";
        var result = await fixture.Service.ManagePullRequestAsync(request);
        Assert.False(result.Succeeded);
        Assert.Equal(CommandReceiptState.Rejected, result.State);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task DescriptionsSupportLongTextAndRejectOversizeTextBeforeWriting()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request(PullRequestManagementAction.EditDetails) with { Body = new string('x', 50_000) };
        Assert.True((await fixture.Service.ManagePullRequestAsync(request)).Succeeded);
        Assert.Single(fixture.Writes);
        var tooLong = request with { Body = new string('x', PullRequestReviewDefaults.MaximumDescriptionCharacters + 1) };
        Assert.False((await fixture.Service.ManagePullRequestAsync(tooLong)).Succeeded);
        Assert.Single(fixture.Writes);
    }

    [Theory]
    [InlineData("other-pr")]
    [InlineData("other-author")]
    [InlineData("changed-text")]
    [InlineData("no-permission")]
    [InlineData("submitted-review")]
    public async Task CommentDeletionChecksOwnershipScopePermissionAndCurrentBody(string mismatch)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Comment = Comment(
            parent: mismatch == "other-pr" ? "OTHER_PR" : "PR_NODE", own: mismatch != "other-author",
            body: mismatch == "changed-text" ? "new comment" : "original comment", canDelete: mismatch != "no-permission",
            type: mismatch == "submitted-review" ? "PullRequestReview" : "IssueComment");
        var result = await fixture.Service.ManagePullRequestAsync(fixture.Request(PullRequestManagementAction.DeleteComment));
        Assert.Equal(CommandReceiptState.Rejected, result.State);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task DraftConflictAndReadOnlyMetadataRemovalAreRejected()
    {
        using var fixture = await Fixture.CreateAsync();
        var draft = fixture.Request(PullRequestManagementAction.SetDraft) with { ExpectedIsDraft = true };
        Assert.False((await fixture.Service.ManagePullRequestAsync(draft)).Succeeded);
        fixture.Permission = "READ";
        Assert.False((await fixture.Service.ManagePullRequestAsync(fixture.Request(PullRequestManagementAction.RemoveLabel))).Succeeded);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task LostMutationResponseStaysUncertainAndIsNotDispatchedAgain()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.LoseResponse = true;
        var request = fixture.Request(PullRequestManagementAction.EditDetails);
        var runner = new HostingOperationRunner(fixture.Database);
        var body = JsonSerializer.Serialize(request, ProtocolJsonContext.Default.ManagePullRequestRequest);
        var result = await runner.RunAsync(request.OperationId, "Manage", body, token => fixture.Service.ManagePullRequestAsync(request, token));
        Assert.Equal(CommandReceiptState.DispatchUncertain, result.State);
        var replay = await runner.RunAsync(request.OperationId, "Manage", body, token => fixture.Service.ManagePullRequestAsync(request, token));
        Assert.Equal(result, replay);
        Assert.Single(fixture.Writes);
    }

    [Theory]
    [InlineData("IssueComment", "updateIssueComment", "id")]
    [InlineData("PullRequestReviewComment", "updatePullRequestReviewComment", "pullRequestReviewCommentId")]
    [InlineData("PullRequestReview", "updatePullRequestReview", "pullRequestReviewId")]
    public void CommentKindsUseTheirCorrectMutation(string type, string mutationName, string idField)
    {
        var request = new ManagePullRequestRequest(new(new(ProjectId.New()), "github.example/owner/repo", "42", "head"),
            PullRequestManagementAction.EditComment, Body: "new", ItemId: "COMMENT", ExpectedBody: "original comment");
        var mutation = SourceControlHostingService.BuildManagementMutation(request, Repository(), Comment(type: type));
        Assert.Contains(mutationName, mutation.Query);
        Assert.Contains(idField + ":$id", mutation.Query);
    }

    [Fact]
    public void ParsingDoesNotOfferDeletingAnotherAuthorsCommentOrSubmittedReview()
    {
        Assert.False(SourceControlHostingService.ParseManagementComment(Comment(own: false), PullRequestCommentKind.General).CanDelete);
        var review = SourceControlHostingService.ParseManagementComment(Comment(type: "PullRequestReview"), PullRequestCommentKind.Review);
        Assert.True(review.CanEdit);
        Assert.False(review.CanDelete);
    }

    [Fact]
    public async Task LinkedThreadStatusRefreshPreservesDraftAndRejectsOldOrUnrelatedResults()
    {
        using var fixture = await Fixture.CreateAsync();
        var project = fixture.Request(PullRequestManagementAction.EditDetails).Target.Workspace.ProjectId;
        var thread = await fixture.Database.CreateThreadAsync(project, "Review task");
        var now = DateTimeOffset.UtcNow;
        var link = new PullRequestLink(SourceControlProvider.GitHub, "owner/repo", "42", "https://github.example/owner/repo/pull/42", "Open", "Old title", now.AddMinutes(-2));
        await fixture.Database.UpdateThreadInboxAsync(thread.ThreadId, 0, pullRequest: link, updatePullRequest: true);
        var draft = await fixture.Database.GetOrCreateThreadDraftAsync(thread.ThreadId);
        await fixture.Database.UpdateThreadDraftAsync(thread.ThreadId, draft.DraftId, draft.Revision, "Keep local prompt");
        var current = new PullRequestDescriptor(link.Provider, link.Repository, link.Number, "Updated title", link.Url,
            PullRequestState.Merged, "author", "feature", "main", false, [], [], PullRequestCheckState.Passed, now);
        await fixture.Database.RefreshPullRequestLinksAsync(project, current, CancellationToken.None);
        await fixture.Database.RefreshPullRequestLinksAsync(project, current with { Title = "Stale title", UpdatedUtc = now.AddMinutes(-1) }, CancellationToken.None);
        await fixture.Database.RefreshPullRequestLinksAsync(project, current with { Url = "https://github.example/other/repo/pull/42", Title = "Other repo" }, CancellationToken.None);
        var saved = await fixture.Database.EnrichThreadDescriptorAsync((await fixture.Database.GetThreadAsync(thread.ThreadId))!);
        Assert.Equal("Merged", saved.PullRequest?.State);
        Assert.Equal("Updated title", saved.PullRequest?.Title);
        Assert.Equal("Keep local prompt", (await fixture.Database.GetOrCreateThreadDraftAsync(thread.ThreadId)).Text);
    }

    [Theory]
    [InlineData(PullRequestManagementAction.Merge)]
    [InlineData(PullRequestManagementAction.EnableAutoMerge)]
    [InlineData(PullRequestManagementAction.DisableAutoMerge)]
    [InlineData(PullRequestManagementAction.UpdateBranch)]
    [InlineData(PullRequestManagementAction.Revert)]
    [InlineData(PullRequestManagementAction.ApproveWorkflow)]
    [InlineData(PullRequestManagementAction.SetReaction)]
    public async Task AdvancedActionsRejectStaleHeadsAndNeverReplayUncertainWrites(PullRequestManagementAction action)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.AutoMerge = action == PullRequestManagementAction.DisableAutoMerge;
        fixture.State = action == PullRequestManagementAction.Revert ? "MERGED" : "OPEN";
        var request = fixture.Request(action);
        var stale = await fixture.Service.ManagePullRequestAsync(request with { Target = request.Target with { HeadCommitId = "stale" } });
        Assert.Equal(CommandReceiptState.Rejected, stale.State);
        Assert.Empty(fixture.Writes);
        fixture.LoseResponse = true;
        var runner = new HostingOperationRunner(fixture.Database);
        var payload = JsonSerializer.Serialize(request, ProtocolJsonContext.Default.ManagePullRequestRequest);
        var uncertain = await runner.RunAsync(request.OperationId, "Manage", payload, token => fixture.Service.ManagePullRequestAsync(request, token));
        Assert.Equal(CommandReceiptState.DispatchUncertain, uncertain.State);
        Assert.Equal(uncertain, await runner.RunAsync(request.OperationId, "Manage", payload, token => fixture.Service.ManagePullRequestAsync(request, token)));
        Assert.Single(fixture.Writes);
    }

    [Theory]
    [InlineData(PullRequestManagementAction.Merge)]
    [InlineData(PullRequestManagementAction.EnableAutoMerge)]
    [InlineData(PullRequestManagementAction.DisableAutoMerge)]
    [InlineData(PullRequestManagementAction.UpdateBranch)]
    [InlineData(PullRequestManagementAction.Revert)]
    [InlineData(PullRequestManagementAction.ApproveWorkflow)]
    [InlineData(PullRequestManagementAction.SetReaction)]
    public async Task AdvancedActionsRejectMissingPermissions(PullRequestManagementAction action)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.AutoMerge = action == PullRequestManagementAction.DisableAutoMerge;
        fixture.State = action == PullRequestManagementAction.Revert ? "MERGED" : "OPEN";
        fixture.Permission = "READ";
        fixture.CanUpdate = false;
        Assert.Equal(CommandReceiptState.Rejected, (await fixture.Service.ManagePullRequestAsync(fixture.Request(action))).State);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public void MergeAndUpdateBindExpectedHeadAndRejectUnsupportedMethods()
    {
        var target = new PullRequestReviewTarget(new(ProjectId.New()), "github.example/owner/repo", "42", "head");
        var merge = new ManagePullRequestRequest(target, PullRequestManagementAction.Merge, MergeMethod: PullRequestMergeMethod.Squash);
        var mutation = SourceControlHostingService.BuildManagementMutation(merge, Repository());
        Assert.Contains("expectedHeadOid:$head", mutation.Query);
        Assert.Equal("head", JsonSerializer.SerializeToElement(mutation.Variables).GetProperty("head").GetString());
        Assert.ThrowsAny<Exception>(() => SourceControlHostingService.BuildManagementMutation(merge with { MergeMethod = (PullRequestMergeMethod)99 }, Repository()));
        Assert.ThrowsAny<Exception>(() => SourceControlHostingService.BuildManagementMutation(merge, Repository(state: "CLOSED")));
        var update = SourceControlHostingService.BuildManagementMutation(new(target, PullRequestManagementAction.UpdateBranch, UpdateMethod: PullRequestUpdateMethod.Rebase), Repository());
        Assert.Contains("expectedHeadOid:$head", update.Query);
        Assert.Equal("REBASE", JsonSerializer.SerializeToElement(update.Variables).GetProperty("method").GetString());
    }

    [Fact]
    public async Task ReactionsSupportOtherAuthorsAndNoOpDesiredStateButRejectAnotherPullRequest()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request(PullRequestManagementAction.SetReaction) with { ItemId = "COMMENT", Reaction = PullRequestReactionContent.Heart };
        fixture.Comment = JsonSerializer.SerializeToElement(new { id = "COMMENT", viewerDidAuthor = false, viewerCanReact = true,
            pullRequest = new { id = "PR_NODE" }, reactionGroups = new[] { new { content = "HEART", viewerHasReacted = true, reactors = new { totalCount = 3 } } } });
        var reaction = Assert.Single(SourceControlHostingService.ParseReactions(fixture.Comment));
        Assert.Equal(3, reaction.Count);
        Assert.True(reaction.ViewerHasReacted);
        Assert.True((await fixture.Service.ManagePullRequestAsync(request)).Succeeded);
        Assert.Empty(fixture.Writes);
        Assert.True((await fixture.Service.ManagePullRequestAsync(request with { Reacted = false })).Succeeded);
        Assert.Contains("removeReaction", Assert.Single(fixture.Writes).Input);
        fixture.Comment = JsonSerializer.SerializeToElement(new { id = "COMMENT", viewerCanReact = true, pullRequest = new { id = "OTHER_PR" } });
        Assert.False((await fixture.Service.ManagePullRequestAsync(request)).Succeeded);
        Assert.Single(fixture.Writes);
    }

    [Theory]
    [InlineData("foreign-pr")]
    [InlineData("other-head")]
    [InlineData("push")]
    [InlineData("approved")]
    [InlineData("foreign-id")]
    public async Task WorkflowApprovalRejectsUnrelatedAndAlreadyApprovedRuns(string mismatch)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Workflow = Workflow(pr: mismatch == "foreign-pr" ? 43 : 42, head: mismatch == "other-head" ? "different" : "head",
            eventName: mismatch == "push" ? "push" : "pull_request", conclusion: mismatch == "approved" ? "success" : "action_required", id: mismatch == "foreign-id" ? 92 : 91);
        Assert.Equal(CommandReceiptState.Rejected, (await fixture.Service.ManagePullRequestAsync(fixture.Request(PullRequestManagementAction.ApproveWorkflow))).State);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task WorkflowDiscoveryFiltersAndPaginatesOnlyMatchingRunsAndRejectsChangedHeads()
    {
        using var fixture = await Fixture.CreateAsync();
        var target = fixture.Request(PullRequestManagementAction.ApproveWorkflow).Target;
        fixture.WorkflowPage = Enumerable.Range(1, 100).Select(i => Workflow(pr: i == 1 ? 42 : 43, id: i)).ToArray();
        var page = await fixture.Service.GetPullRequestWorkflowsAsync(new(target));
        Assert.Equal("1", Assert.Single(page.Workflows).Id);
        Assert.Equal(2, page.NextPage);
        fixture.MoveHeadAfterWorkflowList = true;
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.GetPullRequestWorkflowsAsync(new(target)));
        Assert.Empty(fixture.Writes);
    }

    private static JsonElement Workflow(int pr = 42, string head = "head", string eventName = "pull_request", string conclusion = "action_required", int id = 91) =>
        JsonSerializer.SerializeToElement(new { id, name = "CI", html_url = "https://github.example/owner/repo/actions/runs/91", head_sha = head, @event = eventName,
            status = "completed", conclusion, pull_requests = new[] { new { number = pr, head = new { sha = head } } } });

    [Theory]
    [InlineData(1, "contributor/fork", true)]
    [InlineData(2, "contributor/fork", false)]
    [InlineData(0, "contributor/fork", false)]
    [InlineData(1, "other/fork", false)]
    public async Task ForkRunsWithoutAssociationRequireAUniqueMatchingHead(int matches, string runRepository, bool accepted)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Workflow = JsonSerializer.SerializeToElement(new { id = 91, name = "CI", head_sha = "head", head_branch = "feature", head_repository = new { full_name = runRepository },
            @event = "pull_request", status = "completed", conclusion = "action_required", pull_requests = Array.Empty<object>() });
        fixture.WorkflowPage = [fixture.Workflow];
        fixture.UniqueHeadMatches = matches;
        var target = fixture.Request(PullRequestManagementAction.ApproveWorkflow).Target;
        var page = await fixture.Service.GetPullRequestWorkflowsAsync(new(target));
        Assert.Equal(accepted ? 1 : 0, page.Workflows.Count);
        var result = await fixture.Service.ManagePullRequestAsync(fixture.Request(PullRequestManagementAction.ApproveWorkflow));
        Assert.Equal(accepted, result.Succeeded);
        Assert.Equal(accepted ? 1 : 0, fixture.Writes.Count);
    }

    private static JsonElement Repository(bool canUpdate = true, string permission = "WRITE", string state = "OPEN", bool auto = false, string head = "head") => JsonSerializer.SerializeToElement(new
    {
        viewerPermission = permission,
        mergeCommitAllowed = true, squashMergeAllowed = true, rebaseMergeAllowed = true, autoMergeAllowed = true,
        pullRequest = new { id = "PR_NODE", number = 42, title = "Original title", body = "Original body", state, isDraft = false, headRefOid = head, viewerCanUpdate = canUpdate,
            headRefName = "feature", headRepository = new { nameWithOwner = "contributor/fork", owner = new { login = "contributor" } },
            mergeable = "MERGEABLE", mergeStateStatus = "CLEAN", viewerCanEnableAutoMerge = canUpdate, viewerCanDisableAutoMerge = canUpdate,
            viewerCanUpdateBranch = canUpdate, viewerCanReact = canUpdate, isCrossRepository = true, autoMergeRequest = auto ? new { enabledAt = "2026-09-08" } : null }
    });
    private static JsonElement Comment(string parent = "PR_NODE", bool own = true, string body = "original comment", bool canDelete = true, string type = "IssueComment") =>
        JsonSerializer.SerializeToElement(new { __typename = type, id = "COMMENT", body, state = "COMMENTED", viewerDidAuthor = own, viewerCanUpdate = true, viewerCanDelete = canDelete,
            pullRequest = type == "IssueComment" ? null : new { id = parent }, issuePullRequest = type == "IssueComment" ? new { id = parent } : null });

    private sealed class Fixture : IDisposable
    {
        private readonly HostTestDirectory _directory = new();
        public HostDatabase Database { get; private set; } = null!;
        public SourceControlHostingService Service { get; private set; } = null!;
        private ProjectId _project;
        public bool CanUpdate { get; set; } = true;
        public string Permission { get; set; } = "WRITE";
        public string State { get; set; } = "OPEN";
        public string Head { get; set; } = "head";
        public bool AutoMerge { get; set; }
        public bool MoveHeadAfterWorkflowList { get; set; }
        public int UniqueHeadMatches { get; set; } = 1;
        public JsonElement Workflow { get; set; } = PullRequestManagementTests.Workflow();
        public JsonElement[] WorkflowPage { get; set; } = [PullRequestManagementTests.Workflow()];
        public JsonElement Comment { get; set; } = PullRequestManagementTests.Comment();
        public bool LoseResponse { get; set; }
        public List<(IReadOnlyList<string> Arguments, string? Input)> Writes { get; } = [];

        public ManagePullRequestRequest Request(PullRequestManagementAction action) => new(new(new(_project), "github.example/owner/repo", "42", "head"), action,
            Title: "New title $(literal)", Body: action == PullRequestManagementAction.EditDetails ? "New description" : "edited comment", IsDraft: true,
            ItemId: action switch { PullRequestManagementAction.RemoveReviewer => "owner/core-team", PullRequestManagementAction.RemoveLabel => "bug/parser", PullRequestManagementAction.ApproveWorkflow => "91", PullRequestManagementAction.SetReaction => null, _ => "COMMENT" },
            ExpectedTitle: "Original title", ExpectedBody: action == PullRequestManagementAction.EditDetails ? "Original body" : "original comment", ExpectedIsDraft: false, OperationId: CommandId.New(),
            MergeMethod: PullRequestMergeMethod.Squash, UpdateMethod: PullRequestUpdateMethod.Rebase, Reaction: PullRequestReactionContent.Eyes, Reacted: true);

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            var root = fixture._directory.CreateDirectory("repo");
            Git(root, "init", "--quiet");
            Git(root, "remote", "add", "origin", "https://github.example/owner/repo.git");
            fixture.Database = new HostDatabase(fixture._directory.CreateOptions());
            await fixture.Database.InitializeAsync();
            var projects = new ProjectService(fixture.Database);
            fixture._project = (await projects.AddAsync(new(root))).ProjectId;
            fixture.Service = new(new ThreadWorkspaceResolver(fixture.Database), projects, fixture.ExecuteAsync);
            return fixture;
        }

        private Task<(int, string, string)> ExecuteAsync(string tool, IReadOnlyList<string> args, string root, string? input, CancellationToken token)
        {
            if (args[0] == "auth") return Task.FromResult((0, "", ""));
            var write = args.Contains("DELETE") || args.Contains("POST") && args[^1].EndsWith("/approve", StringComparison.Ordinal) || input?.Contains("mutation(", StringComparison.Ordinal) == true;
            if (write)
            {
                Writes.Add((args, input));
                if (LoseResponse) throw new IOException("Lost response after dispatch");
                if (args[^1].EndsWith("/approve", StringComparison.Ordinal)) return Task.FromResult((0, "HTTP/2.0 201 Created\r\n\r\n", ""));
                if (input?.Contains("revertPullRequest", StringComparison.Ordinal) == true) return Task.FromResult((0, "{\"data\":{\"change\":{\"revertPullRequest\":{\"id\":\"REVERT\",\"url\":\"https://github.example/owner/repo/pull/43\"}}}}", ""));
                return Task.FromResult((0, args.Contains("DELETE") ? "[]" : "{\"data\":{\"change\":{\"clientMutationId\":null}}}", ""));
            }
            if (args[^1].Contains("/actions/runs?", StringComparison.Ordinal))
            {
                if (MoveHeadAfterWorkflowList) Head = "moved";
                return Task.FromResult((0, JsonSerializer.Serialize(new { workflow_runs = WorkflowPage }), ""));
            }
            if (args[^1].Contains("/actions/runs/", StringComparison.Ordinal)) return Task.FromResult((0, Workflow.GetRawText(), ""));
            if (args[^1].Contains("/pulls?state=open&head=", StringComparison.Ordinal))
                return Task.FromResult((0, JsonSerializer.Serialize(Enumerable.Range(0, UniqueHeadMatches).Select(i => new {
                    number = 42 + i, head = new { sha = "head", @ref = "feature", repo = new { full_name = "contributor/fork" } } })), ""));
            var data = input?.Contains("node(id:", StringComparison.Ordinal) == true
                ? JsonSerializer.Serialize(new { data = new { node = Comment } })
                : JsonSerializer.Serialize(new { data = new { repository = Repository(CanUpdate, Permission, State, AutoMerge, Head) } });
            return Task.FromResult((0, data, ""));
        }
        public void Dispose() { Service.Dispose(); _directory.Dispose(); }
        private static void Git(string root, params string[] args)
        {
            var info = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var error = process.StandardError.ReadToEnd(); process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
        }
    }
}
