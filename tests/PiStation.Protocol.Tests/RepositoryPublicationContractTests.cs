using System.Text.Json;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Protocol.Tests;

public sealed class RepositoryPublicationContractTests
{
    [Fact]
    public void PublicationTargetAndProgressRoundTripAndOlderRequestsDefaultToCreation()
    {
        var request = new PublishHostedRepositoryRequest(ProjectId.New(), SourceControlProvider.AzureDevOps, "Team Project", "repo",
            OrganizationUrl: "https://dev.azure.com/station", ResumeExisting: true);
        Assert.Equal(request, JsonSerializer.Deserialize(JsonSerializer.Serialize(request, ProtocolJsonContext.Default.PublishHostedRepositoryRequest), ProtocolJsonContext.Default.PublishHostedRepositoryRequest));
        var result = new SourceControlOperationResult(false, "Resume publication", Publication: new(RepositoryPublicationStage.RemoteConfigured,
            "https://dev.azure.com/station/Team%20Project/_git/repo", "origin-1", "main"));
        Assert.Equal(result, JsonSerializer.Deserialize(JsonSerializer.Serialize(result, ProtocolJsonContext.Default.SourceControlOperationResult), ProtocolJsonContext.Default.SourceControlOperationResult));
        var older = JsonSerializer.Serialize(request, ProtocolJsonContext.Default.PublishHostedRepositoryRequest).Replace(",\"resumeExisting\":true", "", StringComparison.Ordinal);
        Assert.False(JsonSerializer.Deserialize(older, ProtocolJsonContext.Default.PublishHostedRepositoryRequest)!.ResumeExisting);
    }
}
