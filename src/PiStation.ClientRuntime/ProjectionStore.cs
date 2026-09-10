using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Streaming;

namespace PiStation.ClientRuntime;

public enum ProjectionApplyResult
{
    Applied,
    Ignored,
    ResyncRequired,
}

public sealed class ProjectionChangedEventArgs(ThreadProjection? projection) : EventArgs
{
    public ThreadProjection? Projection { get; } = projection;
}

public sealed class ProjectionStore(ThreadId threadId)
{
    private readonly object _gate = new();
    private ThreadProjection? _current;
    private bool _isSynchronized;

    public bool IsSynchronized { get { lock (_gate) return _isSynchronized; } }

    internal void SetSynchronized(bool value)
    {
        lock (_gate)
        {
            if (_isSynchronized == value) return;
            _isSynchronized = value;
        }
        SynchronizationChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? SynchronizationChanged;

    public event EventHandler<ProjectionChangedEventArgs>? Changed;

    public ThreadId ThreadId { get; } = threadId;

    public ThreadProjection? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public ThreadCursor? Cursor
    {
        get
        {
            lock (_gate)
            {
                return _current is null
                    ? null
                    : new ThreadCursor(_current.ProjectionEpoch, _current.Sequence);
            }
        }
    }

    public ProjectionApplyResult Apply(ThreadEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.ThreadId != ThreadId)
        {
            throw new ArgumentException("The envelope belongs to another thread.", nameof(envelope));
        }

        ThreadProjection? changedProjection = null;
        ProjectionApplyResult result;
        lock (_gate)
        {
            result = envelope switch
            {
                ThreadResyncRequiredEnvelope => ProjectionApplyResult.ResyncRequired,
                ThreadSnapshotEnvelope snapshot => ApplySnapshot(snapshot, out changedProjection),
                ThreadEventEnvelope @event => ApplyEvent(@event, out changedProjection),
                _ => ProjectionApplyResult.Ignored,
            };
        }

        if (changedProjection is not null)
        {
            Changed?.Invoke(this, new ProjectionChangedEventArgs(changedProjection));
        }

        return result;
    }

    public void Reset()
    {
        var changed = false;
        lock (_gate)
        {
            changed = _current is not null;
            _current = null;
        }

        if (changed)
        {
            Changed?.Invoke(this, new ProjectionChangedEventArgs(null));
        }
    }

    public bool TryMergeHistory(ThreadHistoryPage page)
    {
        ThreadProjection updated;
        lock (_gate)
        {
            if (!_isSynchronized || _current is null || page.ThreadId != ThreadId ||
                page.ProjectionEpoch != _current.ProjectionEpoch || page.BeforeItemId != _current.EarlierHistory?.BeforeItemId ||
                page.Sequence > _current.Sequence) return false;
            var existing = _current.Timeline.Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);
            updated = _current with { Timeline = page.Items.Where(item => existing.Add(item.ItemId)).Concat(_current.Timeline).ToArray(),
                EarlierHistory = page.Earlier };
            _current = updated;
        }
        Changed?.Invoke(this, new(updated));
        return true;
    }

    private ProjectionApplyResult ApplySnapshot(
        ThreadSnapshotEnvelope snapshot,
        out ThreadProjection? changedProjection)
    {
        changedProjection = null;
        if (_current is not null &&
            _current.ProjectionEpoch == snapshot.ProjectionEpoch &&
            _current.Sequence >= snapshot.Sequence)
        {
            return ProjectionApplyResult.Ignored;
        }

        var replacement = snapshot.Projection;
        if (_current?.ProjectionEpoch == replacement.ProjectionEpoch && replacement.EarlierHistory is { } earlier)
        {
            // A journal overrun can send a fresh recent window in the same
            // epoch. Keep already loaded immutable history when the windows
            // overlap, while replacing the live portion from the host.
            var overlap = -1;
            for (var index = 0; index < _current.Timeline.Count; index++)
                if (_current.Timeline[index].ItemId == earlier.BeforeItemId) { overlap = index; break; }
            if (overlap > 0 && _current.Timeline.Take(overlap).All(item => !ThreadHistoryRules.IsMutable(item))) replacement = replacement with {
                Timeline = _current.Timeline.Take(overlap).Concat(replacement.Timeline).ToArray(), EarlierHistory = _current.EarlierHistory };
        }
        _current = replacement;
        changedProjection = _current;
        return ProjectionApplyResult.Applied;
    }

    private ProjectionApplyResult ApplyEvent(
        ThreadEventEnvelope envelope,
        out ThreadProjection? changedProjection)
    {
        changedProjection = null;
        if (_current is null || _current.ProjectionEpoch != envelope.ProjectionEpoch)
        {
            return ProjectionApplyResult.ResyncRequired;
        }

        if (envelope.Sequence <= _current.Sequence)
        {
            return ProjectionApplyResult.Ignored;
        }

        if (envelope.Sequence != _current.Sequence.Next())
        {
            return ProjectionApplyResult.ResyncRequired;
        }

        _current = ClientProjectionReducer.Apply(_current, envelope.Event) with
        {
            Sequence = envelope.Sequence,
        };
        changedProjection = _current;
        return ProjectionApplyResult.Applied;
    }
}

