using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiStation.PiRpc.Sessions;

/// <summary>A lossless, bounded reader for Pi's current v3 JSONL session tree.</summary>
public sealed class PiSessionDocument
{
    public const int MaximumBytes = 128 * 1024 * 1024;
    public const int MaximumEntries = 100_000;
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
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Pi session exceeds the 128 MiB import limit.");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (buffer.Length > MaximumBytes) throw new InvalidDataException("Pi session exceeds the 128 MiB import limit.");
        return Parse(buffer.ToArray());
    }

    public static PiSessionDocument Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Pi session exceeds the 128 MiB import limit.");
        var entries = new List<JsonObject>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        JsonObject? header = null;
        using var reader = new StringReader(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
        var number = 0;
        var version = 3;
        string? previousId = null;
        while (reader.ReadLine() is { } line)
        {
            number++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject entry;
            try { entry = JsonNode.Parse(line) as JsonObject ?? throw new JsonException(); }
            catch (JsonException) { throw new InvalidDataException($"Invalid Pi session JSON on line {number}. The source was not changed."); }
            if (header is null)
            {
                if (!int.TryParse(entry["version"]?.ToString() ?? "1", out version) || version is < 1 or > 3 || Text(entry, "type") != "session" ||
                    !Guid.TryParse(Text(entry, "id"), out _) || string.IsNullOrWhiteSpace(Text(entry, "cwd")))
                    throw new InvalidDataException("Select a supported Pi v1, v2, or v3 JSONL session.");
                header = entry;
                header["version"] = 3;
                continue;
            }
            if (version == 1)
            {
                // Deterministic IDs keep retries and source revisions stable. The original file is never changed.
                entry["id"] = "legacy-" + entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                entry["parentId"] = previousId;
                if (entry["firstKeptEntryIndex"] is JsonValue index && index.TryGetValue<int>(out var keptIndex))
                {
                    if (keptIndex > 0 && keptIndex <= entries.Count) entry["firstKeptEntryId"] = Text(entries[keptIndex - 1], "id");
                    entry.Remove("firstKeptEntryIndex");
                }
            }
            if (version < 3 && entry["message"] is JsonObject legacyMessage && Text(legacyMessage, "role") == "hookMessage")
                legacyMessage["role"] = "custom";
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
            previousId = id;
            if (entries.Count > MaximumEntries) throw new InvalidDataException("Pi session exceeds the 100,000-entry limit.");
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

    public IReadOnlyDictionary<string, (string Label, string? Timestamp)> Labels()
    {
        var labels = new Dictionary<string, (string, string?)>(StringComparer.Ordinal);
        foreach (var entry in _entries)
        {
            if (Text(entry, "type") != "label" || Text(entry, "targetId") is not { } target || !_byId.ContainsKey(target)) continue;
            if (Text(entry, "label") is { Length: > 0 } label) labels[target] = (label, Text(entry, "timestamp"));
            else labels.Remove(target);
        }
        return labels;
    }

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
            var branch = Branch(leafId).ToList();
            // Labels are session-wide and may have been appended on another branch. Carry
            // their current values into a fork so edits elsewhere apply to retained entries.
            var labels = Labels();
            string? parent = leafId;
            foreach (var target in branch.ToArray())
            {
                if (!labels.TryGetValue(Text(target, "id")!, out var label)) continue;
                var id = Guid.NewGuid().ToString("N");
                branch.Add(new JsonObject { ["type"] = "label", ["id"] = id, ["parentId"] = parent,
                    ["timestamp"] = label.Timestamp, ["targetId"] = Text(target, "id"), ["label"] = label.Label });
                parent = id;
            }
            // Clear historical labels on this branch when their latest change cleared them elsewhere.
            foreach (var target in branch.Where(entry => Text(entry, "type") == "label").Select(entry => Text(entry, "targetId")).Distinct().ToArray())
            {
                if (target is null || labels.ContainsKey(target)) continue;
                var id = Guid.NewGuid().ToString("N");
                branch.Add(new JsonObject { ["type"] = "label", ["id"] = id, ["parentId"] = parent,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"), ["targetId"] = target });
                parent = id;
            }
            entries = branch;
        }
        return Encoding.UTF8.GetBytes(header.ToJsonString() + "\n" + string.Concat(entries.Select(entry => entry.ToJsonString() + "\n")));
    }

    public string ToHtml(string title) => PiSessionHtml.Render(this, title);

    public static string Preview(JsonObject entry, int limit = 240)
    {
        var text = entry["message"] is JsonObject message ? MessageText(message) : Text(entry, "name") ?? Text(entry, "summary") ?? Text(entry, "type") ?? "Entry";
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length > limit ? text[..limit] + "…" : text;
    }

    public static string MessageText(JsonObject message) => message["content"] switch
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
