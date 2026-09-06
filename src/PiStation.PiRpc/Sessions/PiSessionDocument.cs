using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiStation.PiRpc.Sessions;

/// <summary>A lossless, bounded reader for Pi's current v3 JSONL session tree.</summary>
public sealed class PiSessionDocument
{
    public const int MaximumBytes = 64 * 1024 * 1024;
    public const int MaximumEntries = 50_000;
    private readonly JsonObject _header;
    private readonly IReadOnlyList<JsonObject> _entries;
    private readonly Dictionary<string, JsonObject> _byId;
    public string Revision { get; }
    public string SessionId => Text(_header, "id")!;
    public string ProjectDirectory => Text(_header, "cwd")!;
    public string? LeafId => _entries.Count == 0 ? null : Text(_entries[^1], "id");
    public IReadOnlyList<JsonObject> Entries => _entries;
    public string Title => _entries.LastOrDefault(entry => Text(entry, "type") == "session_info") is { } info
        ? Text(info, "name") ?? "Pi session" : Branch().FirstOrDefault(IsUser) is { } first
            ? Preview(first, 100) : "Pi session";

    private PiSessionDocument(JsonObject header, IReadOnlyList<JsonObject> entries, string revision)
    {
        _header = header;
        _entries = entries;
        _byId = entries.ToDictionary(entry => Text(entry, "id")!, StringComparer.Ordinal);
        Revision = revision;
    }

