using System.Globalization;
using PiStation.Host.SourceControl;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class SourceControlHostingServiceTests
{
    [Fact]
    public void PullRequestPagesAndBranchLookupsUseProviderPagingArguments()
    {
        var github = SourceControlHostingService.BuildListCommand(SourceControlProvider.GitHub, null, 100, "feature/old").Arguments;
        Assert.Equal("201", github[Array.IndexOf(github, "--limit") + 1]);
        Assert.Equal("feature/old", github[Array.IndexOf(github, "--head") + 1]);
        var gitlab = SourceControlHostingService.BuildListCommand(SourceControlProvider.GitLab, PullRequestState.Closed, 200, "feature/old").Arguments;
        Assert.Equal("3", gitlab[Array.IndexOf(gitlab, "--page") + 1]); Assert.Contains("--source-branch", gitlab);
        var azure = SourceControlHostingService.BuildListCommand(SourceControlProvider.AzureDevOps, null, 100).Arguments;
        Assert.Equal("100", azure[Array.IndexOf(azure, "--skip") + 1]);
    }

    [Theory]
    [InlineData("git@github.com:openai/example.git", SourceControlProvider.GitHub, "openai", "example", "https://github.com/openai/example")]
    [InlineData("https://gitlab.com/group/example.git", SourceControlProvider.GitLab, "group", "example", "https://gitlab.com/group/example")]
    [InlineData("https://bitbucket.org/team/example.git", SourceControlProvider.Bitbucket, "team", "example", "https://bitbucket.org/team/example")]
    [InlineData("https://dev.azure.com/org/project/_git/example", SourceControlProvider.AzureDevOps, "project", "example", "https://dev.azure.com/org/project/_git/example")]
    public void HostedRemoteParsingPreservesProviderIdentityAndWebUrl(
        string remote,
        SourceControlProvider provider,
        string owner,
        string name,
        string webUrl)
    {
        var parsed = SourceControlHostingService.ParseRemote(remote);

        Assert.Equal(provider, parsed.Provider);
        Assert.Equal(owner, parsed.Owner);
        Assert.Equal(name, parsed.Name);
        Assert.Equal(webUrl, parsed.WebUrl);
    }

    [Fact]
    public void ProviderCommandsUseArgumentListsAndExpectedNativeTools()
    {
        var target = new WorkspaceTarget(ProjectId.New());
        var create = new CreatePullRequestRequest(target, "Title with spaces", "Body with spaces", "feature", "main", true);

        var github = SourceControlHostingService.BuildCreateCommand(SourceControlProvider.GitHub, create);
        var gitlab = SourceControlHostingService.BuildCreateCommand(SourceControlProvider.GitLab, create);
        var azure = SourceControlHostingService.BuildCreateCommand(SourceControlProvider.AzureDevOps, create);

        Assert.Equal("gh", github.FileName);
        Assert.Contains("--draft", github.Arguments);
        Assert.Equal("Title with spaces", github.Arguments[Array.IndexOf(github.Arguments, "--title") + 1]);
        Assert.Equal("glab", gitlab.FileName);
        Assert.Contains("--source-branch", gitlab.Arguments);
        Assert.Equal("az", azure.FileName);
        Assert.Contains("--source-branch", azure.Arguments);

        var mutation = SourceControlHostingService.BuildMutationCommand(
            SourceControlProvider.GitHub,
            new MutatePullRequestRequest(target, "42", PullRequestMutationKind.AddReviewer, "octocat"));
        Assert.Equal(["pr", "edit", "42", "--add-reviewer", "octocat"], mutation.Arguments);
    }

    [Fact]
    public void UnsupportedProviderActionsCannotBeDispatchedAndGitlabUsesNativeStateFlags()
    {
        var closed = SourceControlHostingService.BuildListCommand(SourceControlProvider.GitLab, PullRequestState.Closed);
        Assert.Contains("--closed", closed.Arguments);
        Assert.DoesNotContain("--state", closed.Arguments);
        Assert.Contains("--all", SourceControlHostingService.BuildListCommand(SourceControlProvider.GitLab, null).Arguments);
        Assert.False(HostingCapabilities.CanList(SourceControlProvider.Bitbucket));
        Assert.False(HostingCapabilities.CanMutate(SourceControlProvider.AzureDevOps, PullRequestMutationKind.Comment));
        Assert.False(HostingCapabilities.CanMutate(SourceControlProvider.GitLab, PullRequestMutationKind.RequestChanges));
        Assert.Throws<PiStation.Host.Errors.HostOperationException>(() => SourceControlHostingService.BuildPublishCommand(
            new PublishHostedRepositoryRequest(ProjectId.New(), SourceControlProvider.AzureDevOps, "project", "repo"), "workspace"));
        var nested = SourceControlHostingService.ParseRemote("https://gitlab.com/group/subgroup/repo.git");
        Assert.Equal("group/subgroup", nested.Owner);
    }

    [Fact]
    public void PullRequestJsonNormalizesGithubGitlabAzureAndBitbucketShapes()
    {
        var updated = "2026-09-04T12:00:00Z";
        var fixtures = new[]
        {
            (
                Repository(SourceControlProvider.GitHub),
                """[{"number":12,"title":"GitHub PR","url":"https://github.test/pr/12","state":"OPEN","author":{"login":"hub"},"headRefName":"feature/hub","baseRefName":"main","labels":[{"name":"ready"}],"reviewRequests":[{"login":"reviewer"}],"statusCheckRollup":[{"conclusion":"SUCCESS"}],"updatedAt":"$UPDATED$"}]""".Replace("$UPDATED$", updated, StringComparison.Ordinal),
                "12", "hub", "feature/hub", PullRequestCheckState.Passed),
            (
                Repository(SourceControlProvider.GitLab),
                """[{"iid":13,"title":"GitLab MR","web_url":"https://gitlab.test/mr/13","state":"opened","author":{"username":"lab"},"source_branch":"feature/lab","target_branch":"main","updated_at":"$UPDATED$"}]""".Replace("$UPDATED$", updated, StringComparison.Ordinal),
                "13", "lab", "feature/lab", PullRequestCheckState.Unknown),
            (
                Repository(SourceControlProvider.AzureDevOps),
                """[{"pullRequestId":14,"title":"Azure PR","status":"active","createdBy":{"displayName":"azure"},"sourceRefName":"refs/heads/feature/azure","targetRefName":"refs/heads/main","creationDate":"$UPDATED$"}]""".Replace("$UPDATED$", updated, StringComparison.Ordinal),
                "14", "azure", "feature/azure", PullRequestCheckState.Unknown),
            (
                Repository(SourceControlProvider.Bitbucket),
                """{"values":[{"id":15,"title":"Bitbucket PR","state":"OPEN","author":{"display_name":"bucket"},"source":{"branch":{"name":"feature/bucket"}},"destination":{"branch":{"name":"main"}},"links":{"html":{"href":"https://bitbucket.test/pr/15"}},"updated_on":"$UPDATED$"}]}""".Replace("$UPDATED$", updated, StringComparison.Ordinal),
                "15", "bucket", "feature/bucket", PullRequestCheckState.Unknown),
        };

        foreach (var (repository, json, number, author, sourceBranch, checks) in fixtures)
        {
            var pullRequest = Assert.Single(SourceControlHostingService.ParsePullRequests(repository, json));
            Assert.Equal(number, pullRequest.Number);
            Assert.Equal(author, pullRequest.Author);
            Assert.Equal(sourceBranch, pullRequest.SourceBranch);
            Assert.Equal("main", pullRequest.TargetBranch);
            Assert.Equal(checks, pullRequest.Checks);
            Assert.Equal(DateTimeOffset.Parse(updated, CultureInfo.InvariantCulture), pullRequest.UpdatedUtc);
        }
    }

    [Fact]
    public void SettlementUsesClosureTimestampNotLaterCommentsAndMissingClosureStaysUnknown()
    {
        var repository = Repository(SourceControlProvider.GitHub);
        var withDate = SourceControlHostingService.ParsePullRequests(repository,
            """[{"number":1,"state":"MERGED","mergedAt":"2026-09-01T00:00:00Z","updatedAt":"2026-09-07T00:00:00Z"}]""");
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T00:00:00Z", CultureInfo.InvariantCulture), Assert.Single(withDate).ClosedOrMergedUtc);
        var withoutDate = SourceControlHostingService.ParsePullRequests(repository,
            """[{"number":1,"state":"CLOSED","updatedAt":"2026-09-07T00:00:00Z"}]""");
        Assert.Null(Assert.Single(withoutDate).ClosedOrMergedUtc);
        var command = SourceControlHostingService.BuildListCommand(SourceControlProvider.GitHub, PullRequestState.Merged);
        Assert.Contains("merged", command.Arguments);
        Assert.Contains("mergedAt", command.Arguments[^1], StringComparison.Ordinal);
    }

    private static SourceControlRepository Repository(SourceControlProvider provider) => new(
        provider,
        $"{provider.ToString().ToLowerInvariant()}.test",
        "owner",
        "repo",
        $"https://{provider.ToString().ToLowerInvariant()}.test/owner/repo",
        $"https://{provider.ToString().ToLowerInvariant()}.test/owner/repo.git",
        "main",
        true);
}
