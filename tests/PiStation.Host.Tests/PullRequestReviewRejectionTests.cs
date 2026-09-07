using System.Text.Json;
using PiStation.Host.SourceControl;

namespace PiStation.Host.Tests;

public sealed class PullRequestReviewRejectionTests
{
    [Fact]
    public void IncludedHttpResponseBodyAndStatusAreCaptured()
    {
        const string response = "HTTP/2.0 422 Unprocessable Entity\r\ncontent-type: application/json\r\n\r\n{\"message\":\"Validation Failed\"}";

        Assert.Equal(422, SourceControlHostingService.FindHttpStatus(response)!.Value);
        Assert.Equal("{\"message\":\"Validation Failed\"}", SourceControlHostingService.ReviewResponseBody(response));
    }

    [Fact]
    public void GraphQlPermissionFailureIsDefinitiveOnlyWhenMutationHasNoData()
    {
        using var noData = JsonDocument.Parse("{\"data\":null,\"errors\":[{\"type\":\"FORBIDDEN\",\"message\":\"Resource not accessible\"}]}");
        using var nullMutation = JsonDocument.Parse("{\"data\":{\"addPullRequestReviewThreadReply\":null},\"errors\":[{\"type\":\"FORBIDDEN\",\"message\":\"Resource not accessible\"}]}");
        using var partialData = JsonDocument.Parse("{\"data\":{\"addPullRequestReviewThreadReply\":{\"comment\":null}},\"errors\":[{\"type\":\"FORBIDDEN\",\"message\":\"Resource not accessible\"}]}");

        Assert.True(SourceControlHostingService.IsDefinitiveGraphQlFailure(noData.RootElement));
        Assert.True(SourceControlHostingService.IsDefinitiveGraphQlFailure(nullMutation.RootElement));
        Assert.False(SourceControlHostingService.IsDefinitiveGraphQlFailure(partialData.RootElement));
    }

    [Fact]
    public void HttpStatusInsideJsonBodyIsNotTreatedAsAResponseHeader()
    {
        const string body = "{\"message\":\"failed\\nHTTP/2.0 422 Unprocessable Entity\"}";

        Assert.Null(SourceControlHostingService.FindHttpStatus(body));
        Assert.Equal(body, SourceControlHostingService.ReviewResponseBody(body));
    }

    [Theory]
    [InlineData("HTTP/2.0 403 Forbidden", 403)]
    [InlineData("HTTP/1.1 503 Service Unavailable", 503)]
    public void IncludedHttpStatusUsesTheFinalResponse(string headers, int status)
    {
        Assert.Equal(status, SourceControlHostingService.FindHttpStatus(headers)!.Value);
    }

    [Fact]
    public void MixedGraphQlErrorsRemainUncertainAndHeaderlessResponseBodyIsPreserved()
    {
        using var mixed = JsonDocument.Parse("""{"data":null,"errors":[{"type":"FORBIDDEN"},{"type":"INTERNAL"}]}""");
        Assert.False(SourceControlHostingService.IsDefinitiveGraphQlFailure(mixed.RootElement));
        const string response = "HTTP/2.0 200 OK\r\n\r\n{\"data\":{}}";
        Assert.Equal(200, SourceControlHostingService.FindHttpStatus(response));
        Assert.Equal("{\"data\":{}}", SourceControlHostingService.ReviewResponseBody(response));
    }
}
