using System.Text.Json;

namespace PiStation.ClientRuntime;

/// <summary>CSS viewport dimensions; no user-agent or touch emulation.</summary>
public sealed record BrowserViewportSetting(string Mode, int Width = 0, int Height = 0, string? Preset = null)
{
    public static BrowserViewportSetting Parse(JsonElement input)
    {
        string? Read(string name) => input.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new ArgumentException($"Invalid '{name}'.")
            : null;
        var mode = Read("mode") ?? throw new ArgumentException("resize requires mode: fill, freeform or preset.");
        var preset = Read("preset");
        var orientation = Read("orientation");
        var hasSize = input.TryGetProperty("width", out var width) | input.TryGetProperty("height", out var height);
        if (mode == "fill" && !hasSize && preset is null && orientation is null) return new("fill");
        if (mode == "freeform" && preset is null && orientation is null &&
            width.ValueKind == JsonValueKind.Number && height.ValueKind == JsonValueKind.Number &&
            width.TryGetInt32(out var w) && height.TryGetInt32(out var h) &&
            w is >= 240 and <= 3840 && h is >= 240 and <= 3840 && w * (long)h <= 3840L * 2160)
            return new(mode, w, h);
        if (mode == "preset" && !hasSize && orientation is null or "portrait" or "landscape")
        {
            var (pw, ph) = preset switch
            {
                "desktop" => (1440, 900), "tablet" => (768, 1024), "phone" => (390, 844),
                _ => throw new ArgumentException("Unknown preset. Use desktop, tablet or phone."),
            };
            if (orientation == "portrait" && pw > ph || orientation == "landscape" && pw < ph) (pw, ph) = (ph, pw);
            return new(mode, pw, ph, preset);
        }
        throw new ArgumentException("Invalid viewport. Freeform requires width/height 240..3840 and at most 8294400 pixels; preset accepts a name and optional orientation; fill accepts no dimensions.");
    }
}
