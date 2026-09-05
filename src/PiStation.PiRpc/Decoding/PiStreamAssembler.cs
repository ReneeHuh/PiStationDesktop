using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using PiStation.PiRpc.Wire.Events;

namespace PiStation.PiRpc.Decoding;

public enum PiToolExecutionStatus
{
    Running,
    Completed,
    Failed,
}

public sealed record PiToolExecutionSnapshot(
    string ToolCallId,
    string ToolName,
    string Output,
    PiToolExecutionStatus Status);

public sealed class PiStreamAssembler
{
    private readonly SortedDictionary<int, string> _textBlocks = [];
    private readonly SortedDictionary<int, string> _thinkingBlocks = [];
    private readonly Dictionary<int, ToolCallBuilder> _toolCalls = [];
    private readonly Dictionary<string, PiToolExecutionSnapshot> _toolExecutions = new(StringComparer.Ordinal);

    public string Text => string.Concat(_textBlocks.Values);

    public string Thinking => string.Concat(_thinkingBlocks.Values);

    public bool IsSettled { get; private set; } = true;

    public IReadOnlyDictionary<string, PiToolExecutionSnapshot> ToolExecutions =>
        new ReadOnlyDictionary<string, PiToolExecutionSnapshot>(_toolExecutions);

    public void Apply(PiRpcEvent @event)
    {
        switch (@event)
        {
            case PiAgentStartedEvent:
                IsSettled = false;
                break;
            case PiAgentSettledEvent:
                IsSettled = true;
                break;
            case PiMessageStartedEvent started when IsAssistantMessage(started.Message):
                ResetAssistantMessage();
                break;
            case PiMessageUpdateEvent update:
                ApplyDelta(update.Delta);
                break;
            case PiMessageCompletedEvent completed when IsAssistantMessage(completed.Message):
                ApplyAuthoritativeMessage(completed.Message);
                break;
            case PiToolExecutionStartedEvent started:
                _toolExecutions[started.ToolCallId] = new PiToolExecutionSnapshot(
                    started.ToolCallId,
                    started.ToolName,
                    string.Empty,
                    PiToolExecutionStatus.Running);
                break;
            case PiToolExecutionUpdatedEvent updated:
                _toolExecutions[updated.ToolCallId] = new PiToolExecutionSnapshot(
                    updated.ToolCallId,
                    updated.ToolName,
                    ExtractText(updated.PartialResult),
                    PiToolExecutionStatus.Running);
                break;
            case PiToolExecutionCompletedEvent completed:
                _toolExecutions[completed.ToolCallId] = new PiToolExecutionSnapshot(
                    completed.ToolCallId,
                    completed.ToolName,
                    ExtractText(completed.Result),
                    completed.IsError ? PiToolExecutionStatus.Failed : PiToolExecutionStatus.Completed);
                break;
        }
    }

    private void ApplyDelta(PiAssistantDelta delta)
    {
        switch (delta)
        {
            case PiTextStartedDelta started:
                _textBlocks[started.ContentIndex] = string.Empty;
                break;
            case PiTextDelta text:
                _textBlocks[text.ContentIndex] = GetCurrent(_textBlocks, text.ContentIndex) + text.Delta;
                break;
            case PiTextCompletedDelta completed:
                _textBlocks[completed.ContentIndex] = completed.Content;
                break;
            case PiThinkingStartedDelta started:
                _thinkingBlocks[started.ContentIndex] = string.Empty;
                break;
            case PiThinkingDelta thinking:
                _thinkingBlocks[thinking.ContentIndex] = GetCurrent(_thinkingBlocks, thinking.ContentIndex) + thinking.Delta;
                break;
            case PiThinkingCompletedDelta completed:
                _thinkingBlocks[completed.ContentIndex] = completed.Content;
                break;
            case PiToolCallStartedDelta started:
                _toolCalls[started.ContentIndex] = new ToolCallBuilder(started.ToolCallId, started.ToolName);
                break;
            case PiToolCallArgumentsDelta arguments:
                if (_toolCalls.TryGetValue(arguments.ContentIndex, out var builder))
                {
                    builder.Arguments.Append(arguments.Delta);
                }
                break;
            case PiToolCallCompletedDelta completed:
                ApplyCompletedToolCall(completed.ContentIndex, completed.ToolCall);
                break;
        }
    }

    private void ApplyAuthoritativeMessage(JsonElement message)
    {
        _textBlocks.Clear();
        _thinkingBlocks.Clear();
        _toolCalls.Clear();

        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;
        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var typeProperty) || typeProperty.ValueKind != JsonValueKind.String)
            {
                index++;
                continue;
            }

            switch (typeProperty.GetString())
            {
                case "text" when block.TryGetProperty("text", out var text):
                    _textBlocks[index] = text.GetString() ?? string.Empty;
                    break;
                case "thinking" when block.TryGetProperty("thinking", out var thinking):
                    _thinkingBlocks[index] = thinking.GetString() ?? string.Empty;
                    break;
                case "toolCall":
                    ApplyCompletedToolCall(index, block);
                    break;
            }

            index++;
        }
    }

    private void ApplyCompletedToolCall(int contentIndex, JsonElement toolCall)
    {
        var id = toolCall.TryGetProperty("id", out var idProperty) ? idProperty.GetString() : null;
        var name = toolCall.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
        if (id is null || name is null)
        {
            return;
        }

        var builder = new ToolCallBuilder(id, name);
        if (toolCall.TryGetProperty("arguments", out var arguments))
        {
            builder.Arguments.Append(arguments.ValueKind == JsonValueKind.String
                ? arguments.GetString()
                : arguments.GetRawText());
        }

        _toolCalls[contentIndex] = builder;
    }

    private void ResetAssistantMessage()
    {
        _textBlocks.Clear();
        _thinkingBlocks.Clear();
        _toolCalls.Clear();
        _toolExecutions.Clear();
    }

    private static bool IsAssistantMessage(JsonElement message) =>
        message.TryGetProperty("role", out var role) &&
        role.ValueKind == JsonValueKind.String &&
        string.Equals(role.GetString(), "assistant", StringComparison.Ordinal);

    private static string GetCurrent(SortedDictionary<int, string> blocks, int index) =>
        blocks.TryGetValue(index, out var value) ? value : string.Empty;

    private static string ExtractText(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                type.GetString() == "text" &&
                block.TryGetProperty("text", out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                text.Append(value.GetString());
            }
        }

        return text.ToString();
    }

    private sealed class ToolCallBuilder(string id, string name)
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public StringBuilder Arguments { get; } = new();
    }
}
