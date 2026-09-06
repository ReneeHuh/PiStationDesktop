using System.Text.Json;
using PiStation.PiRpc.Wire.Events;

namespace PiStation.PiRpc.Decoding;

public sealed class PiEventDecoder
{
    public static PiRpcEvent Decode(JsonElement record)
    {
        var type = GetRequiredString(record, "type");
        return type switch
        {
            "agent_start" => new PiAgentStartedEvent(),
            "agent_end" => new PiAgentEndedEvent(GetOptionalBoolean(record, "willRetry")),
            "agent_settled" => new PiAgentSettledEvent(),
            "turn_start" => new PiTurnStartedEvent(),
            "turn_end" => new PiTurnEndedEvent(
                GetRequiredProperty(record, "message").Clone(),
                GetRequiredProperty(record, "toolResults").Clone()),
            "message_start" => new PiMessageStartedEvent(GetRequiredProperty(record, "message").Clone()),
            "message_end" => DecodeMessageCompleted(record),
            "message_update" => new PiMessageUpdateEvent(
                DecodeAssistantDelta(GetRequiredProperty(record, "assistantMessageEvent"))),
            "tool_execution_start" => new PiToolExecutionStartedEvent(
                GetRequiredString(record, "toolCallId"),
                GetRequiredString(record, "toolName"),
                GetRequiredProperty(record, "args").Clone()),
            "tool_execution_update" => new PiToolExecutionUpdatedEvent(
                GetRequiredString(record, "toolCallId"),
                GetRequiredString(record, "toolName"),
                GetRequiredProperty(record, "args").Clone(),
                GetRequiredProperty(record, "partialResult").Clone()),
            "tool_execution_end" => new PiToolExecutionCompletedEvent(
                GetRequiredString(record, "toolCallId"),
                GetRequiredString(record, "toolName"),
                GetRequiredProperty(record, "result").Clone(),
                GetOptionalBoolean(record, "isError")),
            "auto_retry_start" => new PiAutoRetryStartedEvent(
                GetRequiredInt32(record, "attempt"),
                GetRequiredInt32(record, "maxAttempts"),
                GetRequiredInt32(record, "delayMs"),
                GetRequiredString(record, "errorMessage")),
            "auto_retry_end" => new PiAutoRetryCompletedEvent(
                GetOptionalBoolean(record, "success"),
                GetRequiredInt32(record, "attempt"),
                GetOptionalString(record, "finalError")),
            "queue_update" => new PiQueueUpdatedEvent(
                GetRequiredStringArray(record, "steering"),
                GetRequiredStringArray(record, "followUp")),
            "compaction_start" => new PiCompactionStartedEvent(GetOptionalString(record, "reason") ?? "manual"),
            "compaction_end" => new PiCompactionCompletedEvent(
                GetOptionalString(record, "reason") ?? "manual",
                record.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null
                    ? result.Clone()
                    : null,
                GetOptionalBoolean(record, "aborted"),
                GetOptionalBoolean(record, "willRetry"),
                GetOptionalString(record, "errorMessage")),
            "extension_ui_request" => DecodeExtensionUiRequest(record),
            _ => new PiUnknownEvent(type, record.Clone()),
        };
    }

    private static PiMessageCompletedEvent DecodeMessageCompleted(JsonElement record)
    {
        var message = GetRequiredProperty(record, "message").Clone();
        return new PiMessageCompletedEvent(message, PiUsageReader.Read(message));
    }

    private static PiRpcEvent DecodeExtensionUiRequest(JsonElement record)
    {
        var id = GetRequiredString(record, "id");
        var method = GetRequiredString(record, "method");
        var title = GetOptionalString(record, "title") ?? string.Empty;
        var timeout = GetOptionalInt32(record, "timeout");
        return method switch
        {
            "confirm" => new PiConfirmRequestedEvent(
                id,
                title,
                GetRequiredString(record, "message"),
                timeout),
            "select" => new PiSelectRequestedEvent(id, title, GetRequiredStringArray(record, "options"), timeout),
            "input" => new PiInputRequestedEvent(id, title, GetOptionalString(record, "placeholder"), timeout),
            "editor" => new PiEditorRequestedEvent(id, title, GetOptionalString(record, "prefill")),
            "notify" => new PiExtensionUiUpdateEvent(id, method, Text: GetRequiredString(record, "message"),
                Severity: GetOptionalString(record, "notifyType")),
            "setStatus" => new PiExtensionUiUpdateEvent(id, method, GetRequiredString(record, "statusKey"), GetOptionalString(record, "statusText")),
            "setWidget" => new PiExtensionUiUpdateEvent(id, method, GetRequiredString(record, "widgetKey"),
                Lines: record.TryGetProperty("widgetLines", out var lines) && lines.ValueKind == JsonValueKind.Array
                    ? GetRequiredStringArray(record, "widgetLines") : null,
                Placement: GetOptionalString(record, "widgetPlacement")),
            "setTitle" => new PiExtensionUiUpdateEvent(id, method, Text: title),
            "set_editor_text" => new PiExtensionUiUpdateEvent(id, method, Text: GetRequiredString(record, "text")),
            _ => new PiUnknownEvent("extension_ui_request", record.Clone()),
        };
    }

    private static PiAssistantDelta DecodeAssistantDelta(JsonElement record)
    {
        var type = GetRequiredString(record, "type");
        var contentIndex = GetRequiredInt32(record, "contentIndex");
        return type switch
        {
            "text_start" => new PiTextStartedDelta(contentIndex),
            "text_delta" => new PiTextDelta(contentIndex, GetRequiredString(record, "delta")),
            "text_end" => new PiTextCompletedDelta(contentIndex, GetRequiredString(record, "content")),
            "thinking_start" => new PiThinkingStartedDelta(contentIndex),
            "thinking_delta" => new PiThinkingDelta(contentIndex, GetRequiredString(record, "delta")),
            "thinking_end" => new PiThinkingCompletedDelta(contentIndex, GetRequiredString(record, "content")),
            "toolcall_start" => new PiToolCallStartedDelta(
                contentIndex,
                GetRequiredString(record, "id"),
                GetRequiredString(record, "toolName")),
            "toolcall_delta" => new PiToolCallArgumentsDelta(contentIndex, GetRequiredString(record, "delta")),
            "toolcall_end" => new PiToolCallCompletedDelta(
                contentIndex,
                GetRequiredProperty(record, "toolCall").Clone()),
            _ => new PiUnknownAssistantDelta(type, contentIndex, record.Clone()),
        };
    }

    private static JsonElement GetRequiredProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            throw new JsonException($"Pi record is missing required property '{name}'.");
        }

        return value;
    }

    private static string GetRequiredString(JsonElement element, string name)
    {
        var property = GetRequiredProperty(element, name);
        return property.ValueKind == JsonValueKind.String
            ? property.GetString()!
            : throw new JsonException($"Pi property '{name}' must be a string.");
    }

    private static string? GetOptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetRequiredInt32(JsonElement element, string name)
    {
        var property = GetRequiredProperty(element, name);
        return property.TryGetInt32(out var value)
            ? value
            : throw new JsonException($"Pi property '{name}' must be a 32-bit integer.");
    }

    private static int? GetOptionalInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static string[] GetRequiredStringArray(JsonElement element, string name)
    {
        var property = GetRequiredProperty(element, name);
        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"Pi property '{name}' must be an array.");
        }

        var result = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"Pi property '{name}' must contain only strings.");
            }

            result.Add(item.GetString()!);
        }

        return [.. result];
    }

    private static bool GetOptionalBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();
}
