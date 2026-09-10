using System.Text.Json;
using PiStation.PiRpc.Wire.Events;

namespace PiStation.PiRpc.Decoding;

public static class PiUsageReader
{
    public static PiTokenUsage? Read(JsonElement message)
    {
        if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object ||
            !TryReadNonNegativeInt64(usage, "input", out var input) ||
            !TryReadNonNegativeInt64(usage, "output", out var output) ||
            !TryReadNonNegativeInt64(usage, "cacheRead", out var cacheRead) ||
            !TryReadNonNegativeInt64(usage, "cacheWrite", out var cacheWrite) ||
            !TryReadNonNegativeInt64(usage, "totalTokens", out var totalTokens))
        {
            return null;
        }

        long? reasoning = null;
        if (usage.TryGetProperty("reasoning", out var reasoningProperty))
        {
            if (reasoningProperty.ValueKind != JsonValueKind.Number || !reasoningProperty.TryGetInt64(out var reasoningValue) || reasoningValue < 0)
            {
                return null;
            }

            reasoning = reasoningValue;
        }

        decimal? cost = null;
        if (usage.TryGetProperty("cost", out var costs) && costs.ValueKind == JsonValueKind.Object &&
            costs.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number &&
            total.TryGetDecimal(out var amount) && amount >= 0)
        {
            cost = amount;
        }

        return new PiTokenUsage(input, output, cacheRead, cacheWrite, reasoning, totalTokens, cost);
    }

    public static bool HasUsableContext(JsonElement message, PiTokenUsage usage)
    {
        var stopReason = message.TryGetProperty("stopReason", out var stopReasonProperty) &&
            stopReasonProperty.ValueKind == JsonValueKind.String
                ? stopReasonProperty.GetString()
                : null;
        return stopReason is not ("aborted" or "error") && usage.ContextTokens > 0;
    }

    private static bool TryReadNonNegativeInt64(JsonElement element, string name, out long result)
    {
        result = 0;
        return element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out result) &&
            result >= 0;
    }
}
