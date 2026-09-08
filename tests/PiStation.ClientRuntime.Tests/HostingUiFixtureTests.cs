#if DEBUG
using System.Text.Json;
using PiStation.App.Composition;
using PiStation.Host.Hosting;
using PiStation.Host.SourceControl;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class HostingUiFixtureTests
{
    [Fact]
    public void FixtureRequiresExplicitIsolatedFakePiMode()
    {
        var root = Path.Combine(Path.GetTempPath(), "hosting-ui-guard");
        var fixture = Path.Combine(root, "fixture.json");
        Assert.Throws<ArgumentException>(() => AppLaunchOptions.Parse(["--ui-test-hosting-fixture", fixture]));
        Assert.Throws<ArgumentException>(() => AppLaunchOptions.Parse(["--ui-test", "--data-root", root, "--pi-executable", "fake.exe", "--fake-pi-scenario", "normal", "--ui-test-hosting-fixture", Path.Combine(root, "..", "other.json")]));
        Assert.Equal(fixture, AppLaunchOptions.Parse(["--ui-test", "--data-root", root, "--pi-executable", "fake.exe", "--fake-pi-scenario", "normal", "--ui-test-hosting-fixture", fixture]).UiTestHostingFixture);
    }

    [Fact]
    public async Task FixturePersistsNativeEditsAndReactionsAndRejectsUnsupportedCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiStation.HostingUiFixture", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "fixture.json");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "pull-request-review.json"), path);
            var fixture = new UiTestHostingFixture(path);
            var edit = await fixture.ExecuteAsync("gh", ["api", "graphql"], root,
                JsonSerializer.Serialize(new { query = "mutation($id:ID!){change:updatePullRequest(input:{}){clientMutationId}}", variables = new { id = "pr-id", title = "Edited in WinUI", body = "Saved description" } }), CancellationToken.None);
            Assert.Equal(0, edit.ExitCode);
            var reaction = await fixture.ExecuteAsync("gh", ["api", "graphql"], root,
                JsonSerializer.Serialize(new { query = "mutation($id:ID!){change:addReaction(input:{}){clientMutationId}}", variables = new { id = "comment-1", content = "EYES" } }), CancellationToken.None);
            Assert.Equal(0, reaction.ExitCode);
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            var pr = saved.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest");
            Assert.Equal("Edited in WinUI", pr.GetProperty("title").GetString());
            Assert.Equal("Saved description", pr.GetProperty("body").GetString());
            var eyes = pr.GetProperty("reviewThreads").GetProperty("nodes")[0].GetProperty("comments").GetProperty("nodes")[0].GetProperty("reactionGroups")[0];
            Assert.True(eyes.GetProperty("viewerHasReacted").GetBoolean());
            Assert.Equal(3, eyes.GetProperty("reactors").GetProperty("totalCount").GetInt32());
            Assert.NotEqual(0, (await fixture.ExecuteAsync("gh", ["repo", "create", "unrelated"], root, null, CancellationToken.None)).ExitCode);
        }
        finally { Directory.Delete(root, true); }
    }
}

public sealed partial class PullRequestReviewIntegrationTests
{
    [Fact]
    public async Task PopulatedUiFixtureRoundTripsThroughTheRealHostAndClient()
    {
        using var directory = new ClientTestDirectory();
        var projectPath = directory.CreateDirectory("hosting-ui-project");
        await GitAsync(projectPath, "init", "--initial-branch=main");
        await GitAsync(projectPath, "remote", "add", "origin", "https://github.com/pistation-fixture/review.git");
        var fixturePath = Path.Combine(directory.CreateDirectory("hosting-ui-data"), "fixture.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "pull-request-review.json"), fixturePath);
        var fixture = new UiTestHostingFixture(fixturePath);
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(),
            sourceControlFactory: (resolver, projects) => new SourceControlHostingService(resolver, projects, fixture.ExecuteAsync));
        await using var client = new EnvironmentClient(new() { HubAddress = host.HubAddress, BearerCredential = host.BearerCredential });
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new(projectPath));
        var workspace = new WorkspaceTarget(project.ProjectId);
        var snapshot = await client.GetPullRequestReviewAsync(new(workspace, "7"));
        Assert.Single(snapshot.Files);
        Assert.Single(snapshot.Checks);
        Assert.Single(snapshot.Discussions);
        Assert.Equal("Native GitHub review fixture", Assert.Single((await client.ListPullRequestsAsync(new(workspace))).PullRequests).Title);
        var target = new PullRequestReviewTarget(workspace, "github.com/pistation-fixture/review", "7", snapshot.HeadCommitId);

