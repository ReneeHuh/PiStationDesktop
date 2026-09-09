using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiStation.ClientRuntime;

/// <summary>Tab-owned, in-memory diagnostics. No response bodies, cookies or request headers.</summary>
public sealed class BrowserDiagnostics
{
    private const int Capacity = 200;
    private readonly Queue<JsonObject> _console = new();
    private readonly Queue<JsonObject> _network = new();
    private readonly List<JsonObject> _actions = [];
    private readonly Dictionary<string, (string Url, string Method)> _requests = new(StringComparer.Ordinal);
    public DateTimeOffset SinceUtc { get; private set; } = DateTimeOffset.UtcNow;
    public bool ConsoleTruncated { get; private set; }
    public bool NetworkTruncated { get; private set; }
    public bool ActionsTruncated { get; private set; }
    public IEnumerable<JsonObject> ConsoleEntries => _console;
    public IEnumerable<JsonObject> NetworkEntries => _network;
    public IEnumerable<JsonObject> ActionTimeline => _actions;

    public void Clear()
    {
        _console.Clear(); _network.Clear(); _actions.Clear(); _requests.Clear();
        ConsoleTruncated = NetworkTruncated = ActionsTruncated = false;
        SinceUtc = DateTimeOffset.UtcNow;
    }

    public void NavigationStarted() => _requests.Clear();

    public void StartAction(string id, string operation)
    {
        if (_actions.Count == Capacity) { _actions.RemoveAt(0); ActionsTruncated = true; }
        _actions.Add(new JsonObject { ["id"] = id, ["action"] = operation, ["status"] = "running", ["startedAt"] = DateTimeOffset.UtcNow });
    }

    public void FinishAction(string id, string status, string? error = null)
    {
        var action = _actions.LastOrDefault(item => item["id"]?.GetValue<string>() == id);
        if (action is null) return;
        action["status"] = status;
        action["completedAt"] = DateTimeOffset.UtcNow;
        if (error is not null) action["error"] = Limit(error, 512);
    }

    public void Receive(string method, string json, Func<string, string>? logicalUrl = null)
    {
        // Browser event payloads are untrusted and may be much larger than our retained data.
        if (json.Length > 256 * 1024) { ConsoleTruncated = NetworkTruncated = true; return; }
        try
        {
            using var document = JsonDocument.Parse(json);
            var p = document.RootElement;
            var timestamp = DateTimeOffset.UtcNow;
            void Console(string level, string text, string source)
            {
                ConsoleTruncated |= _console.Count == Capacity || text.Length > 1024;
                if (_console.Count == Capacity) _console.Dequeue();
                _console.Enqueue(new JsonObject { ["level"] = Limit(level, 32), ["text"] = Limit(text, 1024), ["source"] = Limit(source, 64), ["timestamp"] = timestamp });
            }
            void Network(string url, string verb, int? status, string? error)
            {
                NetworkTruncated |= _network.Count == Capacity || url.Length > 2048 || error?.Length > 512;
                if (_network.Count == Capacity) _network.Dequeue();
                _network.Enqueue(new JsonObject { ["url"] = Limit(logicalUrl?.Invoke(url) ?? url, 2048), ["method"] = Limit(verb, 32),
                    ["status"] = status, ["failed"] = true, ["errorText"] = error is null ? null : Limit(error, 512), ["timestamp"] = timestamp });
            }
            switch (method)
            {
                case "Runtime.consoleAPICalled":
                    var arguments = p.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array
                        ? string.Join(" ", args.EnumerateArray().Take(16).Select(arg => arg.TryGetProperty("value", out var value)
                            ? Limit(value.ToString(), 1024) : Text(arg, "description", 1024))) : "";
                    Console(Text(p, "type", 32), arguments, "console"); break;
                case "Runtime.exceptionThrown":
                    var details = p.GetProperty("exceptionDetails");
                    Console("error", details.TryGetProperty("exception", out var exception)
                        ? Text(exception, "description", 2048) : Text(details, "text", 2048), "exception"); break;
                case "Log.entryAdded":
                    var entry = p.GetProperty("entry");
                    Console(Text(entry, "level", 32), Text(entry, "text", 2048), Text(entry, "source", 64)); break;
                case "Network.requestWillBeSent":
                    if (_requests.Count == 512) { _requests.Remove(_requests.Keys.First()); NetworkTruncated = true; }
                    var request = p.GetProperty("request");
                    _requests[Text(p, "requestId", 160)] = (Text(request, "url", 2048), Text(request, "method", 32)); break;
                case "Network.responseReceived":
                    var response = p.GetProperty("response");
                    if (response.TryGetProperty("status", out var code) && code.TryGetDouble(out var status) && status >= 400)
                    {
                        _requests.TryGetValue(Text(p, "requestId", 160), out var sent);
                        Network(Text(response, "url", 2048), sent.Method ?? "GET", (int)status, null);
                    }
                    break;
                case "Network.loadingFailed":
                    if (_requests.Remove(Text(p, "requestId", 160), out var failed))
                        Network(failed.Url, failed.Method, null, Text(p, "errorText", 1024));
                    break;
                case "Network.loadingFinished": _requests.Remove(Text(p, "requestId", 160)); break;
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { /* Malformed diagnostics must not break browser automation or the UI event loop. */ }
    }

    public static string Text(JsonElement value, string property, int limit) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) ? Limit(item.ToString(), limit) : "";
    public static string Limit(string? value, int length) => value is null ? "" : value.Length <= length ? value : value[..length];
}
