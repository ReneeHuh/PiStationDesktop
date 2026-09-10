using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Projections;

namespace PiStation.Protocol.Models;

public static class ThreadHistoryRules
{
    public static bool IsMutable(TimelineItem item) => item is ApprovalTimelineItem { State: InteractionState.Pending } or
        QuestionTimelineItem { State: InteractionState.Pending } or ToolTimelineItem { State: ToolExecutionState.Running } or
        MessageTimelineItem { IsComplete: false } or ThinkingTimelineItem { IsComplete: false } or
        StatusTimelineItem { State: TimelineActivityState.Running };
}

public sealed record ThreadHistoryCursor(string BeforeItemId, int RemainingItems);
public sealed record ReadThreadHistoryRequest(ThreadId ThreadId, ProjectionEpoch ProjectionEpoch, string BeforeItemId);
public sealed record ThreadHistoryPage(ThreadId ThreadId, ProjectionEpoch ProjectionEpoch, Sequence Sequence,
    string BeforeItemId, IReadOnlyList<TimelineItem> Items, ThreadHistoryCursor? Earlier);
