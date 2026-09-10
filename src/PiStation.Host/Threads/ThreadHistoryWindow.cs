using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;

namespace PiStation.Host.Threads;

public static class ThreadHistoryWindow
{
    public const int MaximumPageItems = 400;

    public static ThreadProjection Recent(ThreadProjection projection)
    {
        var start = FindStart(projection.Timeline, projection.Timeline.Count, 10);
        // Never hide interactions or mutable output that subsequent events must
        // update. Completed history before this boundary is safe to page later.
        for (var index = 0; index < start; index++)
            if (ThreadHistoryRules.IsMutable(projection.Timeline[index])) { start = index; break; }
        return projection with { Timeline = projection.Timeline.Skip(start).ToArray(), EarlierHistory = Cursor(projection.Timeline, start) };
    }

    public static ThreadHistoryPage Read(ThreadProjection projection, ReadThreadHistoryRequest request)
    {
        if (request.ThreadId != projection.ThreadId || request.ProjectionEpoch != projection.ProjectionEpoch ||
            string.IsNullOrEmpty(request.BeforeItemId) || request.BeforeItemId.Length > 512)
            throw new InvalidOperationException("Conversation history changed. Refresh the thread before loading earlier messages.");
        var end = -1;
        for (var index = 0; index < projection.Timeline.Count; index++)
            if (projection.Timeline[index].ItemId == request.BeforeItemId) { end = index; break; }
        if (end < 0) throw new InvalidOperationException("The history cursor is no longer available. Refresh the thread.");
        var start = FindStart(projection.Timeline, end, 20);
        return new(projection.ThreadId, projection.ProjectionEpoch, projection.Sequence, request.BeforeItemId,
            projection.Timeline.Skip(start).Take(end - start).ToArray(), Cursor(projection.Timeline, start));
    }

    private static ThreadHistoryCursor? Cursor(IReadOnlyList<TimelineItem> items, int start) =>
        start > 0 ? new(items[start].ItemId, start) : null;

    private static int FindStart(IReadOnlyList<TimelineItem> items, int end, int turns)
    {
        var minimum = Math.Max(0, end - MaximumPageItems);
        for (var index = end - 1; index >= minimum; index--)
            if (items[index] is TurnBoundaryTimelineItem { Boundary: TurnBoundaryKind.Started } && --turns == 0) return index;
        return minimum;
    }
}