internal static class ClientProjectionReducer
{
    public static ThreadProjection Apply(ThreadProjection projection, ThreadEvent @event) => @event switch
    {
        PiShellChangedEvent changed => projection with { ShellExecution = changed.Execution, LastEntryId = changed.LastEntryId ?? projection.LastEntryId },
        PiPlanChangedEvent changed when changed.Plan.SessionId == projection.PiSessionId && changed.Plan.Revision >= (projection.Plan?.Revision ?? -1) => projection with { Plan = changed.Plan },
        PiAgentSetupChangedEvent changed when changed.Setup.SessionId == projection.PiSessionId => projection with { AgentSetup = changed.Setup },
        PiExtensionUiChangedEvent changed => projection with { ExtensionUi = (projection.ExtensionUi ?? PiExtensionUiState.Empty).Apply(changed.Update) },
        RuntimeStateChangedEvent changed => ApplyRuntimeStateChanged(projection, changed),
        TurnStartedEvent started => ApplyTurnStarted(projection, started),
        MessageStartedEvent started => projection with
        {
            Timeline = AddOrReplaceMessage(projection.Timeline, started.Message, projection.CurrentTurnId),
        },
        ContentDeltaEvent delta => projection with
        {
            Timeline = ApplyDelta(projection.Timeline, delta, projection.CurrentTurnId),
        },
        MessageCompletedEvent completed => projection with
        {
            Timeline = AddOrReplaceMessage(projection.Timeline, completed.Message, projection.CurrentTurnId),
        },
        ToolStartedEvent started => projection with
        {
            Timeline = AddOrReplace(
                projection.Timeline,
                new ToolTimelineItem(
                    $"tool-{started.Tool.ToolCallId}",
                    projection.CurrentTurnId,
                    started.Tool.ToolCallId,
                    started.Tool.ToolName,
                    started.Tool.ArgumentsPreview,
                    started.Tool.OutputPreview,
                    started.Tool.State)),
        },
        ToolOutputReplacedEvent replaced => projection with
        {
            Timeline = projection.Timeline.Select(item => item is ToolTimelineItem tool &&
                    tool.ToolCallId == replaced.ToolCallId
                ? tool with { OutputPreview = replaced.OutputPreview }
                : item).ToArray(),
        },
        ToolCompletedEvent completed => projection with
        {
            Timeline = projection.Timeline.Select(item => item is ToolTimelineItem tool &&
                    tool.ToolCallId == completed.ToolCallId
                ? tool with { OutputPreview = completed.OutputPreview, State = completed.State }
                : item).ToArray(),
        },
        ApprovalRequestedEvent requested => projection with
        {
            Timeline = AddOrReplace(
                projection.Timeline,
                new ApprovalTimelineItem(
                    $"interaction-{requested.InteractionId.Value}",
                    projection.CurrentTurnId,
                    requested.InteractionId,
                    requested.Title,
                    requested.Message,
                    requested.TimeoutMilliseconds,
                    InteractionState.Pending,
                    null)),
        },
        QuestionRequestedEvent requested => projection with
        {
            Timeline = AddOrReplace(
                projection.Timeline,
                new QuestionTimelineItem(
                    $"interaction-{requested.InteractionId.Value}",
                    projection.CurrentTurnId,
                    requested.InteractionId,
                    requested.InputKind,
                    requested.Title,
                    requested.Prompt,
                    requested.Options,
                    requested.InitialValue,
                    requested.TimeoutMilliseconds,
                    InteractionState.Pending,
                    null)),
        },
        InteractionResolvedEvent resolved => ApplyInteractionResolved(projection, resolved),
        CheckpointCapturedEvent captured => ApplyCheckpointCaptured(projection, captured),
        QueueStateChangedEvent changed => projection with { Queue = changed.Queue },
        AgentActivityChangedEvent changed => projection with
        {
            AgentActivities = AddOrReplaceAgentActivity(projection.AgentActivities ?? [], changed.Activity),
        },
        ContextCompactionChangedEvent changed => projection with { Compaction = changed.Compaction },
        TurnSettledEvent settled => ApplyTurnSettled(projection, settled),
        RuntimeFailedEvent failed => ApplyRuntimeFailed(projection, failed),
        UnknownRuntimeEvent unknown => projection with
        {
            Timeline = AddOrReplace(
                projection.Timeline,
                new StatusTimelineItem(
                    $"status-{projection.Sequence.Next().Value}",
                    projection.CurrentTurnId,
                    $"Pi event: {unknown.RuntimeType} — {unknown.Preview}",
                    TimelineActivityState.Informational)),
        },
        _ => projection,
    };

