using System.Text.Json;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Projections;

namespace PiStation.Host.Threads;

internal static class PiAgentActivityProjector
{
    private const int SummaryLimit = 4_000;
    private const int ActivityLimit = 320;

    public static IReadOnlyList<AgentActivityProjection> Start(
        string toolCallId,
        string toolName,
        JsonElement arguments,
        TurnId? turnId,
        DateTimeOffset now)
    {
        if (!IsAgentTool(toolName))
        {
            return [];
        }

        var mode = ReadString(arguments, "mode") ?? InferMode(arguments);
        if (mode is "parallel" or "chain")
        {
            var workflow = NewActivity(
                toolCallId,
                turnId,
                null,
                AgentActivityKind.Workflow,
                AgentActivityState.Running,
                mode == "parallel" ? "Parallel workflow" : "Chained workflow",
                ReadString(arguments, "task") ?? string.Empty,
                "Preparing agents",
                now,
                canInterrupt: true);
            return [workflow, .. ReadTasks(arguments, mode).Select((task, index) => NewActivity(
                ChildId(toolCallId, index),
                turnId,
                toolCallId,
                AgentActivityKind.Agent,
                AgentActivityState.Pending,
                task.Agent,
                task.Task,
                mode == "chain" && index > 0 ? "Waiting for previous step" : "Waiting to start",
                now,
                step: mode == "chain" ? index + 1 : null,
                agentIndex: index,
                canInterrupt: false))];
        }

        var title = ReadString(arguments, "agent") ?? toolName;
        return
        [
            NewActivity(
                toolCallId,
                turnId,
                null,
                AgentActivityKind.Agent,
                AgentActivityState.Running,
                title,
                ReadString(arguments, "task") ?? string.Empty,
                "Starting",
                now,
                canInterrupt: true),
        ];
    }

    public static IReadOnlyList<AgentActivityProjection> Update(
        string toolCallId,
        JsonElement result,
        IReadOnlyList<AgentActivityProjection> current,
        bool isFinal,
        bool isError,
        TurnId? turnId,
        DateTimeOffset now)
    {
        if (!TryGetDetails(result, out var details) ||
            !details.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            var existing = current.FirstOrDefault(activity => activity.ActivityId == toolCallId);
            if (existing is null)
            {
                return [];
            }

            var summary = ExtractContentText(result);
            var state = isFinal
                ? isError ? AgentActivityState.Failed : AgentActivityState.Completed
                : AgentActivityState.Running;
            return
            [
                existing with
                {
                    State = state,
                    CurrentActivity = isFinal ? StateLabel(state) : Bounded(summary, ActivityLimit, "Working"),
                    UpdatedUtc = now,
                    CompletedUtc = isFinal ? now : null,
                    ResultSummary = isFinal && !isError ? Bounded(summary, SummaryLimit, null) : existing.ResultSummary,
                    FailureSummary = isFinal && isError ? Bounded(summary, SummaryLimit, "Agent failed") : existing.FailureSummary,
                    CanInterrupt = !isFinal,
                },
            ];
        }

        var mode = ReadString(details, "mode") ?? (current.Any(activity => activity.Kind == AgentActivityKind.Workflow)
            ? "parallel"
            : "single");
        var projections = new List<AgentActivityProjection>();
        var index = 0;
        foreach (var item in results.EnumerateArray())
        {
            var activityId = mode is "parallel" or "chain" ? ChildId(toolCallId, index) : toolCallId;
            var previous = current.FirstOrDefault(activity => activity.ActivityId == activityId);
            var state = ReadAgentState(item, isFinal, isError);
            var task = Bounded(ReadString(item, "task"), SummaryLimit, string.Empty);
            var title = Bounded(ReadString(item, "agent"), 120, previous?.Title ?? $"Agent {index + 1}");
            var output = ReadFinalOutput(item);
            var failure = ReadFailure(item);
            var currentActivity = state switch
            {
                AgentActivityState.Running => ReadCurrentActivity(item),
                AgentActivityState.Pending => "Waiting to start",
                AgentActivityState.Waiting => "Waiting",
                _ => StateLabel(state),
            };
            projections.Add(new AgentActivityProjection(
                activityId,
                previous?.TurnId ?? turnId,
                mode is "parallel" or "chain" ? toolCallId : null,
                AgentActivityKind.Agent,
                state,
                title,
                task,
                Bounded(currentActivity, ActivityLimit, StateLabel(state)),
                previous?.StartedUtc ?? now,
                now,
                IsTerminal(state) ? previous?.CompletedUtc ?? now : null,
                CountTools(item),
                ReadUsage(item),
                Bounded(ReadString(item, "model"), 160, previous?.Model),
                Bounded(ReadString(item, "thinkingLevel"), 80, previous?.ReasoningLevel),
                state == AgentActivityState.Completed ? Bounded(output, SummaryLimit, previous?.ResultSummary) : previous?.ResultSummary,
                state is AgentActivityState.Failed or AgentActivityState.Interrupted
                    ? Bounded(failure ?? output, SummaryLimit, previous?.FailureSummary)
                    : previous?.FailureSummary,
                ReadInt32(item, "step") ?? (mode == "chain" ? index + 1 : null),
                index,
                mode == "single" && !IsTerminal(state)));
            index++;
        }

        if (mode is "parallel" or "chain")
        {
            var previous = current.FirstOrDefault(activity => activity.ActivityId == toolCallId);
            var updatedIds = projections.Select(static activity => activity.ActivityId)
                .ToHashSet(StringComparer.Ordinal);
            var allChildren = projections.Concat(current.Where(activity =>
                    activity.ParentActivityId == toolCallId && !updatedIds.Contains(activity.ActivityId)))
                .ToArray();
            var workflowState = AggregateWorkflowState(allChildren, isFinal, isError);
            var completed = allChildren.Count(activity => activity.State == AgentActivityState.Completed);
            var failed = allChildren.Count(activity => activity.State is AgentActivityState.Failed or AgentActivityState.Interrupted);
            var active = allChildren.Length - completed - failed;
            var summary = active > 0
                ? $"{completed + failed}/{allChildren.Length} finished • {active} active"
                : failed > 0 ? $"{completed} completed • {failed} failed" : $"{completed}/{allChildren.Length} completed";
            projections.Insert(0, new AgentActivityProjection(
                toolCallId,
                previous?.TurnId ?? turnId,
                null,
                AgentActivityKind.Workflow,
                workflowState,
                mode == "chain" ? "Chained workflow" : "Parallel workflow",
                previous?.Task ?? string.Empty,
                summary,
                previous?.StartedUtc ?? now,
                now,
                IsTerminal(workflowState) ? previous?.CompletedUtc ?? now : null,
                projections.Sum(static activity => activity.ToolCount),
                SumUsage(projections),
                null,
                null,
                workflowState == AgentActivityState.Completed ? Bounded(ExtractContentText(result), SummaryLimit, null) : previous?.ResultSummary,
                workflowState is AgentActivityState.Failed or AgentActivityState.Interrupted
                    ? Bounded(ExtractContentText(result), SummaryLimit, "Workflow failed")
                    : previous?.FailureSummary,
                null,
                null,
                !IsTerminal(workflowState)));
        }

        return projections;
    }

