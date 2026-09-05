using System.Text;
using System.Text.Json.Nodes;

namespace PiStation.FakePi;

internal sealed class FakeSessionStore
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly string _sessionFile;

    public FakeSessionStore(string sessionDirectory, string sessionId)
    {
        Directory.CreateDirectory(sessionDirectory);
        _sessionFile = Path.Combine(sessionDirectory, $"{sessionId}.jsonl");
    }

    public string SessionFile => _sessionFile;

    public async Task<(JsonArray Entries, string? LeafId)> ReadAsync(CancellationToken cancellationToken)
    {
        var entries = new JsonArray();
        if (!File.Exists(_sessionFile))
        {
            return (entries, null);
        }

        foreach (var line in await File.ReadAllLinesAsync(_sessionFile, cancellationToken).ConfigureAwait(false))
        {
            if (JsonNode.Parse(line) is JsonObject entry)
            {
                entries.Add(entry);
            }
        }

        var leafId = entries.Count == 0 ? null : entries[^1]?["id"]?.GetValue<string>();
        return (entries, leafId);
    }

    public async Task AppendTurnAsync(string prompt, string answer, CancellationToken cancellationToken)
    {
        var (entries, leafId) = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var sequence = entries.Count + 1;
        var userId = $"entry-{sequence:D4}";
        var assistantId = $"entry-{sequence + 1:D4}";
        var user = new JsonObject
        {
            ["type"] = "message",
            ["id"] = userId,
            ["parentId"] = leafId,
            ["timestamp"] = "2026-09-01T00:00:00.000Z",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = prompt },
        };
        var assistant = new JsonObject
        {
            ["type"] = "message",
            ["id"] = assistantId,
            ["parentId"] = userId,
            ["timestamp"] = "2026-09-01T00:00:01.000Z",
            ["message"] = AssistantMessage(answer),
        };

        await AppendAsync(user, cancellationToken).ConfigureAwait(false);
        await AppendAsync(assistant, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RewindAsync(string? entryId, CancellationToken cancellationToken)
    {
        if (entryId is null)
        {
            File.Delete(_sessionFile);
            return true;
        }

        if (!File.Exists(_sessionFile))
        {
            return false;
        }

        var lines = await File.ReadAllLinesAsync(_sessionFile, cancellationToken).ConfigureAwait(false);
        var retained = new List<string>();
        var found = false;
        foreach (var line in lines)
        {
            retained.Add(line);
            if (JsonNode.Parse(line) is JsonObject entry && entry["id"]?.GetValue<string>() == entryId)
            {
                found = true;
                break;
            }
        }

        if (!found)
        {
            return false;
        }

        await File.WriteAllLinesAsync(_sessionFile, retained, Utf8WithoutBom, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    public static JsonObject AssistantMessage(string text) => new()
    {
        ["role"] = "assistant",
        ["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = text },
        },
        ["usage"] = Usage(),
    };

    public static JsonObject Usage() => new()
    {
        ["input"] = 1,
        ["output"] = 1,
        ["cacheRead"] = 0,
        ["cacheWrite"] = 0,
        ["totalTokens"] = 2,
        ["cost"] = new JsonObject
        {
            ["input"] = 0,
            ["output"] = 0,
            ["cacheRead"] = 0,
            ["cacheWrite"] = 0,
            ["total"] = 0,
        },
    };

    private async Task AppendAsync(JsonObject entry, CancellationToken cancellationToken)
    {
        var line = entry.ToJsonString() + "\n";
        await File.AppendAllTextAsync(_sessionFile, line, Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
    }
}
