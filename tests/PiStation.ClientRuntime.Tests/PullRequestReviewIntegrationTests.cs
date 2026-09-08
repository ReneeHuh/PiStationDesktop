using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using PiStation.Host.Hosting;
using PiStation.Host.SourceControl;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.ClientRuntime.Tests;

public sealed partial class PullRequestReviewIntegrationTests
{
    [Fact]
    public async Task FilteredListsCarryViewerAndFiltersAcrossPagesThroughTheHub()
    {
        using var directory = new ClientTestDirectory();
        var path = directory.CreateDirectory("filtered-prs");
        await GitAsync(path, "init", "--initial-branch=main");
        await GitAsync(path, "remote", "add", "origin", "https://github.com/owner/repo.git");
        var fixture = new GitHubFixture { ListCount = 205 };
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(),
            sourceControlFactory: (resolver, projects) => new SourceControlHostingService(resolver, projects, fixture.RunAsync));
        await using var client = new EnvironmentClient(new() { HubAddress = host.HubAddress, BearerCredential = host.BearerCredential });
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new(path));
        var request = new ListPullRequestsRequest(new(project.ProjectId), PullRequestState.Open,
            Filters: new(Involvement: PullRequestInvolvement.ReviewRequested, LabelGroups: [["bug", "regression"]], Checks: PullRequestChecksFilter.Passing));
        var first = await client.ListPullRequestsAsync(request);
        Assert.Equal(100, first.PullRequests.Count);
        Assert.Equal(100, first.NextOffset);
        Assert.All(first.PullRequests, pr => Assert.Equal(PullRequestCheckState.Passed, pr.Checks));
        var second = await client.ListPullRequestsAsync(request with { Offset = first.NextOffset.GetValueOrDefault(-1) });
        Assert.Equal("101", second.PullRequests[0].Number);
        Assert.Equal(200, second.NextOffset);
        var last = await client.ListPullRequestsAsync(request with { Offset = second.NextOffset.GetValueOrDefault(-1) });
        Assert.Equal(5, last.PullRequests.Count);
        Assert.Null(last.NextOffset);
        Assert.Equal(3, fixture.ListCommands.Count);
        Assert.All(fixture.ListCommands, args => {
            var search = args[Array.IndexOf(args, "--search") + 1];
            Assert.Contains("review-requested:\"reviewer\"", search);
            Assert.Contains("label:\"bug\",\"regression\"", search);
            Assert.Contains("status:success", search);
            Assert.Equal("github.com/owner/repo", args[Array.IndexOf(args, "--repo") + 1]);
            Assert.DoesNotContain("statusCheckRollup", args[Array.IndexOf(args, "--json") + 1]);
        });
        fixture.ListCount = 1500;
        var bounded = await client.ListPullRequestsAsync(request with { Offset = 900 });
        Assert.Equal(100, bounded.PullRequests.Count);
        Assert.Null(bounded.NextOffset);
        Assert.Contains("1000", bounded.Notice);
        Assert.Equal(0, fixture.Writes);
    }

    [Fact]
    public async Task ReviewRoundTripUsesDurableReceiptsAndRejectsStaleOrForeignTargets()
    {
        using var directory = new ClientTestDirectory();
        var projectPath = directory.CreateDirectory("review-repository");
        await GitAsync(projectPath, "init", "--initial-branch=main");
        await GitAsync(projectPath, "remote", "add", "origin", "https://github.com/owner/repo.git");
        var fixture = new GitHubFixture();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(),
            sourceControlFactory: (resolver, projects) => new SourceControlHostingService(resolver, projects, fixture.RunAsync));
        await using var client = new EnvironmentClient(new() { HubAddress = host.HubAddress, BearerCredential = host.BearerCredential });
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new(projectPath));
        var workspace = new WorkspaceTarget(project.ProjectId);
        var snapshot = await client.GetPullRequestReviewAsync(new(workspace, "7"));
        Assert.Equal("7", snapshot.PullRequest.Number);
        Assert.Equal("Review fixture", snapshot.PullRequest.Title);
        Assert.Equal(PullRequestCheckState.Failed, snapshot.PullRequest.Checks);
        Assert.Equal("TIMED_OUT", Assert.Single(snapshot.Checks).Conclusion);
        Assert.Contains(snapshot.Files, file => file.Path == "src/App.cs" && file.Lines.Any(line => line.NewLine == 2));
        var target = new PullRequestReviewTarget(workspace, "github.com/owner/repo", "7", fixture.Head);
        var request = new SubmitPullRequestReviewRequest(target, PullRequestReviewEvent.Comment, "Please check the guard.",
            [new("src/App.cs", 2, PullRequestDiffSide.Right, "Handle the empty case.")], CommandId.New());
        var first = await client.SubmitPullRequestReviewAsync(request);
        Assert.True(first.Succeeded, first.Message);
        Assert.Equal(CommandReceiptState.Completed, first.State);
        Assert.Equal(1, fixture.Writes);
        await client.DisconnectAsync();
        await client.ConnectAsync();
        var replay = await client.SubmitPullRequestReviewAsync(request);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(replay));
        Assert.Equal(1, fixture.Writes);

        var stale = await client.SubmitPullRequestReviewAsync(request with { Target = target with { HeadCommitId = new string('b', 40) }, OperationId = CommandId.New() });
        Assert.False(stale.Succeeded);
        Assert.Equal(CommandReceiptState.Rejected, stale.State);
        var foreign = await client.SubmitPullRequestReviewAsync(request with { Target = target with { Repository = "github.com/other/repo" }, OperationId = CommandId.New() });
        Assert.False(foreign.Succeeded);
        Assert.Equal(CommandReceiptState.Rejected, foreign.State);
        var invalidLine = await client.SubmitPullRequestReviewAsync(request with { Comments = [new("src/App.cs", 999, PullRequestDiffSide.Right, "Invalid line")], OperationId = CommandId.New() });
        Assert.Equal(CommandReceiptState.Rejected, invalidLine.State);
        var reply = await client.ReplyPullRequestThreadAsync(new(target, "thread-7", "Fixed", CommandId.New()));
        Assert.True(reply.Succeeded, reply.Message);
        var resolve = await client.SetPullRequestThreadResolvedAsync(new(target, "thread-7", true, CommandId.New()));
        Assert.True(resolve.Succeeded, resolve.Message);
        var foreignThread = await client.ReplyPullRequestThreadAsync(new(target, "another-pr-thread", "Wrong PR", CommandId.New()));
        Assert.Equal(CommandReceiptState.Rejected, foreignThread.State);
        Assert.Equal(3, fixture.Writes);
        Assert.Contains(await client.ListHostingOperationsAsync(), operation => operation.OperationId == request.OperationId && operation.State == CommandReceiptState.Completed);
    }

    [Fact]
    public async Task LostProviderWriteOutcomeIsPersistedAndNeverDispatchedTwice()
    {
        using var directory = new ClientTestDirectory();
        var projectPath = directory.CreateDirectory("uncertain-review");
        await GitAsync(projectPath, "init", "--initial-branch=main");
        await GitAsync(projectPath, "remote", "add", "origin", "https://github.com/owner/repo.git");
        var fixture = new GitHubFixture { FailWrites = true };
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(),
            sourceControlFactory: (resolver, projects) => new SourceControlHostingService(resolver, projects, fixture.RunAsync));
        await using var client = new EnvironmentClient(new() { HubAddress = host.HubAddress, BearerCredential = host.BearerCredential });
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new(projectPath));
        var request = new SubmitPullRequestReviewRequest(new(new(project.ProjectId), "github.com/owner/repo", "7", fixture.Head),
            PullRequestReviewEvent.Comment, "Review body", [], CommandId.New());
        var result = await client.SubmitPullRequestReviewAsync(request);
        Assert.Equal(CommandReceiptState.DispatchUncertain, result.State);
        Assert.Equal(result, await client.SubmitPullRequestReviewAsync(request));
        Assert.Equal(1, fixture.Writes);
        await Assert.ThrowsAsync<HubException>(() => client.SubmitPullRequestReviewAsync(request with { Body = "Different request with same identity" }));
    }

    [Theory]
    [InlineData(PullRequestManagementAction.EditDetails)]
    [InlineData(PullRequestManagementAction.SetDraft)]
    [InlineData(PullRequestManagementAction.EditComment)]
    [InlineData(PullRequestManagementAction.DeleteComment)]
    [InlineData(PullRequestManagementAction.RemoveLabel)]
    [InlineData(PullRequestManagementAction.RemoveReviewer)]
    [InlineData(PullRequestManagementAction.Merge)]
    [InlineData(PullRequestManagementAction.EnableAutoMerge)]
    [InlineData(PullRequestManagementAction.DisableAutoMerge)]
    [InlineData(PullRequestManagementAction.UpdateBranch)]
    [InlineData(PullRequestManagementAction.Revert)]
    [InlineData(PullRequestManagementAction.ApproveWorkflow)]
    [InlineData(PullRequestManagementAction.SetReaction)]
    public async Task ManagementActionsRoundTripThroughTheHubAndReplayWithoutAnotherWrite(PullRequestManagementAction action)
    {
        using var directory = new ClientTestDirectory();
        var projectPath = directory.CreateDirectory("managed-review");
        await GitAsync(projectPath, "init", "--initial-branch=main");
        await GitAsync(projectPath, "remote", "add", "origin", "https://github.com/owner/repo.git");
        var fixture = new GitHubFixture();
        fixture.State = action == PullRequestManagementAction.Revert ? "MERGED" : "OPEN";
        fixture.AutoMerge = action == PullRequestManagementAction.DisableAutoMerge;
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(),
            sourceControlFactory: (resolver, projects) => new SourceControlHostingService(resolver, projects, fixture.RunAsync));
        await using var client = new EnvironmentClient(new() { HubAddress = host.HubAddress, BearerCredential = host.BearerCredential });
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new(projectPath));
        var snapshot = await client.GetPullRequestReviewAsync(new(new(project.ProjectId), "7"));
        Assert.True(snapshot.CanEditDetails);
        Assert.True(snapshot.CanManageMetadata);
        Assert.True(snapshot.CanReact);
        Assert.NotNull(snapshot.Advanced);
        Assert.True(Assert.Single(Assert.Single(snapshot.Discussions).Comments).CanEdit);
        var request = new ManagePullRequestRequest(new(new(project.ProjectId), "github.com/owner/repo", "7", fixture.Head),
            action, Title: "Updated title", Body: "Updated body", IsDraft: true,
            ItemId: action switch { PullRequestManagementAction.RemoveLabel => "bug", PullRequestManagementAction.RemoveReviewer => "owner/team", PullRequestManagementAction.ApproveWorkflow => "91", _ => "comment-1" },
            ExpectedTitle: "Review fixture", ExpectedBody: action == PullRequestManagementAction.EditDetails ? "Fixture description" : "A comment",
            ExpectedIsDraft: false, OperationId: CommandId.New(), MergeMethod: PullRequestMergeMethod.Squash, UpdateMethod: PullRequestUpdateMethod.Rebase, Reaction: PullRequestReactionContent.Heart, Reacted: true);
        if (action == PullRequestManagementAction.ApproveWorkflow)
        {
            var workflows = await client.GetPullRequestWorkflowsAsync(new(request.Target));
            Assert.Equal(request.Target, workflows.Target);
            Assert.Equal("91", Assert.Single(workflows.Workflows).Id);
        }
        var result = await client.ManagePullRequestAsync(request);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(CommandReceiptState.Completed, result.State);
        await client.DisconnectAsync();
        await client.ConnectAsync();
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(await client.ManagePullRequestAsync(request)));
        Assert.Equal(1, fixture.Writes);
    }

    [Theory]
    [InlineData(50000, true)]
    [InlineData(65537, false)]
    public async Task LoadedDescriptionsRetainSupportedTextAndDisableEditingWhenTruncated(int length, bool editable)
    {
        using var directory = new ClientTestDirectory();
        var path = directory.CreateDirectory("description");
        await GitAsync(path, "init", "--initial-branch=main");
        await GitAsync(path, "remote", "add", "origin", "https://github.com/owner/repo.git");
        var fixture = new GitHubFixture { Description = new string('x', length) };
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(),
            sourceControlFactory: (resolver, projects) => new SourceControlHostingService(resolver, projects, fixture.RunAsync));
        await using var client = new EnvironmentClient(new() { HubAddress = host.HubAddress, BearerCredential = host.BearerCredential });
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new(path));
        var snapshot = await client.GetPullRequestReviewAsync(new(new(project.ProjectId), "7"));
        Assert.Equal(Math.Min(length, PullRequestReviewDefaults.MaximumDescriptionCharacters), snapshot.Body.Length);
        Assert.Equal(editable, snapshot.CanEditDetails);
        Assert.Equal(!editable, snapshot.IsTruncated);
    }

    [Theory]
    [InlineData("submit", 422, CommandReceiptState.Rejected)]
    [InlineData("submit", 403, CommandReceiptState.Rejected)]
    [InlineData("submit", 503, CommandReceiptState.DispatchUncertain)]
    [InlineData("reply", 403, CommandReceiptState.Rejected)]
    [InlineData("reply", 200, CommandReceiptState.Rejected)]
    [InlineData("partial-reply", 200, CommandReceiptState.DispatchUncertain)]
    [InlineData("resolve", 503, CommandReceiptState.DispatchUncertain)]
    public async Task ProviderFailureClassificationSurvivesReceiptReplay(string action, int status, CommandReceiptState expected)
    {
        using var directory = new ClientTestDirectory();
        var projectPath = directory.CreateDirectory("provider-rejection");
        await GitAsync(projectPath, "init", "--initial-branch=main");
        await GitAsync(projectPath, "remote", "add", "origin", "https://github.com/owner/repo.git");
        var responseBody = status == 200
            ? action == "partial-reply"
                ? """{"data":{"addPullRequestReviewThreadReply":{"comment":{"id":"created-reply"}}},"errors":[{"type":"FORBIDDEN","message":"Another field failed"}]}"""
                : """{"data":null,"errors":[{"type":"FORBIDDEN","message":"Permission denied"}]}"""
            : """{"message":"Provider rejected the request"}""";
        var fixture = new GitHubFixture
        {
            WriteResponse = (1, $"HTTP/2.0 {status} Provider response\r\nContent-Type: application/json\r\n\r\n{responseBody}", "gh: provider response")
        };
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(),
            sourceControlFactory: (resolver, projects) => new SourceControlHostingService(resolver, projects, fixture.RunAsync));
        await using var client = new EnvironmentClient(new() { HubAddress = host.HubAddress, BearerCredential = host.BearerCredential });
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new(projectPath));
        var target = new PullRequestReviewTarget(new(project.ProjectId), "github.com/owner/repo", "7", fixture.Head);
        var operationId = CommandId.New();
        Task<SourceControlOperationResult> Send() => action switch
        {
            "reply" or "partial-reply" => client.ReplyPullRequestThreadAsync(new(target, "thread-7", "Reply", operationId)),
            "resolve" => client.SetPullRequestThreadResolvedAsync(new(target, "thread-7", true, operationId)),
            _ => client.SubmitPullRequestReviewAsync(new(target, PullRequestReviewEvent.Comment, "Review", [], operationId))
        };
        var result = await Send();
        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.State);
        Assert.Equal(result, await Send());
        Assert.Equal(1, fixture.Writes);
        Assert.Contains(await client.ListHostingOperationsAsync(), operation => operation.OperationId == operationId && operation.State == expected);
    }

    private static async Task GitAsync(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stdout;
        Assert.True(process.ExitCode == 0, await stderr);
    }

    private sealed class GitHubFixture
    {
        public string Head { get; } = new('a', 40);
        public string State { get; set; } = "OPEN";
        public bool AutoMerge { get; set; }
        public string Description { get; init; } = "Fixture description";
        public int ListCount { get; set; } = 1;
        public List<string[]> ListCommands { get; } = [];
        public int Writes { get; private set; }
        public bool FailWrites { get; init; }
        public (int ExitCode, string StandardOutput, string StandardError)? WriteResponse { get; init; }

        public Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(string tool, IReadOnlyList<string> arguments,
            string workspace, string? input, CancellationToken cancellationToken)
        {
            Assert.Equal("gh", tool);
            if (arguments[0] == "pr" && arguments[1] == "list")
            {
                var args = arguments.ToArray();
                ListCommands.Add(args);
                var limit = int.Parse(args[Array.IndexOf(args, "--limit") + 1], System.Globalization.CultureInfo.InvariantCulture);
                var rows = Enumerable.Range(1, Math.Min(ListCount, limit)).Select(number => new { number, title = "Filtered PR", state = "OPEN", author = new { login = "author" },
                    url = $"https://github.com/owner/repo/pull/{number}", headRefName = "feature", baseRefName = "main", updatedAt = "2026-09-08T00:00:00Z" });
                return Task.FromResult((0, JsonSerializer.Serialize(rows), ""));
            }
            Assert.Contains("--hostname", arguments);
            Assert.Contains("github.com", arguments);
            if (arguments[0] == "auth") return Task.FromResult((0, string.Empty, string.Empty));
            var endpoint = arguments[^1];
            Task<(int ExitCode, string StandardOutput, string StandardError)> Success(string json) =>
                Task.FromResult((0, arguments.Contains("--include") ? "HTTP/2.0 200 OK\r\nContent-Type: application/json\r\n\r\n" + json : json, string.Empty));
            if (arguments.Contains("DELETE"))
            {
                Writes++;
                return Success("{}");
            }
            if (endpoint.Contains("/actions/runs", StringComparison.Ordinal))
            {
                if (arguments.Contains("POST"))
                {
                    Writes++;
                    Assert.False(cancellationToken.CanBeCanceled);
                    return Task.FromResult((0, "HTTP/2.0 201 Created\r\n\r\n", ""));
                }
                var run = new { id = 91, name = "CI", head_sha = Head, @event = "pull_request", status = "completed", conclusion = "action_required",
                    html_url = "https://github.com/owner/repo/actions/runs/91", pull_requests = new[] { new { number = 7, head = new { sha = Head } } } };
                return Success(endpoint.Contains('?') ? JsonSerializer.Serialize(new { workflow_runs = new[] { run } }) : JsonSerializer.Serialize(run));
            }
            if (input is not null)
            {
                using var payload = JsonDocument.Parse(input);
                var isGraphQl = payload.RootElement.TryGetProperty("query", out var query);
                if (isGraphQl && query.GetString()!.Contains("PullRequestListCheckStates", StringComparison.Ordinal))
                {
                    var states = Regex.Matches(query.GetString()!, @"(pr\d+):pullRequest\(number:(\d+)\)").ToDictionary(
                        match => match.Groups[1].Value, match => new { number = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                            commits = new { nodes = new[] { new { commit = new { statusCheckRollup = new { state = "SUCCESS" } } } } } });
                    return Success(JsonSerializer.Serialize(new { data = new { repository = states } }));
                }
                if (!isGraphQl || query.GetString()!.TrimStart().StartsWith("mutation", StringComparison.Ordinal))
                {
                    Writes++;
                    Assert.False(cancellationToken.CanBeCanceled, "Accepted hosting writes must outlive disconnects.");
                    if (FailWrites) throw new IOException("Fixture connection lost after accepting the write");
                    if (WriteResponse is { } response) return Task.FromResult(response);
                    if (!isGraphQl)
                    {
                        Assert.Equal("repos/owner/repo/pulls/7/reviews", endpoint);
                        Assert.Equal(Head, payload.RootElement.GetProperty("commit_id").GetString());
                        return Success("{\"id\":77,\"state\":\"COMMENTED\"}");
                    }
                    return Success("""{"data":{"change":{"clientMutationId":null,"pullRequest":{"id":"pr-id"},"revertPullRequest":{"id":"revert-id","url":"https://github.com/owner/repo/pull/8"}},"addPullRequestReviewThreadReply":{"comment":{"id":"reply"}},"resolveReviewThread":{"thread":{"id":"thread-7","isResolved":true}},"unresolveReviewThread":{"thread":{"id":"thread-7","isResolved":false}}}}""");
                }
                if (query.GetString()!.Contains("node(id:$id)", StringComparison.Ordinal))
                    return Success("""{"data":{"node":{"id":"comment-1","__typename":"PullRequestReviewComment","body":"A comment","viewerDidAuthor":true,"viewerCanUpdate":true,"viewerCanDelete":true,"viewerCanReact":true,"pullRequest":{"id":"pr-id"}}}}""");
                return Success(GraphQl());
            }
            Assert.Contains("repos/owner/repo/pulls/7/files", endpoint, StringComparison.Ordinal);
            return Success("""[{"filename":"src/App.cs","status":"modified","additions":1,"deletions":1,"patch":"@@ -1,2 +1,2 @@\n context\n-old\n+new"}]""");
        }

        public string GraphQl() => """
        {"data":{"viewer":{"login":"reviewer"},"repository":{"id":"repo-id","viewerPermission":"WRITE","mergeCommitAllowed":true,"squashMergeAllowed":true,"rebaseMergeAllowed":true,"autoMergeAllowed":true,"pullRequest":{
          "id":"pr-id","number":7,"title":"Review fixture","url":"https://github.com/owner/repo/pull/7","state":"$STATE$","isDraft":false,"viewerCanUpdate":true,
          "mergeable":"MERGEABLE","mergeStateStatus":"CLEAN","viewerCanEnableAutoMerge":true,"viewerCanDisableAutoMerge":true,"viewerCanUpdateBranch":true,"viewerCanReact":true,"isCrossRepository":true,"autoMergeRequest":$AUTO$,
          "body":$DESCRIPTION$,"updatedAt":"2026-09-06T12:00:00Z","author":{"login":"author"},
          "headRefName":"feature","baseRefName":"main","headRefOid":"$HEAD$","baseRefOid":"$BASE$",
          "labels":{"nodes":[],"pageInfo":{"hasNextPage":false}},"reviewRequests":{"nodes":[],"pageInfo":{"hasNextPage":false}},
          "comments":{"nodes":[],"pageInfo":{"hasNextPage":false}},"reviews":{"nodes":[],"pageInfo":{"hasNextPage":false}},
          "commits":{"nodes":[{"commit":{"oid":"$HEAD$","messageHeadline":"Fixture commit","committedDate":"2026-09-06T11:00:00Z","author":{"name":"Author","user":{"login":"author"}},"statusCheckRollup":{"contexts":{"nodes":[],"pageInfo":{"hasNextPage":false}}}}}],"pageInfo":{"hasNextPage":false}},
          "headCommit":{"nodes":[{"commit":{"oid":"$HEAD$","statusCheckRollup":{"contexts":{"nodes":[{"name":"CI","status":"COMPLETED","conclusion":"TIMED_OUT"}],"pageInfo":{"hasNextPage":false}}}}}]},
          "reviewThreads":{"nodes":[{"id":"thread-7","path":"src/App.cs","line":2,"diffSide":"RIGHT","isResolved":false,"isOutdated":false,"viewerCanReply":true,"viewerCanResolve":true,"viewerCanUnresolve":true,
             "comments":{"nodes":[{"id":"comment-1","databaseId":1,"body":"A comment","createdAt":"2026-09-06T12:00:00Z","viewerDidAuthor":true,"viewerCanUpdate":true,"viewerCanDelete":true,"author":{"login":"reviewer"}}],"pageInfo":{"hasNextPage":false}}}],"pageInfo":{"hasNextPage":false}}
        }}}}
        """.Replace("$HEAD$", Head, StringComparison.Ordinal).Replace("$BASE$", new string('b', 40), StringComparison.Ordinal)
            .Replace("$STATE$", State, StringComparison.Ordinal).Replace("$AUTO$", AutoMerge ? "{\"enabledAt\":\"2026-09-08\"}" : "null", StringComparison.Ordinal)
            .Replace("$DESCRIPTION$", JsonSerializer.Serialize(Description), StringComparison.Ordinal);
    }
}
