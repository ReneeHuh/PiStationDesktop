using PiStation.Host.Persistence;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;

namespace PiStation.Host.Threads;

public static class ThreadSettlementPolicy
{
    public static bool HasLiveWork(ThreadProjection? projection) => projection is not null &&
        (projection.RuntimeState is ThreadRuntimeState.Starting or ThreadRuntimeState.Hydrating or ThreadRuntimeState.Running or ThreadRuntimeState.Stopping ||
         projection.Queue?.PendingMessageCount > 0 ||
         projection.Plan?.Mode is "ready" or "executing" or "paused" ||
         projection.Timeline.Any(item => item is ApprovalTimelineItem { State: InteractionState.Pending } or QuestionTimelineItem { State: InteractionState.Pending }) ||
         projection.AgentActivities?.Any(agent => agent.State is AgentActivityState.Pending or AgentActivityState.Running or AgentActivityState.Waiting) == true);

    public static bool ShouldSettle(ThreadDescriptor thread, SettlementActivity activity, SettlementSettings settings,
        DateTimeOffset now, PullRequestDescriptor? pullRequest = null)
    {
        if (thread.IsArchived || thread.IsSettled || activity.Protected || thread.NeedsAttention ||
            thread.SnoozedUntilUtc > now || thread.SetupScriptState is SetupScriptState.Pending or SetupScriptState.Running ||
            thread.RuntimeState is ThreadRuntimeState.Starting or ThreadRuntimeState.Hydrating or ThreadRuntimeState.Running or ThreadRuntimeState.Stopping)
            return false;
        // Protect accepted prompts while their runtime catches up, including clock skew.
        if (activity.LastUserActivity is { } user && Math.Abs((now - user).TotalMinutes) <= 2) return false;
        if (pullRequest?.ClosedOrMergedUtc is { } closedAt && activity.LastUserActivity is { } anchor && closedAt >= anchor &&
            ((settings.OnMerge && pullRequest.State == PullRequestState.Merged) || (settings.OnClose && pullRequest.State == PullRequestState.Closed)))
            return true;
        // No guessed migration timestamp: untouched/legacy threads stay active until real activity is recorded.
        return settings.InactiveDays is { } days && activity.LastActivity is { } last && last < now.AddDays(-days);
    }
}
