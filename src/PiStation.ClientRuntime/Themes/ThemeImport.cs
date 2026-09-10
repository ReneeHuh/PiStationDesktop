using System.Text.Json;

namespace PiStation.ClientRuntime.Themes;

public static partial class ThemeFiles
{
    private static readonly string[] VsCodeNameKeys = ["name", "label", "$id"];
    private static readonly string[] AnsiColors = ["#000000", "#800000", "#008000", "#808000", "#000080", "#800080", "#008080", "#c0c0c0", "#808080", "#ff0000", "#00ff00", "#ffff00", "#0000ff", "#ff00ff", "#00ffff", "#ffffff"];
    private static ThemePalette ParseVsCode(JsonElement root)
    {
        if (root.TryGetProperty("include", out _)) throw new FormatException("This VS Code theme includes another file. Import a standalone theme with its inherited colors merged first.");
        if (!root.TryGetProperty("colors", out var source) || source.ValueKind != JsonValueKind.Object) throw new FormatException("VS Code theme requires workbench colors.");
        ThemeColor? Pick(params string[] keys)
        {
            foreach (var key in keys) if (source.TryGetProperty(key, out var entry) && entry.ValueKind == JsonValueKind.String && ThemeColor.TryParse(entry.GetString(), out var parsed)) return parsed;
            return null;
        }
        var background = Pick("editor.background", "editorPane.background") ?? throw new FormatException("VS Code theme requires editor.background.");
        var canvas = background with { A = 1 };
        var type = root.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String ? typeValue.GetString() : null;
        var mode = type is "light" or "hcLight" ? "light" : type is "dark" or "hc" or "hcBlack" ? "dark" : canvas.Luminance < .179 ? "dark" : "light";
        var colors = Defaults(mode);
        var white = new ThemeColor(1, 1, 1); var black = new ThemeColor(0, 0, 0);
        var foreground = canvas.Contrast(white) > canvas.Contrast(black) ? white : black;
        // Derive missing chrome from the imported canvas, never from an unrelated accent hue.
        foreach (var role in Roles)
        {
            if (role.Contains("Foreground", StringComparison.Ordinal) || role is "text" or "textMuted" or "placeholder" or "secondaryLabel" or "iconMuted") colors[role] = foreground.Hex;
            else if (role.Contains("Border", StringComparison.Ordinal) || role is "border" or "input") colors[role] = canvas.Mix(foreground, .2).Hex;
            else if (role.Contains("Hover", StringComparison.Ordinal) || role.Contains("Selected", StringComparison.Ordinal) || role.Contains("Active", StringComparison.Ordinal)) colors[role] = canvas.Mix(foreground, .15).Hex;
            else if (role.Contains("Surface", StringComparison.Ordinal) || role.Contains("Background", StringComparison.Ordinal) || role is "canvas" or "chrome" or "toolbar" or "surface" or "surfaceRaised" or "surfaceOverlay" or "sidebar" or "secondary" or "muted") colors[role] = canvas.Mix(foreground, role == "canvas" ? 0 : .04).Hex;
        }
        string Solid(ThemeColor over, string fallback, params string[] keys) => Pick(keys)?.Over(over).Hex ?? fallback;
        string Readable(string surface, string fallback, params string[] keys)
        {
            var bg = ThemeColor.Parse(surface); var specified = Pick(keys)?.Over(bg);
            if (specified is { } fg && fg.Contrast(bg) >= 4.5) return fg.Hex;
            if (ThemeColor.Parse(fallback).Contrast(bg) >= 4.5) return fallback;
            return bg.Contrast(white) > bg.Contrast(black) ? white.Hex : black.Hex;
        }
        var mappings = new Dictionary<string, string[]>
        {
            ["surface"] = ["editorWidget.background"], ["surfaceRaised"] = ["editorWidget.background", "dropdown.background"],
            ["surfaceOverlay"] = ["menu.background", "quickInput.background", "dropdown.background"], ["border"] = ["panel.border", "editorGroup.border", "contrastBorder"],
            ["input"] = ["input.border", "dropdown.border"], ["accentSurface"] = ["list.activeSelectionBackground", "list.hoverBackground"],
            ["codeBackground"] = ["textCodeBlock.background"], ["sidebar"] = ["sideBar.background", "activityBar.background"],
            ["terminalBackground"] = ["terminal.background", "panel.background"],
        };
        colors["canvas"] = canvas.Hex;
        foreach (var (role, keys) in mappings) colors[role] = Solid(canvas, colors[role], keys);
        foreach (var (role, keys) in new Dictionary<string, string[]> {
            ["text"] = ["editor.foreground", "foreground"], ["textMuted"] = ["descriptionForeground", "disabledForeground"],
            ["placeholder"] = ["input.placeholderForeground"], ["error"] = ["editorError.foreground", "errorForeground"], ["warning"] = ["editorWarning.foreground"] })
            colors[role] = Readable(canvas.Hex, colors[role], keys);
        var sidebar = ThemeColor.Parse(colors["sidebar"]); var terminal = ThemeColor.Parse(colors["terminalBackground"]);
        colors["sidebarForeground"] = Readable(sidebar.Hex, colors["sidebarForeground"], "sideBar.foreground");
        foreach (var (role, keys) in new Dictionary<string, string[]> { ["sidebarBorder"] = ["sideBar.border"], ["sidebarRowHover"] = ["list.hoverBackground"],
            ["sidebarRowActive"] = ["list.inactiveSelectionBackground", "list.hoverBackground"], ["sidebarRowSelected"] = ["list.activeSelectionBackground"] })
            colors[role] = Solid(sidebar, colors[role], keys);
        colors["terminalForeground"] = Readable(terminal.Hex, colors["terminalForeground"], "terminal.foreground");
        colors["terminalCursor"] = Solid(terminal, colors["terminalCursor"], "terminalCursor.foreground", "editorCursor.foreground");
        colors["terminalSelection"] = Solid(terminal, colors["terminalSelection"], "terminal.selectionBackground", "editor.selectionBackground");
        colors["terminalScrollbar"] = Solid(terminal, colors["terminalScrollbar"], "scrollbarSlider.background");
        if (Pick("focusBorder", "button.background", "textLink.foreground", "activityBarBadge.background", "progressBar.background", "badge.background") is { } accent)
        {
            colors["accent"] = colors["focus"] = accent.Over(canvas).Hex;
            colors["messageAction"] = Solid(canvas, colors["accent"], "button.background");
            colors["messageActionForeground"] = Readable(colors["messageAction"], foreground.Hex, "button.foreground");
            colors["accentForeground"] = Readable(colors["accent"], foreground.Hex, "button.foreground");
        }
        var name = VsCodeNameKeys.Select(key => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "VS Code theme";
        name = name.Trim(); if (name.Length > 48) name = name[..48];
        return new(ThemeId(name), name, mode, colors);
    }

    private static ThemePalette ParsePi(JsonElement root)
    {
        var name = RequiredString(root, "name").Trim();
        if (name.Length is < 1 or > 48) throw new FormatException("Pi theme name must contain 1–48 characters.");
        if (!root.TryGetProperty("colors", out var source) || source.ValueKind != JsonValueKind.Object) throw new FormatException("Pi theme requires colors.");
        var vars = root.TryGetProperty("vars", out var variables) && variables.ValueKind == JsonValueKind.Object ? variables : default;
        ThemeColor? Resolve(JsonElement value, HashSet<string> chain)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var index) && index is >= 0 and <= 255)
            {
                if (index < 16) return ThemeColor.Parse(AnsiColors[index]);
                if (index >= 232) { var gray = (8 + (index - 232) * 10) / 255d; return new(gray, gray, gray); }
                var cube = index - 16; double Component(int component) => (component == 0 ? 0 : 55 + component * 40) / 255d;
                return new(Component(cube / 36), Component(cube / 6 % 6), Component(cube % 6));
            }
            if (value.ValueKind != JsonValueKind.String) throw new FormatException("Pi colors require hex, a variable or a 0–255 ANSI index.");
            var text = value.GetString()!;
            if (text.Length == 0) return null; // Terminal-default color uses the native palette fallback.
            if (text.StartsWith('#') && ThemeColor.TryParse(text, out var color)) return color;
            if (!chain.Add(text) || chain.Count > 32) throw new FormatException("Pi theme variables contain a cycle.");
            if (vars.ValueKind != JsonValueKind.Object || !vars.TryGetProperty(text, out var variable)) throw new FormatException($"Unknown Pi theme variable: {text}.");
            return Resolve(variable, chain);
        }
        // Resolve every supplied token, including those without a native equivalent.
        var resolved = source.EnumerateObject().ToDictionary(p => p.Name, p => Resolve(p.Value, []));
        var mode = resolved.GetValueOrDefault("userMessageBg") is { } bg && bg.Luminance >= .179 ? "light" : "dark";
        var colors = Defaults(mode);
        foreach (var (pi, roles) in new Dictionary<string, string[]> {
            ["accent"] = ["accent", "focus"], ["border"] = ["border"], ["borderAccent"] = ["input"], ["borderMuted"] = ["sidebarBorder"],
            ["success"] = ["update"], ["error"] = ["error"], ["warning"] = ["warning"], ["muted"] = ["textMuted"], ["dim"] = ["placeholder", "iconMuted"],
            ["text"] = ["text", "sidebarForeground", "terminalForeground", "codeForeground"], ["selectedBg"] = ["sidebarRowSelected", "terminalSelection"],
            ["userMessageBg"] = ["messageSurface"], ["userMessageText"] = ["messageForeground"], ["customMessageBg"] = ["surface"],
            ["toolPendingBg"] = ["warningSurface"], ["toolSuccessBg"] = ["updateSurface"], ["toolErrorBg"] = ["errorSurface"],
            ["scrollbarTrack"] = ["terminalScrollbar"], ["scrollbarThumb"] = ["terminalScrollbarHover"], ["mdCodeBlockBorder"] = ["border"],
        }) if (resolved.GetValueOrDefault(pi) is { } value) foreach (var role in roles) colors[role] = value.Hex;
        return new(ThemeId(name), name, mode, colors);
    }
}
