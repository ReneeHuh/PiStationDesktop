using System.Text.Json;
using System.Diagnostics;
using PiStation.Host.SourceControl;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host;

public sealed partial class EnvironmentService
{
    private int _linkedPullRequestRefreshOffset;
    public Task<PullRequestWorkflowsResult> GetPullRequestWorkflowsAsync(GetPullRequestWorkflowsRequest request, CancellationToken cancellationToken = default) =>
        _sourceControl.GetPullRequestWorkflowsAsync(request, cancellationToken);

    public Task<SourceControlOperationResult> ManagePullRequestAsync(ManagePullRequestRequest request, CancellationToken cancellationToken = default) =>
        new HostingOperationRunner(_database).RunAsync(request.OperationId, "Manage pull request",
            JsonSerializer.Serialize(request, ProtocolJsonContext.Default.ManagePullRequestRequest),
            token => _sourceControl.ManagePullRequestAsync(request, token), cancellationToken);

    public async Task<ThreadDescriptor> CreatePullRequestReviewThreadAsync(
        CreatePullRequestReviewThreadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(request.Target.Workspace);
        var snapshot = await _sourceControl.GetPullRequestReviewAsync(
            new(request.Target.Workspace, request.Target.Number), cancellationToken).ConfigureAwait(false);
        return await _gitCommands.CreatePullRequestReviewThreadAsync(request, snapshot, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PullRequestReviewSnapshot> GetPullRequestReviewAsync(GetPullRequestReviewRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = await _sourceControl.GetPullRequestReviewAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.Page is null) await _database.RefreshPullRequestLinksAsync(request.Target.ProjectId, snapshot.PullRequest, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private async Task RefreshLinkedPullRequestsAsync(CancellationToken cancellationToken)
    {
        var pending = new List<(ProjectId ProjectId, PullRequestLink Link)>();
        foreach (var project in await _database.ListProjectsAsync(cancellationToken).ConfigureAwait(false))
        {
            var links = new List<PullRequestLink>();
            foreach (var record in await _database.ListThreadsAsync(project.ProjectId, includeArchived: true, cancellationToken).ConfigureAwait(false))
                if ((await _database.EnrichThreadDescriptorAsync(record, cancellationToken).ConfigureAwait(false)).PullRequest is { } link)
                    links.Add(link);
            foreach (var link in links.DistinctBy(link => (link.Provider, link.Repository, link.Number, link.Url)))
                pending.Add((project.ProjectId, link));
        }
        // Rotate bounded batches so one offline repository cannot starve the rest or the settlement worker.
        var started = Stopwatch.GetTimestamp();
        for (var attempt = 0; attempt < Math.Min(20, pending.Count); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = TimeSpan.FromSeconds(45) - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) break;
            var (projectId, link) = pending[_linkedPullRequestRefreshOffset % pending.Count];
            _linkedPullRequestRefreshOffset = (_linkedPullRequestRefreshOffset + 1) % pending.Count;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(remaining < TimeSpan.FromSeconds(15) ? remaining : TimeSpan.FromSeconds(15));
            try
            {
                var current = await _sourceControl.GetPullRequestAsync(new(projectId), link.Number, timeout.Token).ConfigureAwait(false);
                await _database.RefreshPullRequestLinksAsync(projectId, current, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { /* Keep the last confirmed status on provider failures. */ }
        }
    }

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
