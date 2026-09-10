using System.Text.Json;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Serialization;

namespace PiStation.Protocol.Tests;

public sealed class ProviderReviewContractTests
{
    [Fact]
    public void PartialReviewProgressRoundTripsThroughDurableResults()
    {
        var result = new SourceControlOperationResult(false, "Inspect partial review", State: CommandReceiptState.DispatchUncertain,
            ReviewProgress: new(2, 3, "Review summary"));
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(result, ProtocolJsonContext.Default.SourceControlOperationResult), ProtocolJsonContext.Default.SourceControlOperationResult);
        Assert.Equal(result, restored);
    }

    [Fact]
    public void AzureReviewIdentityIncludesOrganizationAndKeepsGitHubKeysStable()
    {
        var azure = new SourceControlRepository(SourceControlProvider.AzureDevOps, "dev.azure.com", "project", "repo", "https://dev.azure.com/one/project/_git/repo", "", "main", true);
        Assert.NotEqual(PullRequestReviewDefaults.RepositoryKey(azure), PullRequestReviewDefaults.RepositoryKey(azure with { WebUrl = "https://dev.azure.com/two/project/_git/repo" }));
        var github = azure with { Provider = SourceControlProvider.GitHub, Host = "github.com", Owner = "owner" };
        Assert.Equal("github.com/owner/repo", PullRequestReviewDefaults.RepositoryKey(github));
        Assert.False(HostingCapabilities.Review(SourceControlProvider.AzureDevOps).Diff);
        Assert.DoesNotContain(PullRequestReviewEvent.RequestChanges, HostingCapabilities.Review(SourceControlProvider.GitLab).Verdicts);
        Assert.Equal(PullRequestReviewDefaults.HostingAuthority(SourceControlProvider.AzureDevOps, "https://one.visualstudio.com/project/_git/repo"),
            PullRequestReviewDefaults.HostingAuthority(SourceControlProvider.AzureDevOps, "https://dev.azure.com/one/_apis/git/repositories/id"));
        Assert.NotEqual(PullRequestReviewDefaults.HostingAuthority(SourceControlProvider.AzureDevOps, azure.WebUrl),
            PullRequestReviewDefaults.HostingAuthority(SourceControlProvider.AzureDevOps, "https://dev.azure.com/two/project/_git/repo"));
    }
}
