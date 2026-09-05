using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiStation.PiRpc.Wire.Responses;

public sealed record PiRpcResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("command")]
    public string Command { get; init; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("data")]
    public JsonElement Data { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