    private static ThreadProjection ApplyRuntimeStateChanged(
        ThreadProjection projection,
        RuntimeStateChangedEvent changed)
    {
        var timeline = projection.Timeline;
        if (projection.CurrentTurnId is not null &&
            changed.State is ThreadRuntimeState.Running or ThreadRuntimeState.Stopping)
        {
            timeline = AddOrReplace(
                timeline,
                new StatusTimelineItem(
                    $"status-{projection.Sequence.Next().Value}",
                    projection.CurrentTurnId,
                    changed.State == ThreadRuntimeState.Stopping ? "Stopping turn" : "Pi is working",
                    TimelineActivityState.Running));
        }

        return projection with
        {
            RuntimeState = changed.State,
            ExtensionUi = changed.State is ThreadRuntimeState.Starting or ThreadRuntimeState.Stopped ? PiExtensionUiState.Empty : projection.ExtensionUi,
            Timeline = timeline,
            LastError = changed.State == ThreadRuntimeState.Crashed ? projection.LastError : null,
        };
    }

    private static ThreadProjection ApplyTurnStarted(ThreadProjection projection, TurnStartedEvent started)
    {
        var timeline = AddOrReplace(
            projection.Timeline,
            CreateTurnBoundary(started.TurnId, TurnBoundaryKind.Started));
        timeline = AddOrReplaceMessage(
            timeline,
            new MessageProjection(
                $"user-{started.TurnId.Value}",
                MessageRole.User,
                started.Prompt,
                string.Empty,
                true, started.Content),
            started.TurnId);

        return projection with
        {
            RuntimeState = ThreadRuntimeState.Running,
            CurrentTurnId = started.TurnId,
            Timeline = timeline,
            LastError = null,
        };
    }

    private static ThreadProjection ApplyTurnSettled(ThreadProjection projection, TurnSettledEvent settled)
    {
        IReadOnlyList<TimelineItem> timeline = FinalizeTurnItems(
            projection.Timeline,
            settled.TurnId,
            TimelineActivityState.Completed);
        timeline = AddOrReplace(
            timeline,
            CreateTurnBoundary(settled.TurnId, TurnBoundaryKind.Settled, settled.Metrics));
        return projection with
        {
            CompletionSequence = Math.Max(projection.CompletionSequence, settled.CompletionSequence),
            RuntimeState = ThreadRuntimeState.Ready,
            CurrentTurnId = null,
            Timeline = timeline,
        };
    }

    private static ThreadProjection ApplyCheckpointCaptured(
        ThreadProjection projection,
        CheckpointCapturedEvent captured) => projection with
    {
        Checkpoints = projection.Checkpoints
            .Where(checkpoint => checkpoint.TurnCount != captured.Checkpoint.TurnCount)
            .Append(captured.Checkpoint)
            .OrderBy(static checkpoint => checkpoint.TurnCount)
            .ToArray(),
        LastEntryId = captured.Checkpoint.PiEntryIdAfterTurn ?? projection.LastEntryId,
    };

    private static ThreadProjection ApplyRuntimeFailed(ThreadProjection projection, RuntimeFailedEvent failed)
    {
        var turnId = projection.CurrentTurnId;
        var timeline = turnId is null
            ? projection.Timeline
            : FinalizeTurnItems(projection.Timeline, turnId.Value, TimelineActivityState.Failed);
        return projection with
        {
            RuntimeState = ThreadRuntimeState.Crashed,
            CurrentTurnId = null,
            Timeline = AddOrReplace(
                timeline,
                new ErrorTimelineItem(
                    $"error-{projection.Sequence.Next().Value}",
                    turnId,
                    failed.Error)),
            LastError = failed.Error,
        };
    }

    private static IReadOnlyList<TimelineItem> ApplyDelta(
        IReadOnlyList<TimelineItem> timeline,
        ContentDeltaEvent delta,
        TurnId? turnId)
    {
        if (delta.ContentKind == ContentKind.Text)
        {
            return timeline.Select(item => item is MessageTimelineItem message &&
                    message.MessageId == delta.MessageId
                ? message with { Text = message.Text + delta.Delta }
                : item).ToArray();
        }

        var result = timeline.ToList();
        var thinkingIndex = result.FindIndex(item => item is ThinkingTimelineItem thinking &&
            thinking.MessageId == delta.MessageId);
        if (thinkingIndex >= 0)
        {
            var thinking = (ThinkingTimelineItem)result[thinkingIndex];
            result[thinkingIndex] = thinking with { Text = thinking.Text + delta.Delta };
            return result;
        }

        var messageIndex = result.FindIndex(item => item is MessageTimelineItem message &&
            message.MessageId == delta.MessageId);
        var messageTurnId = messageIndex >= 0 ? result[messageIndex].TurnId : turnId;
        var thinkingItem = new ThinkingTimelineItem(
            $"thinking-{delta.MessageId}",
            messageTurnId,
            delta.MessageId,
            delta.Delta,
            false);
        if (messageIndex >= 0)
        {
            result.Insert(messageIndex, thinkingItem);
        }
        else
        {
            result.Add(thinkingItem);
        }

        return result;
    }

