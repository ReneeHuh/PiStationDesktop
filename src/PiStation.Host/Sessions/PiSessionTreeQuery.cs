using System.Text.Json.Nodes;
using PiStation.PiRpc.Sessions;
using PiStation.Protocol.Models;
using static PiStation.PiRpc.Sessions.PiSessionDocument;

namespace PiStation.Host.Sessions;

internal static class PiSessionTreeQuery
{
    internal static bool Matches(JsonObject entry, string? label, string? leafId, PiSessionTreeFilter filter, string[] tokens)
    {
        var type = Text(entry, "type");
        var message = entry["message"] as JsonObject;
        var role = message is null ? null : Text(message, "role");
        // Pi hides intermediate assistant tool calls in every mode, retaining errors and the current leaf.
        if (role == "assistant" && Text(entry, "id") != leafId &&
            Text(message!, "stopReason") is null or "stop" or "toolUse" &&
            !HasText(message!["content"])) return false;
        var settings = type is "label" or "custom" or "model_change" or "thinking_level_change" or "session_info";
        if (!(filter switch
        {
            PiSessionTreeFilter.Default => !settings,
            PiSessionTreeFilter.NoTools => !settings && role != "toolResult",
            PiSessionTreeFilter.UserOnly => type == "message" && role == "user",
            PiSessionTreeFilter.LabeledOnly => label is not null,
            _ => true,
        })) return false;
        if (tokens.Length == 0) return true;
        var content = type switch
        {
            "message" => role + " " + Content(message!["content"]) + (role == "bashExecution" ? " " + Text(message, "command") : string.Empty),
            "custom_message" => Text(entry, "customType") + " " + Content(entry["content"]),
            "compaction" => "compaction",
            "branch_summary" => "branch summary " + Text(entry, "summary"),
            "session_info" => "title " + Text(entry, "name"),
            "model_change" => "model " + Text(entry, "modelId"),
            "thinking_level_change" => "thinking " + Text(entry, "thinkingLevel"),
            "custom" => "custom " + Text(entry, "customType"),
            "label" => "label " + Text(entry, "label"),
            _ => string.Empty,
        };
        var searchable = label + " " + content;
        return tokens.All(token => searchable.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasText(JsonNode? content) => content is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ||
        content is JsonArray blocks && blocks.OfType<JsonObject>().Any(block => Text(block, "type") == "text" && !string.IsNullOrWhiteSpace(Text(block, "text")));

    private static string Content(JsonNode? content) => content switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray blocks => string.Concat(blocks.OfType<JsonObject>().Where(block => Text(block, "type") == "text").Select(block => Text(block, "text"))),
        _ => string.Empty,
    };
}
