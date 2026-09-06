using System.Text.Json;
using PiStation.PiRpc.Transport;

namespace PiStation.PiRpc.Decoding;

/// <summary>Reads legacy top-level command metadata and the current sourceInfo contract.</summary>
public static class PiCommandReader
{
    public static PiCommandInfo Read(JsonElement command)
    {
        var name = Text(command, "name") ?? throw new JsonException("Pi command is missing its name.");
        var source = Text(command, "source") ?? "extension";
        PiCommandSourceInfo? info = null;
        if (command.TryGetProperty("sourceInfo", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            info = new(Text(metadata, "path"), Text(metadata, "source"), Text(metadata, "scope"),
                Text(metadata, "origin"), Text(metadata, "baseDir"));
        if (source == "skill" && !name.StartsWith("skill:", StringComparison.Ordinal)) name = "skill:" + name;
        return new(name, Text(command, "description"), source,
            info?.Scope ?? Text(command, "location"), info?.Path ?? Text(command, "path"), info);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
