using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiStation.PiRpc.Decoding;

namespace PiStation.Host.Usage;

internal sealed record UsageRecord(string Key, string SessionId, DateTimeOffset Timestamp, string Provider, string Model,
    long Input, long Output, long CacheRead, long CacheWrite, long Reasoning, long Total, decimal? Cost,
    bool Child = false, bool Legacy = false)
{
    public static UsageRecord? Read(JsonElement message, string sessionId, string fallbackId, bool child = false)
    {
        if (Text(message, "role") != "assistant" || PiUsageReader.Read(message) is not { } usage) return null;
        var provider = Text(message, "provider") ?? "unknown";
        var model = Text(message, "model") ?? "unknown";
        var timestamp = Date(message, "timestamp");
        if (timestamp is null) return null; // Never invent a billing date for undated history.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(provider); writer.WriteStringValue(model); writer.WriteStringValue(timestamp.Value.ToUniversalTime());
            if (message.TryGetProperty("content", out var content)) Canonical(writer, content);
            else writer.WriteStringValue(fallbackId);
            writer.WriteNumberValue(usage!.InputTokens); writer.WriteNumberValue(usage.OutputTokens);
            writer.WriteNumberValue(usage.CacheReadTokens); writer.WriteNumberValue(usage.CacheWriteTokens);
            writer.WriteNumberValue(usage.TotalTokens); writer.WriteEndArray();
        }
        return new(Convert.ToHexString(SHA256.HashData(buffer.ToArray())), sessionId, timestamp.Value,
            provider, model, usage!.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens,
            usage.ReasoningTokens ?? 0, usage.TotalTokens, usage.TotalCost, child);
    }
    private static void Canonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); Canonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Canonical(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
    internal static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    internal static DateTimeOffset? Date(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind == JsonValueKind.String && property.TryGetDateTimeOffset(out var date)) return date;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var milliseconds) && milliseconds is >= -62135596800000 and <= 253402300799999)
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        return null;
    }
}

internal sealed record UsageFileCache(long Length, long ModifiedTicks, string SessionId, int Malformed, UsageRecord[] Records);
internal sealed record UsageDiskCache(int Version, Dictionary<string, UsageFileCache> Files);
internal sealed record UsageRate(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite);
internal sealed record UsagePriceCache(DateTimeOffset UpdatedUtc, Dictionary<string, UsageRate> Rates);

[JsonSerializable(typeof(UsageDiskCache))]
[JsonSerializable(typeof(UsagePriceCache))]
[JsonSerializable(typeof(UsageRecord))]
internal partial class UsageJsonContext : JsonSerializerContext;