    public static async Task<PiSessionDocument> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Pi session exceeds the 64 MiB import limit.");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (buffer.Length > MaximumBytes) throw new InvalidDataException("Pi session exceeds the 64 MiB import limit.");
        return Parse(buffer.ToArray());
    }

    public static PiSessionDocument Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Pi session exceeds the 64 MiB import limit.");
        var entries = new List<JsonObject>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        JsonObject? header = null;
        using var reader = new StringReader(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
        var number = 0;
        while (reader.ReadLine() is { } line)
        {
            number++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject entry;
            try { entry = JsonNode.Parse(line) as JsonObject ?? throw new JsonException(); }
            catch (JsonException) { throw new InvalidDataException($"Invalid Pi session JSON on line {number}. The source was not changed."); }
            if (header is null)
            {
                if (Text(entry, "type") != "session" || entry["version"]?.ToString() != "3" ||
                    !Guid.TryParse(Text(entry, "id"), out _) || string.IsNullOrWhiteSpace(Text(entry, "cwd")))
                    throw new InvalidDataException("Select a Pi v3 JSONL session. Open older sessions in a current Pi version before importing them.");
                header = entry;
                continue;
            }
            var id = Text(entry, "id");
            var parent = Text(entry, "parentId");
            if (Text(entry, "type") is null or "session" || string.IsNullOrWhiteSpace(id) || id.Length > 256 ||
                !entry.ContainsKey("parentId") || entry["parentId"] is not null && parent is null ||
                parent is not null && !ids.Contains(parent) || !ids.Add(id))
                throw new InvalidDataException($"Invalid or duplicate Pi tree entry on line {number}.");
            if (Text(entry, "type") == "message" && (entry["message"] is not JsonObject message ||
                string.IsNullOrWhiteSpace(Text(message, "role")) ||
                message["content"] is not null && message["content"] is not JsonArray && Text(message, "content") is null))
                throw new InvalidDataException($"Invalid Pi message on line {number}.");
            if (Text(entry, "type") == "model_change" && (string.IsNullOrWhiteSpace(Text(entry, "provider")) || string.IsNullOrWhiteSpace(Text(entry, "modelId"))))
                throw new InvalidDataException($"Invalid model configuration on line {number}.");
            entries.Add(entry);
            if (entries.Count > MaximumEntries) throw new InvalidDataException("Pi session exceeds the 50,000-entry limit.");
        }
        if (header is null) throw new InvalidDataException("The session is empty.");
        return new(header, entries, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public IReadOnlyList<JsonObject> Branch(string? leafId = null)
    {
        var branch = new List<JsonObject>();
        var id = leafId ?? LeafId;
        while (id is not null)
        {
            if (!_byId.TryGetValue(id, out var entry)) throw new InvalidDataException("The selected conversation point no longer exists.");
            branch.Add(entry);
            id = Text(entry, "parentId");
        }
        branch.Reverse();
        return branch;
    }

    public (string? Provider, string? Model, string? Thinking) Configuration(string? leafId = null)
    {
        string? provider = null, model = null, thinking = null;
        foreach (var entry in Branch(leafId))
        {
            if (Text(entry, "type") == "model_change") { provider = Text(entry, "provider"); model = Text(entry, "modelId"); }
            if (Text(entry, "type") == "thinking_level_change") thinking = Text(entry, "thinkingLevel");
            if (entry["message"] is JsonObject message && Text(message, "role") == "assistant" && Text(message, "provider") is { } messageProvider)
            { provider = messageProvider; model = Text(message, "model") ?? model; }
        }
        return (provider, model, thinking);
    }

    public static bool CanFork(JsonObject entry) => Text(entry, "type") == "message" && entry["message"] is JsonObject message &&
        Text(message, "role") == "assistant" && Text(message, "stopReason") == "stop" &&
        !(message["content"] as JsonArray ?? []).OfType<JsonObject>().Any(block => Text(block, "type") == "toolCall");

    public byte[] Copy(string newSessionId, string cwd, string sourcePath, string? leafId = null)
    {
        if (!Guid.TryParse(newSessionId, out _)) throw new ArgumentException("Invalid new session identity.", nameof(newSessionId));
        var header = (JsonObject)_header.DeepClone();
        header["id"] = newSessionId;
        header["cwd"] = cwd;
        header["parentSession"] = sourcePath;
        IReadOnlyList<JsonObject> entries = _entries;
        if (leafId is not null)
        {
            if (!_byId.TryGetValue(leafId, out var leaf) || !CanFork(leaf))
                throw new InvalidDataException("Choose a completed assistant response to fork. Tool calls and partial responses are not fork points.");
            entries = Branch(leafId);
        }
        return Encoding.UTF8.GetBytes(header.ToJsonString() + "\n" + string.Concat(entries.Select(entry => entry.ToJsonString() + "\n")));
    }

    public string ToHtml(string title)
    {
        var html = new StringBuilder("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>")
            .Append(WebUtility.HtmlEncode(title)).Append("</title><style>body{max-width:900px;margin:40px auto;padding:0 24px;font:16px/1.6 system-ui}article{border-top:1px solid #ccc;padding:16px 0}pre{white-space:pre-wrap;overflow-wrap:anywhere;font:inherit}small{color:#666}</style><h1>")
            .Append(WebUtility.HtmlEncode(title)).Append("</h1><p>Active conversation branch. Referenced workspace files are external to this export.</p>");
        foreach (var entry in Branch().Where(entry => entry["message"] is JsonObject))
        {
            var message = (JsonObject)entry["message"]!;
            html.Append("<article><h2>").Append(WebUtility.HtmlEncode(Text(message, "role") ?? "Message"))
                .Append("</h2><small>").Append(WebUtility.HtmlEncode(Text(entry, "timestamp")))
                .Append("</small><pre>").Append(WebUtility.HtmlEncode(MessageText(message))).Append("</pre></article>");
        }
        return html.Append("</html>").ToString();
    }

    public static string Preview(JsonObject entry, int limit = 240)
    {
        var text = entry["message"] is JsonObject message ? MessageText(message) : Text(entry, "name") ?? Text(entry, "summary") ?? Text(entry, "type") ?? "Entry";
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length > limit ? text[..limit] + "…" : text;
    }

    private static string MessageText(JsonObject message) => message["content"] switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray blocks => string.Join("\n", blocks.OfType<JsonObject>().Select(block => Text(block, "type") switch
        {
            "text" => Text(block, "text"), "thinking" => Text(block, "thinking"), "image" => "[Embedded image — available in JSONL export]",
            "toolCall" => "Tool: " + Text(block, "name") + "\n" + block["arguments"]?.ToJsonString(), _ => "[" + Text(block, "type") + "]",
        })),
        _ => Text(message, "output") ?? Text(message, "summary") ?? string.Empty,
    };
    public static string? Text(JsonObject value, string key) => value[key] is JsonValue node && node.TryGetValue<string>(out var text) ? text : null;
    private static bool IsUser(JsonObject entry) => entry["message"] is JsonObject message && Text(message, "role") == "user";
}
