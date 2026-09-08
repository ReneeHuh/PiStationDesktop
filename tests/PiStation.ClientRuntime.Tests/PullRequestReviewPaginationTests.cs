using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using PiStation.App.ViewModels;
using PiStation.Host.Hosting;
using PiStation.Host.SourceControl;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed partial class PullRequestReviewIntegrationTests
{
    [Fact]
    public async Task ReviewPagesReachEveryCollectionAndLaterPagesRemainWritable()
    {
        using var directory = new ClientTestDirectory();
        var root = directory.CreateDirectory("paged-review");
        await GitAsync(root, "init", "--initial-branch=main");
        await GitAsync(root, "remote", "add", "origin", "https://github.com/owner/repo.git");
        var fixture = new PagedGitHubFixture();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(),
            sourceControlFactory: (resolver, projects) => new SourceControlHostingService(resolver, projects, fixture.RunAsync));
        await using var client = new EnvironmentClient(new() { HubAddress = host.HubAddress, BearerCredential = host.BearerCredential });
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new(root));
        var target = new WorkspaceTarget(project.ProjectId);
        var first = await client.GetPullRequestReviewAsync(new(target, "7"));
        Assert.Equal(20_000, Assert.Single(first.Files).Lines.Count);
        Assert.Equal(9, first.NextPages?.Count);
        Assert.Equal(PullRequestCheckState.Unknown, first.PullRequest.Checks);
        var firstCursor = first.NextPages![0];
        await Assert.ThrowsAsync<HubException>(() => client.GetPullRequestReviewAsync(new(target, "7", firstCursor with { Number = "8" })));
        await Assert.ThrowsAsync<HubException>(() => client.GetPullRequestReviewAsync(new(target, "7", firstCursor with { HeadCommitId = "old" })));
        await Assert.ThrowsAsync<HubException>(() => client.GetPullRequestReviewAsync(new(target, "7", firstCursor with { BaseCommitId = "old-base" })));
        using var model = new PullRequestReviewViewModel(() => client, new(directory.CreateDirectory("review-drafts")));
        await model.LoadAsync(project.ProjectId, target, first.PullRequest);
        model.SetBody("Draft survives all pages");
        var count = 0;
        while (model.CanLoadMore)
        {
            await model.LoadMoreAsync();
            Assert.DoesNotContain("Could not", model.Status);
            Assert.True(++count < 30);
        }
        var complete = model.Snapshot!;
        Assert.Empty(complete.NextPages!);
        Assert.False(complete.IsTruncated);
        Assert.Equal(401, complete.Files.Count);
        Assert.Equal(30_001, complete.Files[0].Lines.Count);
        Assert.Equal(30_000, complete.Files[0].Lines[^1].NewLine);
        Assert.Equal(101, complete.Commits.Count);
        Assert.Equal(101, complete.Checks.Count);
        Assert.Equal(PullRequestCheckState.Failed, complete.PullRequest.Checks);
        Assert.Equal(101, complete.PullRequest.Labels.Count);
        Assert.Equal(101, complete.PullRequest.Reviewers.Count);
        Assert.Equal(303, complete.Discussions.Count);
        Assert.Equal(101, complete.Discussions.Single(thread => thread.Id == "thread-0").Comments.Count);
        Assert.Equal("Draft survives all pages", model.Body);
        Assert.Contains("commit-100", model.CommitsSummary);
        Assert.Contains("check-100", model.ChecksSummary);
        model.SelectedFile = complete.Files[0];
        Assert.Equal(30_001, model.Lines.Count);
        var writeTarget = new PullRequestReviewTarget(target, "github.com/owner/repo", "7", fixture.Head);
        var review = await client.SubmitPullRequestReviewAsync(new(writeTarget, PullRequestReviewEvent.Comment, "Later pages",
            [new("file-400.cs", 1, PullRequestDiffSide.Right, "Last file"), new("file-0.cs", 30_000, PullRequestDiffSide.Right, "Last patch line")], CommandId.New()));
        Assert.True(review.Succeeded, review.Message);
        var reply = await client.ReplyPullRequestThreadAsync(new(writeTarget, "thread-100", "Later thread", CommandId.New()));
        Assert.True(reply.Succeeded, reply.Message);
        var resolve = await client.SetPullRequestThreadResolvedAsync(new(writeTarget, "thread-100", true, CommandId.New()));
        Assert.True(resolve.Succeeded, resolve.Message);
        var foreign = await client.ReplyPullRequestThreadAsync(new(writeTarget, "foreign-thread", "Wrong PR", CommandId.New()));
        Assert.False(foreign.Succeeded);
        Assert.Equal(3, fixture.Writes);
        Assert.Contains(5, fixture.FilePages);
        Assert.Contains("thread-100", fixture.RequestedThreads);
    }

    [Fact]
    public async Task SmallerGraphQlPagesRecoverFromLongCommentResponses()
    {
        using var directory = new ClientTestDirectory();
        var root = directory.CreateDirectory("large-comment-review");
        await GitAsync(root, "init", "--initial-branch=main");
        await GitAsync(root, "remote", "add", "origin", "https://github.com/owner/repo.git");
        var fixture = new PagedGitHubFixture { RequireSmallPages = true };
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(),
            sourceControlFactory: (resolver, projects) => new SourceControlHostingService(resolver, projects, fixture.RunAsync));
        await using var client = new EnvironmentClient(new() { HubAddress = host.HubAddress, BearerCredential = host.BearerCredential });
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new(root));
        var snapshot = await client.GetPullRequestReviewAsync(new(new(project.ProjectId), "7"));
        Assert.NotEmpty(snapshot.NextPages!);
        Assert.True(fixture.SmallPages > 0);
        Assert.True(fixture.SmallFilePages > 0);
        var next = await client.GetPullRequestReviewAsync(new(new(project.ProjectId), "7",
            snapshot.NextPages!.Single(page => page.Kind == PullRequestReviewPageKind.Files)));
        Assert.Equal(25, next.Files.Count);
        Assert.Equal(20_000, next.Files[0].PatchLineOffset);
        Assert.Equal(30_000, next.Files[0].Lines[^1].NewLine);
    }

    private sealed class PagedGitHubFixture
    {
        public string Head { get; } = new('a', 40);
        public int Writes { get; private set; }
        public int SmallPages { get; private set; }
        public int SmallFilePages { get; private set; }
        public bool RequireSmallPages { get; init; }
        public HashSet<int> FilePages { get; } = [];
        public HashSet<string> RequestedThreads { get; } = [];

        public Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(string tool, IReadOnlyList<string> arguments,
            string workspace, string? input, CancellationToken cancellationToken)
        {
            Assert.Equal("gh", tool);
            if (arguments[0] == "auth") return Success(string.Empty);
            if (input is not null)
            {
                using var payload = JsonDocument.Parse(input);
                var query = payload.RootElement.TryGetProperty("query", out var queryValue) ? queryValue.GetString() ?? "" : "";
                if (query.StartsWith("mutation", StringComparison.Ordinal) || payload.RootElement.TryGetProperty("event", out _))
                {
                    Writes++;
                    return Success(query.Length == 0 ? "{\"id\":7}" : "{\"data\":{\"mutation\":{\"id\":7}}}");
                }
                var variables = payload.RootElement.GetProperty("variables");
                if (RequireSmallPages && query.Contains("first:100", StringComparison.Ordinal))
                    return Success(new string('x', 2 * 1024 * 1024));
                if (query.Contains("first:25", StringComparison.Ordinal)) SmallPages++;
                if (variables.TryGetProperty("threadId", out var threadIdValue))
                {
                    var threadId = threadIdValue.GetString()!;
                    RequestedThreads.Add(threadId);
                    if (!threadId.StartsWith("thread-", StringComparison.Ordinal)) return Success("{\"data\":{\"node\":null}}");
                    var thread = Thread(int.Parse(threadId[7..], CultureInfo.InvariantCulture));
                    thread["pullRequest"] = JsonSerializer.SerializeToNode(new { number = 7, headRefOid = Head, repository = new { nameWithOwner = "owner/repo" } });
                    if (variables.TryGetProperty("cursor", out var threadCursor) && threadCursor.ValueKind == JsonValueKind.String)
                        thread["comments"] = Connection("threadComments", true, i => Comment($"thread-0-comment-{i}"));
                    return Success(new JsonObject { ["data"] = new JsonObject { ["node"] = thread } }.ToJsonString());
                }
                var cursor = variables.TryGetProperty("cursor", out var cursorValue) ? cursorValue.GetString() : null;
                var document = JsonNode.Parse(new GitHubFixture().GraphQl())!;
                var pr = document["data"]!["repository"]!["pullRequest"]!;
                pr["changedFiles"] = 401;
                pr["commits"] = Connection("commits", cursor == "commits:100", i => JsonSerializer.SerializeToNode(new
                { commit = new { oid = $"commit-{i}", messageHeadline = $"commit-{i}", committedDate = "2026-09-06T00:00:00Z", author = new { name = "Author" } } })!);
                pr["labels"] = Connection("labels", cursor == "labels:100", i => new JsonObject { ["name"] = $"label-{i}" });
                pr["reviewRequests"] = Connection("reviewRequests", cursor == "reviewRequests:100", i => JsonSerializer.SerializeToNode(new { requestedReviewer = new { login = $"reviewer-{i}" } })!);
                pr["comments"] = Connection("comments", cursor == "comments:100", i => Comment($"general-{i}"));
                pr["reviews"] = Connection("reviews", cursor == "reviews:100", i => Comment($"review-{i}"));
                pr["reviewThreads"] = Connection("reviewThreads", cursor == "reviewThreads:100", Thread);
                pr["headCommit"]!["nodes"]![0]!["commit"]!["statusCheckRollup"]!["contexts"] = Connection("contexts", cursor == "contexts:100",
                    i => JsonSerializer.SerializeToNode(new { id = $"check-{i}", name = $"check-{i}", status = "COMPLETED", conclusion = i == 100 ? "FAILURE" : "SUCCESS" })!);
                if (cursor is not null)
                {
                    var field = cursor[..cursor.IndexOf(':', StringComparison.Ordinal)];
                    Assert.Contains(field + "(first:100, after:$cursor)", query);
                    // Only the requested root connection may receive this cursor.
                    Assert.Equal(1, query.Split("after:$cursor", StringSplitOptions.None).Length - 1);
                }
                return Success(document.ToJsonString());
            }
            var endpoint = arguments[^1];
            var page = int.Parse(endpoint[(endpoint.LastIndexOf('=') + 1)..], CultureInfo.InvariantCulture);
            var sizeStart = endpoint.IndexOf("per_page=", StringComparison.Ordinal) + "per_page=".Length;
            var size = int.Parse(endpoint[sizeStart..endpoint.IndexOf('&', sizeStart)], CultureInfo.InvariantCulture);
            if (RequireSmallPages && size == 100) return Success(new string('x', 2 * 1024 * 1024));
            if (size < 100) SmallFilePages++;
            FilePages.Add(page);
            var files = Enumerable.Range((page - 1) * size, Math.Min(size, Math.Max(0, 401 - (page - 1) * size))).Select(i => new
            {
                filename = $"file-{i}.cs", status = "added", additions = i == 0 ? 30_000 : 1, deletions = 0,
                patch = i == 0 ? "@@ -0,0 +1,30000 @@\n" + string.Join('\n', Enumerable.Repeat("+added", 30_000)) : "@@ -0,0 +1,1 @@\n+added"
            });
            return Success(JsonSerializer.Serialize(files));
        }

        private static Task<(int ExitCode, string StandardOutput, string StandardError)> Success(string value) => Task.FromResult((0, value, ""));
        private static JsonNode Comment(string id) => JsonSerializer.SerializeToNode(new
        { id, body = $"Comment {id}", createdAt = "2026-09-06T00:00:00Z", submittedAt = "2026-09-06T00:00:00Z", author = new { login = "Author" } })!;
        private static JsonObject Thread(int index) => new()
        {
            ["id"] = $"thread-{index}", ["path"] = "file-0.cs", ["line"] = 1, ["diffSide"] = "RIGHT",
            ["isResolved"] = false, ["isOutdated"] = false, ["viewerCanReply"] = true,
            ["viewerCanResolve"] = true, ["viewerCanUnresolve"] = true,
            ["comments"] = index == 0 ? Connection("threadComments", false, i => Comment($"thread-0-comment-{i}")) : new JsonObject
            { ["nodes"] = new JsonArray(Comment($"thread-{index}-comment")), ["pageInfo"] = new JsonObject { ["hasNextPage"] = false } }
        };
        private static JsonObject Connection(string field, bool later, Func<int, JsonNode> node) => new()
        {
            ["nodes"] = new JsonArray(Enumerable.Range(later ? 100 : 0, later ? 1 : 100).Select(node).ToArray()),
            ["pageInfo"] = new JsonObject { ["hasNextPage"] = !later, ["endCursor"] = later ? null : $"{field}:100" }
        };
    }
}
