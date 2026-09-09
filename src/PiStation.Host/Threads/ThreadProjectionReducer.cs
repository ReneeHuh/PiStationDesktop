using System.Globalization;
using System.Text;
using System.Text.Json;
using PiStation.PiRpc.Decoding;
using PiStation.PiRpc.Wire.Events;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Streaming;
using PiStation.PiRpc.Transport;

namespace PiStation.Host.Threads;

public static class ThreadProjectionReducer
{
    public static ThreadProjection Create(
        EnvironmentId environmentId,
        ThreadId threadId,
        string piSessionId,
        string? piSessionFile = null) => new(
        environmentId,
        threadId,
        ProjectionEpoch.New(),
        Sequence.Initial,
        ThreadRuntimeState.Stopped,
        null,
        [],
        [],
        piSessionId,
        piSessionFile,
        null,
        null,
        EmptyQueue(),
        []);

    public static ThreadProjection Hydrate(
        ThreadProjection current,
        IReadOnlyList<JsonElement> entries,
        string? leafId,
        string? piSessionFile,
        int? contextWindow = null,
        IReadOnlyList<ThreadCheckpoint>? checkpoints = null,
        string? piSessionId = null,
        IReadOnlyDictionary<string, SentMessageContent>? sentMessages = null)
    {
        var timeline = new List<TimelineItem>();
        var hydratedTurnIds = new List<TurnId>();
        TurnId? hydratedTurnId = null;
        DateTimeOffset? hydratedTurnStartedUtc = null;
        DateTimeOffset? hydratedTurnLastUtc = null;
        TokenUsage? hydratedTurnUsage = null;
        long? hydratedContextTokens = null;
        var messageCount = 0;
        foreach (var entry in entries)
        {
            if (!entry.TryGetProperty("type", out var entryType) || entryType.GetString() != "message" ||
                !entry.TryGetProperty("message", out var message))
            {
                continue;
            }

            var id = entry.TryGetProperty("id", out var idProperty)
                ? idProperty.GetString() ?? $"entry-{messageCount + 1}"
                : $"entry-{messageCount + 1}";
            var entryTimestamp = ReadEntryTimestamp(entry);
            messageCount++;
            var hydratedMessage = ReadMessage(id, message, isComplete: true, sentMessages);
            if (hydratedMessage.Role == MessageRole.User)
            {
                if (hydratedTurnId is not null)
                {
                    timeline = AddOrReplace(
                        timeline,
                        CreateTurnBoundary(
                            hydratedTurnId.Value,
                            TurnBoundaryKind.Settled,
                            CreateHydratedTurnMetrics(
                                hydratedTurnStartedUtc,
                                hydratedTurnLastUtc,
                                hydratedTurnUsage,
                                hydratedContextTokens,
                                contextWindow)));
                }

                hydratedTurnId = TurnId.Parse($"hydrated-{id}");
                hydratedTurnIds.Add(hydratedTurnId.Value);
                hydratedTurnStartedUtc = entryTimestamp;
                hydratedTurnLastUtc = entryTimestamp;
                hydratedTurnUsage = null;
                hydratedContextTokens = null;
                timeline = AddOrReplace(
                    timeline,
                    CreateTurnBoundary(hydratedTurnId.Value, TurnBoundaryKind.Started));
            }

            if (hydratedTurnId is not null)
            {
                hydratedTurnLastUtc = entryTimestamp ?? hydratedTurnLastUtc;
                if (hydratedMessage.Role == MessageRole.Assistant && PiUsageReader.Read(message) is { } usage)
                {
                    hydratedTurnUsage = AddUsage(hydratedTurnUsage, ToProtocolUsage(usage));
                    if (PiUsageReader.HasUsableContext(message, usage))
                    {
                        hydratedContextTokens = usage.ContextTokens;
                    }
                }
            }

            timeline = AddOrReplaceMessage(timeline, hydratedMessage, hydratedTurnId);
        }

        if (hydratedTurnId is not null)
        {
            timeline = AddOrReplace(
                timeline,
                CreateTurnBoundary(
                    hydratedTurnId.Value,
                    TurnBoundaryKind.Settled,
                    CreateHydratedTurnMetrics(
                        hydratedTurnStartedUtc,
                        hydratedTurnLastUtc,
                        hydratedTurnUsage,
                        hydratedContextTokens,
                        contextWindow)));
        }

        var hydratedCheckpoints = (checkpoints ?? current.Checkpoints)
            .OrderBy(static checkpoint => checkpoint.TurnCount)
            .Select(checkpoint => checkpoint.TurnCount > 0 && checkpoint.TurnCount <= hydratedTurnIds.Count
                ? checkpoint with { TurnId = hydratedTurnIds[checkpoint.TurnCount - 1] }
                : checkpoint)
            .ToArray();

        return current with
        {
            ProjectionEpoch = ProjectionEpoch.New(),
            Sequence = Sequence.Initial,
            RuntimeState = ThreadRuntimeState.Ready,
            CurrentTurnId = null,
            Timeline = timeline,
            Checkpoints = hydratedCheckpoints,
            PiSessionId = piSessionId ?? current.PiSessionId,
            PiSessionFile = piSessionFile,
            LastEntryId = leafId,
            LastError = null,
        };
    }

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

