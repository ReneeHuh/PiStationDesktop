using PiStation.ClientRuntime;

namespace PiStation.App.ViewModels;

internal sealed record PullRequestInboxConnection(PullRequestInboxSource Source, ShellViewModel Owner, IEnvironmentClient Client, string EnvironmentIdentity);

public sealed partial class ShellViewModel
{
    private static readonly List<WeakReference<ShellViewModel>> HostingWindows = [];
    internal bool IsHostingReviewOpen { get; set; }

    private void RegisterHostingWindow()
    {
        lock (HostingWindows)
        {
            HostingWindows.RemoveAll(reference => !reference.TryGetTarget(out _));
            HostingWindows.Add(new(this));
        }
    }

    internal static IReadOnlyList<PullRequestInboxConnection> ConnectedPullRequestSources()
    {
        lock (HostingWindows)
        {
            return HostingWindows.Select(reference => reference.TryGetTarget(out var owner) ? owner : null)
                .Where(owner => owner?.IsConnected == true && owner._client?.Descriptor is not null)
                .Select(owner => owner!.CreatePullRequestSource()).DistinctBy(connection => connection.Source.Id).ToArray();
        }
    }

    private PullRequestInboxConnection CreatePullRequestSource()
    {
        var client = RequireClient();
        var identity = client.Descriptor!.EnvironmentId;
        IEnvironmentClient Current()
        {
            if (!IsConnected || !ReferenceEquals(_client, client) || client.Descriptor?.EnvironmentId != identity)
                throw new InvalidOperationException("The originating environment disconnected. Reconnect and refresh the inbox.");
            return client;
        }
        // Different connection profiles can reach the same host with different access levels and draft roots.
        var scope = identity.Value + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(BrowserAutomationRoot).ToUpperInvariant())));
        var source = new PullRequestInboxSource(scope, IsRemote ? EnvironmentLabel : client.Descriptor.EnvironmentName,
            token => Current().ListProjectsAsync(token),
            (request, token) => Current().DetectSourceControlAsync(request, token),
            (request, token) => Current().ListPullRequestsAsync(request, token));
        return new(source, this, client, identity.Value);
    }

    internal PullRequestReviewViewModel CreateInboxReview(PullRequestInboxConnection connection)
    {
        if (connection.Owner != this || IsHostingReviewOpen)
            throw new InvalidOperationException("Close the review already open in this environment before opening another review.");
        IEnvironmentClient Current()
        {
            if (!IsConnected || !ReferenceEquals(_client, connection.Client) || _client.Descriptor?.EnvironmentId.Value != connection.EnvironmentIdentity)
                throw new InvalidOperationException("The originating environment disconnected. Reconnect and refresh the inbox.");
            return connection.Client;
        }
        _ = Current();
        IsHostingReviewOpen = true;
        return new PullRequestReviewViewModel(Current,
            new PullRequestReviewDraftStore(Path.Combine(Path.GetDirectoryName(BrowserAutomationRoot)!, "pr-review-drafts")),
            () => IsConnected && CanOperate && ReferenceEquals(_client, connection.Client));
    }
}
