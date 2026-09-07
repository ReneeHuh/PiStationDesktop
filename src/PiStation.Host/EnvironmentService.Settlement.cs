using PiStation.Protocol.Models;
using PiStation.Host.Threads;

namespace PiStation.Host;

public sealed partial class EnvironmentService
{
    private readonly CancellationTokenSource _settlementShutdown = new();
    private readonly SemaphoreSlim _settlementGate = new(1, 1);
    private Task? _settlementWorker;

    public Task<SettlementSettings> GetSettlementSettingsAsync(CancellationToken token = default) => _database.GetSettlementSettingsAsync(token);

    public async Task SaveSettlementSettingsAsync(SettlementSettings settings, CancellationToken token = default)
    {
        await _settlementGate.WaitAsync(token).ConfigureAwait(false);
        try { await _database.SaveSettlementSettingsAsync(settings, token).ConfigureAwait(false); }
        finally { _settlementGate.Release(); }
        // The next minute's sweep applies changes; settled threads are never reopened by settings changes.
    }

    private async Task RunSettlementWorkerAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do
            {
                try { await SweepThreadSettlementAsync(DateTimeOffset.UtcNow, _settlementShutdown.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_settlementShutdown.IsCancellationRequested) { break; }
                catch (Exception exception) { _diagnostics.Record($"Thread settlement sweep failed: {exception.Message}"); }
            } while (await timer.WaitForNextTickAsync(_settlementShutdown.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_settlementShutdown.IsCancellationRequested) { }
    }

    public async Task<int> SweepThreadSettlementAsync(DateTimeOffset now, CancellationToken token = default)
    {
        await _settlementGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var settings = await _database.GetSettlementSettingsAsync(token).ConfigureAwait(false);
            var count = 0;
            foreach (var project in await _database.ListProjectsAsync(token).ConfigureAwait(false))
            {
                ListPullRequestsResult? pullRequests = null;
                var lookupAttempted = false;
                foreach (var record in await _database.ListThreadsAsync(project.ProjectId, token).ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var thread = await _database.EnrichThreadDescriptorAsync(record, token).ConfigureAwait(false);
                        var activity = await _database.GetSettlementActivityAsync(thread.ThreadId, token).ConfigureAwait(false);
                        if (activity.LastActivity is null || thread.IsSettled || activity.Protected || thread.SnoozedUntilUtc > now ||
                            thread.SetupScriptState is SetupScriptState.Pending or SetupScriptState.Running) continue;
                        _threads.TryGetController(thread.ThreadId, out var controller);
                        if (ThreadSettlementPolicy.HasLiveWork(controller?.Journal.Projection)) continue;
                        if (!lookupAttempted && (settings.OnMerge || settings.OnClose) && (thread.PullRequest is not null || thread.BranchName is not null))
                        {
                            lookupAttempted = true;
                            using var lookupTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                            lookupTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                            try
                            {
                                var merged = await _sourceControl.ListPullRequestsAsync(new(new(project.ProjectId), PullRequestState.Merged), lookupTimeout.Token).ConfigureAwait(false);
                                var closed = await _sourceControl.ListPullRequestsAsync(new(new(project.ProjectId), PullRequestState.Closed), lookupTimeout.Token).ConfigureAwait(false);
                                pullRequests = merged with { PullRequests = merged.PullRequests.Concat(closed.PullRequests).DistinctBy(p => p.Url).ToArray() };
                            }
                            catch (Exception) when (!token.IsCancellationRequested) { /* Unknown PR state never triggers PR settlement. */ }
                        }
                        PullRequestDescriptor? pr = null;
                        if (pullRequests is not null)
                        {
                            if (thread.PullRequest is { } link)
                                pr = pullRequests.PullRequests.FirstOrDefault(p => p.Provider == link.Provider && p.Number == link.Number &&
                                    string.Equals(p.Url.TrimEnd('/'), link.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
                            else if (thread.BranchName is { } branch && branch != pullRequests.Repository.DefaultBranch)
                            {
                                var matches = pullRequests.PullRequests.Where(p => p.SourceBranch == branch).ToArray();
                                if (matches.Length == 1) pr = matches[0]; // Ambiguous/reused branches do not guess.
                            }
                        }
                        if (!ThreadSettlementPolicy.ShouldSettle(thread, activity, settings, now, pr)) continue;
                        if (pr is not null && thread.PullRequest is { } linked &&
                            (linked.State != pr.State.ToString() || linked.UpdatedUtc != pr.UpdatedUtc))
                        {
                            var refreshed = new PullRequestLink(pr.Provider, pr.Repository, pr.Number, pr.Url, pr.State.ToString(), pr.Title, pr.UpdatedUtc);
                            var update = await _database.UpdateThreadInboxAsync(thread.ThreadId, thread.Revision,
                                pullRequest: refreshed, updatePullRequest: true, cancellationToken: token).ConfigureAwait(false);
                            if (!update.WasUpdated) continue;
                            // Do not adopt a later concurrent revision returned by a metadata reload.
                            thread = thread with { Revision = thread.Revision + 1, PullRequest = refreshed };
                        }
                        var changed = controller is not null
                            ? await controller.TryAutoSettleAsync(thread.Revision, token).ConfigureAwait(false)
                            : await _database.TryAutoSettleAsync(thread.ThreadId, thread.Revision, token).ConfigureAwait(false);
                        if (changed) count++;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception exception) { _diagnostics.Record($"Thread {record.ThreadId} settlement skipped: {exception.Message}"); }
                }
            }
            return count;
        }
        finally { _settlementGate.Release(); }
    }
}