    public static MessageProjection ReadMessage(string messageId, JsonElement message, bool isComplete,
        IReadOnlyDictionary<string, SentMessageContent>? sentMessages = null)
    {
        var role = message.TryGetProperty("role", out var roleProperty)
            ? ParseRole(roleProperty.GetString())
            : MessageRole.Assistant;
        var text = new StringBuilder();
        var thinking = new StringBuilder();
        if (roleProperty.ValueKind == JsonValueKind.String && roleProperty.GetString() == "bashExecution")
        {
            return ReadShellMessage(messageId, message.GetProperty("command").GetString() ?? "",
                message.GetProperty("output").GetString() ?? "",
                message.TryGetProperty("excludeFromContext", out var excluded) && excluded.ValueKind == JsonValueKind.True,
                message.TryGetProperty("cancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True,
                message.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True,
                message.TryGetProperty("exitCode", out var exitCode) && exitCode.ValueKind == JsonValueKind.Number ? exitCode.GetInt32() : null,
                message.TryGetProperty("fullOutputPath", out var fullPath) && fullPath.ValueKind == JsonValueKind.String ? fullPath.GetString() : null);
        }
        if (message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                text.Append(content.GetString());
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    var type = block.TryGetProperty("type", out var typeProperty) ? typeProperty.GetString() : null;
                    if (type == "text" && block.TryGetProperty("text", out var textProperty))
                    {
                        text.Append(textProperty.GetString());
                    }
                    else if (type == "thinking" && block.TryGetProperty("thinking", out var thinkingProperty))
                    {
                        thinking.Append(thinkingProperty.GetString());
                    }
                }
            }
        }

        var messageText = text.ToString();
        SentMessageContent? sentContent = null;
        if (role == MessageRole.User)
        {
            if (SentMessageReference.Read(messageText) is { } reference && sentMessages is not null)
                sentMessages.TryGetValue(reference, out sentContent);
            messageText = SentMessageReference.Remove(PiPromptFormatter.NormalizePersistedMessage(messageText));
        }

        return new MessageProjection(messageId, role, sentContent?.Text ?? messageText, thinking.ToString(), isComplete, sentContent);
    }

    internal static MessageProjection ReadShellMessage(string id, string command, string output, bool excluded,
        bool cancelled, bool truncated, int? exitCode, string? fullOutputPath)
    {
        var text = new StringBuilder("Pi shell: ").AppendLine(command)
            .AppendLine(excluded ? "Excluded from model context" : "Included in model context");
        if (cancelled) text.AppendLine("Cancelled");
        if (exitCode is { } code) text.Append("Exit ").AppendLine(code.ToString(CultureInfo.InvariantCulture));
        if (output.Length > PiShellExecution.MaximumOutputLength)
        {
            var start = output.Length - PiShellExecution.MaximumOutputLength;
            if (char.IsLowSurrogate(output[start])) start++;
            output = output[start..];
            truncated = true;
        }
        if (truncated) text.AppendLine("Output truncated");
        if (fullOutputPath is not null) text.Append("Full output on the host: ").AppendLine(fullOutputPath);
        text.Append(output);
        return new MessageProjection(id, MessageRole.System, text.ToString(), "", true);
    }

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

    private static ThreadQueueProjection EmptyQueue() => new(
        [],
        QueueDeliveryMode.OneAtATime,
        QueueDeliveryMode.OneAtATime,
        QueueDeliveryState.Empty,
        0,
        DateTimeOffset.UtcNow);

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

    private static TurnMetrics? CreateHydratedTurnMetrics(
        DateTimeOffset? startedUtc,
        DateTimeOffset? completedUtc,
        TokenUsage? usage,
        long? contextTokens,
        int? contextWindow)
    {
        long? elapsedMilliseconds = startedUtc is not null &&
            completedUtc is not null &&
            completedUtc >= startedUtc
                ? Math.Max(0, (long)Math.Round((completedUtc.Value - startedUtc.Value).TotalMilliseconds))
                : null;
        return elapsedMilliseconds is null && usage is null && contextTokens is null
            ? null
            : new TurnMetrics(elapsedMilliseconds, usage, contextTokens, contextWindow);
    }

    private static DateTimeOffset? ReadEntryTimestamp(JsonElement entry)
    {
        if (!entry.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            timestamp.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
                ? parsed
                : null;
    }

    private static TokenUsage ToProtocolUsage(PiTokenUsage usage) => new(
        usage.InputTokens,
        usage.OutputTokens,
        usage.CacheReadTokens,
        usage.CacheWriteTokens,
        usage.ReasoningTokens,
        usage.TotalTokens);

    private static TokenUsage AddUsage(TokenUsage? current, TokenUsage next) => new(
        AddSaturated(current?.InputTokens ?? 0, next.InputTokens),
        AddSaturated(current?.OutputTokens ?? 0, next.OutputTokens),
        AddSaturated(current?.CacheReadTokens ?? 0, next.CacheReadTokens),
        AddSaturated(current?.CacheWriteTokens ?? 0, next.CacheWriteTokens),
        current?.ReasoningTokens is null && next.ReasoningTokens is null
            ? null
            : AddSaturated(current?.ReasoningTokens ?? 0, next.ReasoningTokens ?? 0),
        AddSaturated(current?.TotalTokens ?? 0, next.TotalTokens));

    private static long AddSaturated(long left, long right) => left > long.MaxValue - right
        ? long.MaxValue
        : left + right;

    private static MessageRole ParseRole(string? role) => role switch
    {
        "user" => MessageRole.User,
        "system" => MessageRole.System,
        "tool" => MessageRole.Tool,
        _ => MessageRole.Assistant,
    };
}
