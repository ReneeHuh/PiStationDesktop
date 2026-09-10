using System.Globalization;
using System.Text.Json;
using PiStation.Protocol.Models;

namespace PiStation.Host.Usage;

internal static class UsageLimitAdapters
{
    internal const int MaximumBytes = 1024 * 1024;
    internal static IReadOnlyList<UsageLimitAccount> CliProxy(byte[] json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var accounts = Object(document.RootElement, "accounts");
        var result = new List<UsageLimitAccount>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in accounts.EnumerateObject())
        {
            if (!ids.Add(item.Name) || ids.Count > 128) throw new JsonException();
            var value = item.Value;
            var provider = Text(value, "provider", 80);
            // Keep unknown providers visible without guessing their window semantics.
            var windows = new List<UsageLimitWindow>();
            if (provider is "claude" or "codex")
                foreach (var (key, id, kind, label, minutes) in new[] {
                    ("five_hour", "five_hour", "session", "Session", 300),
                    ("seven_day", "seven_day", "weekly", "Weekly", 10080),
                    ("weekly", "secondary", "weekly", "Weekly", 10080),
                    ("fable", "seven_day_fable", "weekly", "Weekly · Fable", 10080) })
                {
                    if (!value.TryGetProperty(key, out var window)) continue;
                    if (window.ValueKind != JsonValueKind.Object) throw new JsonException();
                    if (OptionalBool(window, "known") == false) continue;
                    var used = Number(window, "used_percent");
                    windows.Add(new(id, kind, label, OptionalBool(window, "hard_limited") == true ? 100 : Math.Clamp(used, 0, 100),
                        Date(window, "reset_at"), minutes));
                }
            result.Add(new(Bounded(item.Name, 256), provider, Bounded(item.Name, 256), Checked(value, "fetched_at", now),
                windows, OptionalText(value, "plan", 100), provider is "claude" or "codex" ? null : "unsupported"));
        }
        return result.OrderBy(a => a.Provider, StringComparer.Ordinal).ThenBy(a => a.Id, StringComparer.Ordinal).ToArray();
    }

    internal static UsageLimitSource PiFeed(byte[] json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (Number(root, "version") != 1) throw new JsonException();
        var checkedAt = Checked(root, "checkedAt", now, required: true);
        var accounts = root.GetProperty("accounts");
        if (accounts.ValueKind != JsonValueKind.Array || accounts.GetArrayLength() > 128) throw new JsonException();
        var result = new List<UsageLimitAccount>();
        foreach (var account in accounts.EnumerateArray())
        {
            var unavailable = OptionalText(account, "unavailable", 32);
            if (unavailable is not (null or "unsupported" or "probeFailed")) throw new JsonException();
            var windows = account.GetProperty("windows");
            if (windows.ValueKind != JsonValueKind.Array || windows.GetArrayLength() > 32) throw new JsonException();
            var normalized = new List<UsageLimitWindow>();
            foreach (var window in windows.EnumerateArray())
            {
                var kind = Text(window, "kind", 16);
                if (kind is not ("session" or "weekly" or "monthly" or "other")) throw new JsonException();
                double? duration = window.TryGetProperty("windowDurationMins", out var d) && d.ValueKind != JsonValueKind.Null ? Number(window, "windowDurationMins") : null;
                if (duration is < 0 or > 527040) throw new JsonException();
                normalized.Add(new(Text(window, "id", 128), kind, Text(window, "label", 128), Math.Clamp(Number(window, "usedPercent"), 0, 100),
                    Date(window, "resetsAt"), duration));
            }
            if (normalized.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count() != normalized.Count) throw new JsonException();
            result.Add(new(Text(account, "id", 256), Text(account, "provider", 80), Text(account, "label", 256),
                Checked(account, "checkedAt", checkedAt, required: true), unavailable == "unsupported" ? [] : normalized.OrderBy(w => KindOrder(w.Kind)).ThenBy(w => w.Id, StringComparer.Ordinal).ToArray(),
                OptionalText(account, "plan", 100), unavailable));
        }
        if (result.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != result.Count) throw new JsonException();
        return new("pi:" + Text(root, "id", 128), "piExtension", Text(root, "label", 128), checkedAt, result);
    }
    private static int KindOrder(string kind) => kind switch { "session" => 0, "weekly" => 1, "monthly" => 2, _ => 3 };
    private static JsonElement Object(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : throw new JsonException();
    private static string Text(JsonElement parent, string name, int max) => OptionalText(parent, name, max) is { Length: > 0 } text ? text : throw new JsonException();
    private static string? OptionalText(JsonElement parent, string name, int max) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ?
        value.ValueKind == JsonValueKind.String ? Bounded(value.GetString()!, max) : throw new JsonException() : null;
    private static string Bounded(string value, int max) => value.Length <= max && !value.Any(char.IsControl) ? value : throw new JsonException();
    private static double Number(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : throw new JsonException();
    private static bool? OptionalBool(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) ?
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new JsonException() : null;
    private static DateTimeOffset? Date(JsonElement parent, string name) => OptionalText(parent, name, 64) is { } text &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    private static DateTimeOffset Checked(JsonElement parent, string name, DateTimeOffset now, bool required = false)
    {
        var date = Date(parent, name);
        if (required && date is null || date > now.AddMinutes(5)) throw new JsonException();
        return date ?? now;
    }
    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[16384];
        int length;
        while ((length = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + length > MaximumBytes) throw new JsonException();
            bytes.Write(buffer, 0, length);
        }
        return bytes.ToArray();
    }
}
