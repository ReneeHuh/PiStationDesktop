using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;

namespace PiStation.App.Composition;

/// <summary>Tracks live completion edges without replaying old completions on navigation/reconnect.</summary>
internal sealed class ProactivePanels
{
    private ThreadId? _thread;
    private ProjectionEpoch? _epoch;
    private long _completion;
    private TurnId? _pendingTurn;

    public int? Observe(ThreadProjection? projection, bool enabled)
    {
        if (projection is null) { Reset(); return null; }
        if (_thread != projection.ThreadId || _epoch != projection.ProjectionEpoch)
        {
            _thread = projection.ThreadId;
            _epoch = projection.ProjectionEpoch;
            _completion = projection.CompletionSequence;
            _pendingTurn = null;
            return null;
        }
        if (projection.CompletionSequence > _completion)
            _pendingTurn = enabled ? projection.Timeline.OfType<TurnBoundaryTimelineItem>()
                .LastOrDefault(item => item.Boundary == TurnBoundaryKind.Settled)?.BoundaryTurnId : null;
        _completion = projection.CompletionSequence;
        if (!enabled) { _pendingTurn = null; return null; }
        var checkpoint = projection.Checkpoints.LastOrDefault(item => item.TurnId == _pendingTurn);
        if (checkpoint is null) return null;
        _pendingTurn = null;
        return checkpoint.Status == ThreadCheckpointStatus.Ready && checkpoint.Files.Count > 0
            ? checkpoint.TurnCount : null;
    }

    public void Reset() { _thread = null; _epoch = null; _pendingTurn = null; _completion = 0; }

    public static bool IsNewLink(ThreadDescriptor? previous, ThreadDescriptor? next) =>
        previous is not null && next?.ThreadId == previous.ThreadId && next.PullRequest is { } link &&
        (previous.PullRequest is not { } old || old.Provider != link.Provider ||
         old.Repository != link.Repository || old.Number != link.Number);
}
