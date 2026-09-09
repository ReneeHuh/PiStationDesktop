using System.Text.Json;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

/// <summary>Validated, bounded input shared by the desktop browser dispatcher and tests.</summary>
public sealed record BrowserAutomationCommand(string Operation, string? TabId, string? Selector,
    string? Value, string? Url, string? Key, int Modifiers, int DeltaX, int DeltaY,
    string Condition, int TimeoutMs, bool Open = true, bool ReuseExistingTab = true,
    BrowserViewportSetting? Viewport = null, string? ColorScheme = null)
{
    public bool RequiresInteraction => BrowserAutomationLimits.RequiresInteraction(Operation);

    public static BrowserAutomationCommand Parse(string operation, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Browser input must be an object.");
        if (!BrowserAutomationLimits.IsOperation(operation))
            throw new ArgumentException("Unknown browser operation.");
        string? Read(string name, int limit, bool required = false, bool allowEmpty = false)
        {
            if (!input.TryGetProperty(name, out var element))
            {
                if (required) throw new ArgumentException($"Browser operation requires '{name}'.");
                return null;
            }
            if (element.ValueKind != JsonValueKind.String) throw new ArgumentException($"Invalid '{name}'.");
            var value = element.GetString()!;
            if (value.Length > limit || (!allowEmpty && string.IsNullOrWhiteSpace(value))) throw new ArgumentException($"Invalid '{name}'.");
            return value;
        }
        int Number(string name, int fallback, int min, int max)
        {
            if (!input.TryGetProperty(name, out var element)) return fallback;
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value) || value < min || value > max)
                throw new ArgumentException($"Invalid '{name}' (expected {min}..{max}).");
            return value;
        }
        bool Boolean(string name, bool fallback)
        {
            if (!input.TryGetProperty(name, out var element)) return fallback;
            if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException($"Invalid '{name}'.");
            return element.GetBoolean();
        }
        var tabId = Read("tabId", 160);
        var reuse = Boolean("reuseExistingTab", true);
        if (operation == "open" && tabId is not null && !reuse)
            throw new ArgumentException("tabId cannot be combined with reuseExistingTab=false.");
        var appearance = Read("colorScheme", 16, operation == "set_appearance");
        if (appearance is not null && appearance is not ("system" or "light" or "dark"))
            throw new ArgumentException("colorScheme must be system, light or dark.");
        var condition = Read("condition", 32) ?? "visible";
        if (operation == "wait" && condition is not ("visible" or "hidden" or "text" or "url" or "loaded"))
            throw new ArgumentException("Unknown wait condition.");
        var selector = Read("selector", 1024, operation is "click" or "type" || operation == "wait" && condition is "visible" or "hidden" or "text");
        var key = Read("key", 32, operation == "press_key");
        if (key is not null) _ = VirtualKey(key);
        return new(operation, tabId, selector,
            Read("value", 8192, operation == "type" || operation == "wait" && condition is "text" or "url", operation == "type"),
            Read("url", 2048, operation == "navigate"), key, Number("modifiers", 0, 0, 15),
            Number("deltaX", 0, -10000, 10000), Number("deltaY", 0, -10000, 10000),
            condition, Number("timeoutMs", 5000, 100, 20000), Boolean("open", true), reuse,
            operation == "resize" ? BrowserViewportSetting.Parse(input) : null, appearance);
    }

    public static int VirtualKey(string key) => key switch
    {
        "Enter" => 13, "Tab" => 9, "Escape" => 27, "Backspace" => 8, "Delete" => 46,
        "ArrowLeft" => 37, "ArrowUp" => 38, "ArrowRight" => 39, "ArrowDown" => 40,
        "Home" => 36, "End" => 35, "PageUp" => 33, "PageDown" => 34, "Space" => 32,
        _ when key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]) => char.ToUpperInvariant(key[0]),
        _ => throw new ArgumentException("Unsupported browser key. Use a letter/digit, Enter, Tab, Escape, Backspace, Delete, arrows, Home, End, PageUp, PageDown or Space."),
    };
}
