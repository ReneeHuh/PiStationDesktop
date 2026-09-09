using System.Globalization;
using System.Text.Json;

namespace PiStation.ClientRuntime;

internal static class TailscaleServeConfiguration
{
    public static void EnsurePortAvailable(string json, int port)
    {
        if (Read(json, port).Count != 0)
            throw new InvalidOperationException("That port is already used by Tailscale Serve or Funnel. Choose another port.");
    }

    public static bool HasForward(string json, int port, int localPort)
    {
        var entries = Read(json, port);
        return entries.Count == 1 && entries[0] == "foreground:127.0.0.1:" + localPort.ToString(CultureInfo.InvariantCulture);
    }

    private static List<string> Read(string json, int port)
    {
        if (json.Length > 4 * 1024 * 1024) throw new InvalidDataException("Tailscale configuration exceeds its size limit.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
        var entries = new List<string>();
        Visit(document.RootElement, port.ToString(CultureInfo.InvariantCulture), entries, 0);
        return entries;
    }

    private static void Visit(JsonElement root, string port, List<string> entries, int depth)
    {
        if (root.ValueKind == JsonValueKind.Null) return;
        if (root.ValueKind != JsonValueKind.Object || depth > 4) throw new JsonException("Invalid Tailscale configuration.");
        if (Map(root, "TCP") is { } tcp && tcp.TryGetProperty(port, out var handler))
        {
            var forward = handler.ValueKind == JsonValueKind.Object && handler.TryGetProperty("TCPForward", out var target) &&
                target.ValueKind == JsonValueKind.String && !Enabled(handler, "HTTPS") && !Enabled(handler, "HTTP") &&
                !Enabled(handler, "TerminateTLS") && !Enabled(handler, "ProxyProtocol") ? target.GetString()! : "occupied";
            entries.Add(depth > 0 ? "foreground:" + forward : forward);
        }
        if (Map(root, "Web") is { } web && web.EnumerateObject().Any(item => item.Name.EndsWith(":" + port, StringComparison.Ordinal)))
            entries.Add("web");
        if (Map(root, "AllowFunnel") is { } funnel)
            foreach (var entry in funnel.EnumerateObject())
            {
                if (entry.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException("Invalid Funnel configuration.");
                if (entry.Name.EndsWith(":" + port, StringComparison.Ordinal) && entry.Value.ValueKind == JsonValueKind.True) entries.Add("funnel");
            }
        if (Map(root, "Foreground") is { } foreground)
            foreach (var item in foreground.EnumerateObject()) Visit(item.Value, port, entries, depth + 1);
    }

    private static JsonElement? Map(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid Tailscale configuration map.");
        return value;
    }

    private static bool Enabled(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind switch
    {
        JsonValueKind.False or JsonValueKind.Null => false,
        JsonValueKind.String => value.GetString()!.Length != 0,
        JsonValueKind.Number => !value.TryGetInt32(out var number) || number != 0,
        _ => true,
    };
}
