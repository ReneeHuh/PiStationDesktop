using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Persistence;

public sealed partial class HostDatabase
{
    internal async Task RefreshPullRequestLinksAsync(ProjectId projectId, PullRequestDescriptor pullRequest, CancellationToken cancellationToken)
    {
        foreach (var record in await ListThreadsAsync(projectId, includeArchived: true, cancellationToken).ConfigureAwait(false))
        {
            var thread = await EnrichThreadDescriptorAsync(record, cancellationToken).ConfigureAwait(false);
            if (thread.PullRequest is not { } link || link.Provider != pullRequest.Provider || link.Number != pullRequest.Number ||
                !string.Equals(link.Repository, pullRequest.Repository, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(link.Url.TrimEnd('/'), pullRequest.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                link.UpdatedUtc > pullRequest.UpdatedUtc) continue;
            var refreshed = new PullRequestLink(pullRequest.Provider, pullRequest.Repository, pullRequest.Number,
                pullRequest.Url, pullRequest.State.ToString(), pullRequest.Title, pullRequest.UpdatedUtc);
            if (refreshed == link) continue;
            // A concurrent relink or metadata edit wins. The next refresh can retry.
            await UpdateThreadInboxAsync(thread.ThreadId, thread.Revision, pullRequest: refreshed,
                updatePullRequest: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
