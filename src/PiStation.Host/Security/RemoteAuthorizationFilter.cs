using Microsoft.AspNetCore.SignalR;
using PiStation.Host.Hubs;
using PiStation.Protocol.Models;

namespace PiStation.Host.Security;

public sealed class RemoteAuthorizationFilter : IHubFilter
{
    internal const string AuthorizationItem = "PiStation.RemoteAuthorization";
    internal const string ReadOnlyItem = "PiStation.RemoteReadOnly";
    private const string LifetimeItem = "PiStation.RemoteLifetime";
    private const string ActivityItem = "PiStation.RemoteActivity";

    // An explicit list makes new RPCs inaccessible remotely until their policy is chosen.
    public static IReadOnlyDictionary<string, RemoteAccessLevel> MethodAccess { get; } =
        new Dictionary<string, RemoteAccessLevel>(StringComparer.Ordinal)
        {
            [nameof(EnvironmentHub.GetEnvironmentDescriptor)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.ListProjects)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.SubscribeCatalog)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.GetRemoteUpdateDescriptor)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.GetRemoteUpdateReceipt)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.GetRemoteUpdateHistory)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.PrepareRemoteUpdate)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.CommitRemoteUpdate)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.CancelRemoteUpdate)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.DiscoverProjectPreviewServers)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.OpenPreview)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.OpenBrowserAutomation)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.PollBrowserAutomation)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.CompleteBrowserAutomation)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.CloseBrowserAutomation)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.RenewPreview)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.ClosePreview)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.SearchProjectFiles)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.ReadProjectFile)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.ListProjectEntries)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.SearchProjectContents)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.ReadProjectFileAsset)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.GetProjectChanges)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.GetProjectChangeDiff)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.ListGitRefs)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.ListGitWorktrees)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.GetWorkspaceGitCommandResult)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.GetThreadCheckpointDiff)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.ListTerminalSessions)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.ListThreads)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.GetThread)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.SearchThreads)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.SearchGlobal)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.GetThreadDraft)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.GetThreadPiConfiguration)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.GetCommandReceipt)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.SubscribeThread)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.SubscribeTerminal)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.AddProject)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.SetProjectScriptsTrust)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.RunProjectSetupScript)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.SaveProjectFile)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.ExecuteWorkspaceGitCommand)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.StartTerminalSession)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.WriteTerminalInput)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.ResizeTerminalSession)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.StopTerminalSession)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.CloseTerminalSession)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.ClearTerminalHistory)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.CreateThread)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.ExecuteThreadCommand)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.GetSourceControlWritingSettings)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.GetPiAutomationSettings)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.GetPiAutomationStatus)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.GetSettlementSettings)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.ListPromptStashes)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.DetectSourceControl)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.GetPullRequestReview)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.ListPullRequests)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.GetDiagnostics)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.ListHostingOperations)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.ReadArtifactFile)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.SaveSourceControlWritingSettings)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.SavePiAutomationSettings)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.ApplyPiAutomation)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.SaveSettlementSettings)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.SubmitBackgroundTask)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.RemoveProject)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.UpdateProjectDefaults)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.UpdateProjectIcons)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.RunProjectScript)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.GetComposerDiscovery)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.SavePromptStash)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.DeletePromptStash)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.SubmitPullRequestReview)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.ReplyPullRequestThread)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.SetPullRequestThreadResolved)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.CloneHostedRepository)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.PublishHostedRepository)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.CreatePullRequest)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.MutatePullRequest)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.GenerateSourceControlText)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.ConfigurePiRuntime)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.GetPiRuntimeConfiguration)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.DownloadDiagnostics)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.BrowseHostPath)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.ReadProjectIcon)] = RemoteAccessLevel.ReadOnly,
            [nameof(EnvironmentHub.ManagePiResources)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.BrowsePiSessions)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.InspectPiSession)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.InspectPiSessionPage)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.CopyPiSession)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.NavigatePiSession)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.SetPiSessionLabel)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.CancelPiSessionNavigation)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.ExportPiSession)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.StartPiSetup)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.ExportDiagnostics)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.DeleteThread)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.ApplyThreadBulkOperation)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.SetThreadPinnedOrder)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.LinkThreadPullRequest)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.CreatePullRequestReviewThread)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.ManagePullRequest)] = RemoteAccessLevel.Operate,
            [nameof(EnvironmentHub.GetPullRequestWorkflows)] = RemoteAccessLevel.ReadOnly,
        };

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var context = invocationContext;
        var authorization = GetAuthorization(context.Context);
        if (authorization is null || !authorization.IsActive ||
            authorization.Device.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            context.Context.Abort();
            throw new HubException("Remote access has expired or been revoked.");
        }
        if (!MethodAccess.TryGetValue(context.HubMethodName, out var level) || authorization.Device.AccessLevel < level)
            throw new HubException("This operation is not permitted for this remote connection.");
        // Keep the access mode scoped to this SignalR connection.  The service must never
        // infer it from global state because local/operate calls may run concurrently.
        context.Context.Items[ReadOnlyItem] = authorization.Device.AccessLevel == RemoteAccessLevel.ReadOnly;
        var result = await next(context).ConfigureAwait(false);
        if (result is EnvironmentDescriptor descriptor)
        {
            var capabilities = descriptor.Capabilities.Where(c => c != "editor.open");
            if (authorization.Device.AccessLevel == RemoteAccessLevel.ReadOnly)
                capabilities = capabilities.Where(c => c.EndsWith(".read", StringComparison.Ordinal) ||
                    c.Contains("search", StringComparison.Ordinal) || c is "file.artifacts" or "file.assets" or "git.refs" or "git.worktrees" or "attachment.download");
            return descriptor with { Capabilities = capabilities.Append("remote.access").ToArray() };
        }
        return result;
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var authorization = GetAuthorization(context.Context) ?? throw new HubException("Remote authorization is required.");
        if (authorization.TrackConnection is { } track) context.Context.Items[ActivityItem] = track(context.Context.ConnectionId);
        context.Context.Items[ReadOnlyItem] = authorization.Device.AccessLevel == RemoteAccessLevel.ReadOnly;
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(authorization.Revoked);
        var remaining = authorization.Device.ExpiresAt - DateTimeOffset.UtcNow;
        // Timer limits are smaller than the device lifetime; expiry is also checked per invocation.
        var expiryTimer = new Timer(_ =>
        {
            if (authorization.Device.ExpiresAt <= DateTimeOffset.UtcNow) context.Context.Abort();
        }, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        var registration = lifetime.Token.Register(context.Context.Abort);
        context.Context.Items[LifetimeItem] = (lifetime, registration, expiryTimer);
        if (remaining <= TimeSpan.Zero) context.Context.Abort();
        await next(context).ConfigureAwait(false);
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        if (context.Context.Items.Remove(ActivityItem, out var activity) && activity is IDisposable tracking) tracking.Dispose();
        if (context.Context.Items.Remove(LifetimeItem, out var value) &&
            value is ValueTuple<CancellationTokenSource, CancellationTokenRegistration, Timer> lifetime)
        {
            lifetime.Item2.Dispose();
            lifetime.Item3.Dispose();
            lifetime.Item1.Dispose();
        }
        await next(context, exception).ConfigureAwait(false);
    }

    private static RemoteAuthorization? GetAuthorization(HubCallerContext context) =>
        context.GetHttpContext()?.Items[AuthorizationItem] as RemoteAuthorization;
}