    private static List<TimelineItem> AddOrReplaceMessage(
        IReadOnlyList<TimelineItem> timeline,
        MessageProjection message,
        TurnId? turnId)
    {
        var result = timeline.ToList();
        var messageItem = new MessageTimelineItem(
            $"message-{message.MessageId}",
            turnId,
            message.MessageId,
            message.Role,
            message.Text,
            message.IsComplete, message.Content);
        result = AddOrReplace(result, messageItem);

        var thinkingIndex = result.FindIndex(item => item is ThinkingTimelineItem thinking &&
            thinking.MessageId == message.MessageId);
        if (!string.IsNullOrEmpty(message.Thinking))
        {
            var thinkingItem = new ThinkingTimelineItem(
                $"thinking-{message.MessageId}",
                turnId,
                message.MessageId,
                message.Thinking,
                message.IsComplete);
            if (thinkingIndex >= 0)
            {
                result[thinkingIndex] = thinkingItem;
            }
            else
            {
                var messageIndex = result.FindIndex(item => item.ItemId == messageItem.ItemId);
                result.Insert(Math.Max(0, messageIndex), thinkingItem);
            }
        }
        else if (thinkingIndex >= 0 && message.IsComplete)
        {
            var thinking = (ThinkingTimelineItem)result[thinkingIndex];
            result[thinkingIndex] = thinking with { IsComplete = true };
        }

        return result;
    }

    private static List<TimelineItem> AddOrReplace(
        IReadOnlyList<TimelineItem> timeline,
        TimelineItem item)
    {
        var result = timeline.ToList();
        var index = result.FindIndex(existing => existing.ItemId == item.ItemId);
        if (index < 0)
        {
            result.Add(item);
        }
        else
        {
            result[index] = item;
        }

        return result;
    }

    private static AgentActivityProjection[] AddOrReplaceAgentActivity(
        IReadOnlyList<AgentActivityProjection> activities,
        AgentActivityProjection activity)
    {
        var result = activities.ToList();
        var index = result.FindIndex(existing => existing.ActivityId == activity.ActivityId);
        if (index < 0)
        {
            result.Add(activity);
        }
        else
        {
            result[index] = activity;
        }

        return result
            .OrderBy(static item => item.StartedUtc)
            .ThenBy(static item => item.AgentIndex ?? int.MaxValue)
            .ThenBy(static item => item.ActivityId, StringComparer.Ordinal)
            .ToArray();
    }

    private static ThreadProjection ApplyInteractionResolved(
        ThreadProjection projection,
        InteractionResolvedEvent resolved) => projection with
    {
        Timeline = projection.Timeline.Select(item => item switch
        {
            ApprovalTimelineItem approval when approval.InteractionId == resolved.InteractionId => approval with
            {
                State = resolved.State,
                Decision = resolved.ApprovalDecision,
            },
            QuestionTimelineItem question when question.InteractionId == resolved.InteractionId => question with
            {
                State = resolved.State,
                Answer = resolved.Answer,
            },
            _ => item,
        }).ToArray(),
    };

    private static TimelineItem[] FinalizeTurnItems(
        IReadOnlyList<TimelineItem> timeline,
        TurnId turnId,
        TimelineActivityState finalState) => timeline
        .Select(item => item switch
        {
            StatusTimelineItem status when status.TurnId == turnId &&
                status.State == TimelineActivityState.Running => status with { State = finalState },
            ApprovalTimelineItem approval when approval.TurnId == turnId &&
                approval.State == InteractionState.Pending => approval with { State = InteractionState.Canceled },
            QuestionTimelineItem question when question.TurnId == turnId &&
                question.State == InteractionState.Pending => question with { State = InteractionState.Canceled },
            _ => item,
        })
        .ToArray();

    private static TurnBoundaryTimelineItem CreateTurnBoundary(
        TurnId turnId,
        TurnBoundaryKind boundary,
        TurnMetrics? metrics = null) => new(
        $"turn-{turnId.Value}-{boundary.ToString().ToLowerInvariant()}",
        turnId,
        boundary,
        metrics);
}