    private static AgentActivityProjection NewActivity(
        string id,
        TurnId? turnId,
        string? parentId,
        AgentActivityKind kind,
        AgentActivityState state,
        string title,
        string task,
        string currentActivity,
        DateTimeOffset now,
        int? step = null,
        int? agentIndex = null,
        bool canInterrupt = false) => new(
        id,
        turnId,
        parentId,
        kind,
        state,
        Bounded(title, 120, kind == AgentActivityKind.Workflow ? "Workflow" : "Agent"),
        Bounded(task, SummaryLimit, string.Empty),
        Bounded(currentActivity, ActivityLimit, string.Empty),
        now,
        now,
        null,
        0,
        null,
        null,
        null,
        null,
        null,
        step,
        agentIndex,
        canInterrupt);

    private static bool IsAgentTool(string toolName) =>
        toolName.Equals("subagent", StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("spawn_agent", StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("spawn_agents", StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("workflow", StringComparison.OrdinalIgnoreCase);

    private static string InferMode(JsonElement arguments) =>
        arguments.TryGetProperty("chain", out var chain) && chain.ValueKind == JsonValueKind.Array
            ? "chain"
            : arguments.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array
                ? "parallel"
                : "single";

    private static (string Agent, string Task)[] ReadTasks(JsonElement arguments, string mode)
    {
        var propertyName = mode == "chain" ? "chain" : "tasks";
        if (!arguments.TryGetProperty(propertyName, out var tasks) || tasks.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return tasks.EnumerateArray()
            .Select((task, index) => (
                Bounded(ReadString(task, "agent"), 120, $"Agent {index + 1}"),
                Bounded(ReadString(task, "task"), SummaryLimit, string.Empty)))
            .ToArray();
    }

    private static AgentActivityState ReadAgentState(JsonElement item, bool isFinal, bool isError)
    {
        var stopReason = ReadString(item, "stopReason");
        if (string.Equals(stopReason, "aborted", StringComparison.OrdinalIgnoreCase))
        {
            return AgentActivityState.Interrupted;
        }

        var exitCode = ReadInt32(item, "exitCode");
        if (exitCode == -1)
        {
            return AgentActivityState.Running;
        }

        if (exitCode is > 0 || string.Equals(stopReason, "error", StringComparison.OrdinalIgnoreCase))
        {
            return AgentActivityState.Failed;
        }

        return exitCode == 0 || isFinal
            ? isError ? AgentActivityState.Failed : AgentActivityState.Completed
            : AgentActivityState.Running;
    }

    private static AgentActivityState AggregateWorkflowState(
        IReadOnlyList<AgentActivityProjection> children,
        bool isFinal,
        bool isError)
    {
        if (children.Any(static activity => activity.State == AgentActivityState.Running))
        {
            return AgentActivityState.Running;
        }

        if (children.Any(static activity => activity.State == AgentActivityState.Pending))
        {
            return isFinal ? AgentActivityState.Interrupted : AgentActivityState.Waiting;
        }

        if (children.Any(static activity => activity.State == AgentActivityState.Interrupted))
        {
            return AgentActivityState.Interrupted;
        }

        return isError || children.Any(static activity => activity.State == AgentActivityState.Failed)
            ? AgentActivityState.Failed
            : AgentActivityState.Completed;
    }

    private static string ReadCurrentActivity(JsonElement item)
    {
        if (!item.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return "Working";
        }

        foreach (var message in messages.EnumerateArray().Reverse())
        {
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var block in content.EnumerateArray().Reverse())
            {
                var type = ReadString(block, "type");
                if (type == "toolCall")
                {
                    return $"Using {ReadString(block, "name") ?? "tool"}";
                }

                if (type == "text" && ReadString(block, "text") is { Length: > 0 } text)
                {
                    return text.ReplaceLineEndings(" ").Trim();
                }
            }
        }

        return "Working";
    }

    private static int CountTools(JsonElement item)
    {
        if (!item.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return messages.EnumerateArray()
            .Where(static message => message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            .SelectMany(static message => message.GetProperty("content").EnumerateArray())
            .Count(static block => ReadString(block, "type") == "toolCall");
    }

    private static string? ReadFinalOutput(JsonElement item)
    {
        if (!item.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var message in messages.EnumerateArray().Reverse())
        {
            if (ReadString(message, "role") != "assistant" ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var text = string.Concat(content.EnumerateArray()
                .Where(static block => ReadString(block, "type") == "text")
                .Select(static block => ReadString(block, "text")));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return null;
    }

    private static string? ReadFailure(JsonElement item) =>
        ReadString(item, "errorMessage") ?? ReadString(item, "stderr");

    private static TokenUsage? ReadUsage(JsonElement item)
    {
        if (!item.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var input = ReadInt64(usage, "input") ?? 0;
        var output = ReadInt64(usage, "output") ?? 0;
        var cacheRead = ReadInt64(usage, "cacheRead") ?? 0;
        var cacheWrite = ReadInt64(usage, "cacheWrite") ?? 0;
        var reasoning = ReadInt64(usage, "reasoning");
        var total = ReadInt64(usage, "totalTokens") ?? AddSaturated(AddSaturated(input, output), AddSaturated(cacheRead, cacheWrite));
        return input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0 && reasoning is null && total == 0
            ? null
            : new TokenUsage(input, output, cacheRead, cacheWrite, reasoning, total);
    }

    private static TokenUsage? SumUsage(IEnumerable<AgentActivityProjection> activities)
    {
        var usages = activities.Select(static activity => activity.Usage).Where(static usage => usage is not null).ToArray();
        if (usages.Length == 0)
        {
            return null;
        }

        return new TokenUsage(
            usages.Aggregate(0L, static (sum, usage) => AddSaturated(sum, usage!.InputTokens)),
            usages.Aggregate(0L, static (sum, usage) => AddSaturated(sum, usage!.OutputTokens)),
            usages.Aggregate(0L, static (sum, usage) => AddSaturated(sum, usage!.CacheReadTokens)),
            usages.Aggregate(0L, static (sum, usage) => AddSaturated(sum, usage!.CacheWriteTokens)),
            usages.All(static usage => usage!.ReasoningTokens is null)
                ? null
                : usages.Aggregate(0L, static (sum, usage) => AddSaturated(sum, usage!.ReasoningTokens ?? 0)),
            usages.Aggregate(0L, static (sum, usage) => AddSaturated(sum, usage!.TotalTokens)));
    }

    private static bool TryGetDetails(JsonElement result, out JsonElement details)
    {
        details = default;
        return result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("details", out details) &&
            details.ValueKind == JsonValueKind.Object;
    }

    private static string ExtractContentText(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Concat(content.EnumerateArray()
            .Where(static block => ReadString(block, "type") == "text")
            .Select(static block => ReadString(block, "text")));
    }

    private static string StateLabel(AgentActivityState state) => state switch
    {
        AgentActivityState.Completed => "Completed",
        AgentActivityState.Failed => "Failed",
        AgentActivityState.Interrupted => "Interrupted",
        AgentActivityState.Waiting => "Waiting",
        AgentActivityState.Pending => "Pending",
        _ => "Working",
    };

    private static bool IsTerminal(AgentActivityState state) => state is
        AgentActivityState.Completed or AgentActivityState.Failed or AgentActivityState.Interrupted;

    private static string ChildId(string toolCallId, int index) => $"{toolCallId}:agent:{index}";

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt32(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var result)
            ? result
            : null;

    private static long? ReadInt64(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetInt64(out var result)
            ? result
            : null;

    private static string Bounded(string? value, int limit, string? fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback ?? string.Empty;
        }

        var normalized = value.Trim();
        return normalized.Length <= limit ? normalized : $"{normalized[..limit]}…";
    }

    private static long AddSaturated(long left, long right) => left > long.MaxValue - right
        ? long.MaxValue
        : left + right;
}