        await Manage(new(target, PullRequestManagementAction.EditDetails, Title: "Edited fixture", Body: "Saved description",
            ExpectedTitle: snapshot.PullRequest.Title, ExpectedBody: snapshot.Body));
        await Manage(new(target, PullRequestManagementAction.SetDraft, IsDraft: true, ExpectedIsDraft: false));
        Assert.True((await client.GetPullRequestReviewAsync(new(workspace, "7"))).PullRequest.IsDraft);
        await Manage(new(target, PullRequestManagementAction.SetDraft, IsDraft: false, ExpectedIsDraft: true));
        await Manage(new(target, PullRequestManagementAction.EditComment, ItemId: "comment-1", Body: "Saved comment", ExpectedBody: "A comment"));
        await Manage(new(target, PullRequestManagementAction.SetReaction, ItemId: "comment-1", Reaction: PullRequestReactionContent.Eyes, Reacted: true));
        await Manage(new(target, PullRequestManagementAction.SetReaction, Reaction: PullRequestReactionContent.Heart, Reacted: false));
        await Manage(new(target, PullRequestManagementAction.RemoveLabel, ItemId: "component/ui"));
        await Manage(new(target, PullRequestManagementAction.RemoveReviewer, ItemId: "other-reviewer"));
        snapshot = await client.GetPullRequestReviewAsync(new(workspace, "7"));
        Assert.Equal("Edited fixture", snapshot.PullRequest.Title);
        Assert.Equal("Saved description", snapshot.Body);
        Assert.Equal("bug", Assert.Single(snapshot.PullRequest.Labels));
        Assert.Empty(snapshot.PullRequest.Reviewers);
        var comment = Assert.Single(Assert.Single(snapshot.Discussions).Comments);
        Assert.Equal("Saved comment", comment.Body);
        Assert.Equal(new(PullRequestReactionContent.Eyes, 3, true), Assert.Single(comment.Reactions!));
        Assert.Equal(new(PullRequestReactionContent.Heart, 2, false), Assert.Single(snapshot.Reactions!));
        await Manage(new(target, PullRequestManagementAction.EnableAutoMerge, MergeMethod: PullRequestMergeMethod.Squash));
        Assert.True((await client.GetPullRequestReviewAsync(new(workspace, "7"))).Advanced!.AutoMergeEnabled);
        await Manage(new(target, PullRequestManagementAction.DisableAutoMerge));
        Assert.False((await client.GetPullRequestReviewAsync(new(workspace, "7"))).Advanced!.AutoMergeEnabled);
        Assert.Single((await client.GetPullRequestWorkflowsAsync(new(target))).Workflows);
        await Manage(new(target, PullRequestManagementAction.ApproveWorkflow, ItemId: "91"));
        Assert.Empty((await client.GetPullRequestWorkflowsAsync(new(target))).Workflows);
        await Manage(new(target, PullRequestManagementAction.DeleteComment, ItemId: "comment-1", ExpectedBody: "Saved comment"));
        Assert.Empty((await client.GetPullRequestReviewAsync(new(workspace, "7"))).Discussions.SelectMany(discussion => discussion.Comments));
        await Manage(new(target, PullRequestManagementAction.UpdateBranch, UpdateMethod: PullRequestUpdateMethod.Merge));
        snapshot = await client.GetPullRequestReviewAsync(new(workspace, "7"));
        Assert.NotEqual(target.HeadCommitId, snapshot.HeadCommitId);
        target = target with { HeadCommitId = snapshot.HeadCommitId };
        await Manage(new(target, PullRequestManagementAction.Merge, MergeMethod: PullRequestMergeMethod.Squash));
        Assert.Equal(PullRequestState.Merged, (await client.GetPullRequestReviewAsync(new(workspace, "7"))).PullRequest.State);
        var reverted = await client.ManagePullRequestAsync(new(target, PullRequestManagementAction.Revert, OperationId: CommandId.New()));
        Assert.True(reverted.Succeeded, reverted.Message);
        Assert.Contains("/pull/8", reverted.Message);

        async Task Manage(ManagePullRequestRequest request)
        {
            request = request with { OperationId = CommandId.New() };
            var result = await client.ManagePullRequestAsync(request);
            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(result, await client.ManagePullRequestAsync(request));
        }
    }
}
#else
using PiStation.App.Composition;

namespace PiStation.ClientRuntime.Tests;

public sealed class HostingUiFixtureTests
{
    [Fact]
    public void FixtureIsDisabledInReleaseBuilds()
    {
        var root = Path.Combine(Path.GetTempPath(), "hosting-ui-release-guard");
        Assert.Throws<InvalidOperationException>(() => AppLaunchOptions.Parse(["--ui-test", "--data-root", root,
            "--pi-executable", "fake.exe", "--fake-pi-scenario", "normal", "--ui-test-hosting-fixture", Path.Combine(root, "fixture.json")]));
    }
}
#endif
