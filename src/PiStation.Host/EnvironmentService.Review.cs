using System.Text.Json;
using PiStation.Host.SourceControl;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host;

public sealed partial class EnvironmentService
{
    public Task<PullRequestReviewSnapshot> GetPullRequestReviewAsync(GetPullRequestReviewRequest request, CancellationToken cancellationToken = default) =>
        _sourceControl.GetPullRequestReviewAsync(request, cancellationToken);

    public Task<SourceControlOperationResult> SubmitPullRequestReviewAsync(SubmitPullRequestReviewRequest request, CancellationToken cancellationToken = default) =>
        new HostingOperationRunner(_database).RunAsync(request.OperationId, "Submit pull request review",
            JsonSerializer.Serialize(request, ProtocolJsonContext.Default.SubmitPullRequestReviewRequest),
            token => _sourceControl.SubmitPullRequestReviewAsync(request, token), cancellationToken);

    public Task<SourceControlOperationResult> ReplyPullRequestThreadAsync(ReplyPullRequestThreadRequest request, CancellationToken cancellationToken = default) =>
        new HostingOperationRunner(_database).RunAsync(request.OperationId, "Reply to pull request thread",
            JsonSerializer.Serialize(request, ProtocolJsonContext.Default.ReplyPullRequestThreadRequest),
            token => _sourceControl.ReplyPullRequestThreadAsync(request, token), cancellationToken);

    public Task<SourceControlOperationResult> SetPullRequestThreadResolvedAsync(SetPullRequestThreadResolvedRequest request, CancellationToken cancellationToken = default) =>
        new HostingOperationRunner(_database).RunAsync(request.OperationId, "Change pull request thread resolution",
            JsonSerializer.Serialize(request, ProtocolJsonContext.Default.SetPullRequestThreadResolvedRequest),
            token => _sourceControl.SetPullRequestThreadResolvedAsync(request, token), cancellationToken);
}
