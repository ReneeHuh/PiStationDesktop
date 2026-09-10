using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PiStation.ClientRuntime.Themes;

public sealed record ThemeGroup(string Id, string Label);
public sealed record ThemePalette(string Id, string Name, string Appearance, Dictionary<string, string> Colors,
    Dictionary<string, Dictionary<string, string>>? Variants = null, ThemeGroup? Collection = null, bool Managed = false)
{
    public IReadOnlyDictionary<string, string>? ForAppearance(string appearance) => appearance == Appearance ? Colors :
        Variants?.GetValueOrDefault(appearance);
    public override string ToString() => Name;
}

public static partial class ThemeFiles
{
    public const int MaximumBytes = 256 * 1024;
    public static readonly string[] Roles = ("canvas chrome toolbar toolbarForeground toolbarBorder toolbarControl toolbarControlForeground toolbarControlHover " +
        "surface surfaceRaised surfaceOverlay text textMuted border input focus accent accentForeground secondary secondaryForeground muted mutedForeground placeholder secondaryLabel iconMuted " +
        "error errorForeground errorSurface warning warningForeground warningSurface update updateForeground updateSurface accentSurface accentSurfaceForeground " +
        "messageSurface messageForeground messageAction messageActionForeground messageActionHover codeBackground codeForeground sidebar sidebarForeground sidebarMutedForeground " +
        "sidebarControlSurface sidebarRowHover sidebarRowActive sidebarRowSelected sidebarBorder terminalBackground terminalForeground terminalCursor terminalSelection terminalScrollbar terminalScrollbarHover").Split(' ');
    public static readonly IReadOnlyDictionary<string, string> NativeRoles = new Dictionary<string, string>
    {
        ["PiCanvas"] = "canvas", ["PiSidebar"] = "sidebar", ["PiChrome"] = "chrome", ["PiWorkbench"] = "codeBackground",
        ["PiSurface"] = "surface", ["PiSurfaceElevated"] = "surfaceRaised", ["PiControlSurface"] = "toolbarControl",
        ["PiSurfaceHover"] = "sidebarRowHover", ["PiSurfaceSelected"] = "sidebarRowSelected", ["PiMessageUser"] = "messageSurface",
        ["PiOverlay"] = "surfaceOverlay", ["PiDivider"] = "sidebarBorder", ["PiBorder"] = "border", ["PiBorderStrong"] = "input",
        ["PiTextPrimary"] = "text", ["PiTextSecondary"] = "textMuted", ["PiTextMuted"] = "placeholder", ["PiAccent"] = "accent",
        ["PiAccentHover"] = "messageActionHover", ["PiFocus"] = "focus", ["PiWarningSurface"] = "warningSurface",
        ["PiCriticalSurface"] = "errorSurface", ["PiSuccessSurface"] = "updateSurface", ["PiRunning"] = "accentSurfaceForeground",
        ["PiCompleted"] = "update", ["PiWaiting"] = "warning", ["PiCritical"] = "error", ["PiOffline"] = "iconMuted",
        ["PiSidebarForeground"] = "sidebarForeground", ["PiMessageForeground"] = "messageForeground", ["PiCodeForeground"] = "codeForeground",
        ["PiCodeBackground"] = "codeBackground",
        ["PiTerminalBackground"] = "terminalBackground", ["PiTerminalForeground"] = "terminalForeground", ["PiTerminalCursor"] = "terminalCursor",
        ["PiTerminalSelection"] = "terminalSelection",
    };
    private static readonly HashSet<string> Reserved = new("system light dark t3-chat grove ocean ember iris t3-chat-dark t3-grove t3-ocean t3-ember t3-iris".Split(' '));
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly JsonSerializerOptions ExportOptions = new(JsonOptions) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private static readonly JsonDocument DefaultsDocument = JsonDocument.Parse(typeof(ThemeFiles).Assembly.GetManifestResourceStream("PiStation.ThemeDefaults.json")!);
    public static Dictionary<string, string> Defaults(string appearance, bool t3 = false) => DefaultsDocument.RootElement
        .GetProperty(t3 ? "t3" : "native").GetProperty(appearance).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
    public static ThemePalette New(string appearance) => new("custom-" + Guid.NewGuid().ToString("N")[..8], "Custom theme", appearance, Defaults(appearance));
    public static string ThemeId(string name)
    {
        var id = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (id.Length > 48) id = id[..48].TrimEnd('-');
        return id.Length == 0 || Reserved.Contains(id) ? "custom-" + Guid.NewGuid().ToString("N")[..8] : id;
    }
    public static JsonDocument ReadJson(string json, bool comments = false)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new FormatException("Theme files must be 256 KB or smaller.");
        return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 20, AllowTrailingCommas = comments, CommentHandling = comments ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow });
    }
    public static ThemePalette Import(string json, string format = "auto")
    {
        using var document = ReadJson(json, comments: format != "t3");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("A theme must contain a JSON object.");
        if (format == "pi") return ParsePi(root);
        if (format == "vscode" || (format == "auto" && !root.TryGetProperty("version", out _) && root.TryGetProperty("colors", out var colors) && colors.ValueKind == JsonValueKind.Object && colors.EnumerateObject().Any(p => p.Name.Contains('.')))) return ParseVsCode(root);
        return ParseT3(root);
    }
    public static ThemePalette ParseT3(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != 1) throw new FormatException("Expected a T3 theme file with version 1.");
        var name = RequiredString(root, "name").Trim();
        if (name.Length is < 1 or > 48) throw new FormatException("Theme names must contain 1–48 characters.");
        var appearance = RequiredString(root, "appearance");
        if (appearance is not ("dark" or "light")) throw new FormatException("Theme appearance must be dark or light.");
        var id = root.TryGetProperty("id", out var idValue) ? idValue.GetString() ?? "" : ThemeId(name);
        if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{0,47}$") || Reserved.Contains(id)) throw new FormatException("Theme id is invalid or reserved.");
        var colors = ParseColors(root.GetProperty("colors"), appearance);
        Dictionary<string, Dictionary<string, string>>? variants = null;
        if (root.TryGetProperty("variants", out var variantObject))
        {
            if (variantObject.ValueKind != JsonValueKind.Object) throw new FormatException("Theme variants must be an object.");
            variants = [];
            foreach (var variant in variantObject.EnumerateObject())
            {
                if (variant.Name is not ("dark" or "light") || variant.Name == appearance || variants.ContainsKey(variant.Name)) throw new FormatException("Variants must name the opposite appearance exactly once.");
                variants.Add(variant.Name, ParseColors(variant.Value, variant.Name));
            }
        }
        ThemeGroup? collection = null;
        if (root.TryGetProperty("collection", out var collectionValue))
        {
            var collectionId = RequiredString(collectionValue, "id"); var label = RequiredString(collectionValue, "label").Trim();
            if (!Regex.IsMatch(collectionId, "^[a-zA-Z0-9][a-zA-Z0-9.:-]{0,127}$") || label.Length is < 1 or > 48) throw new FormatException("Theme collection is invalid.");
            collection = new(collectionId, label);
        }
        return new(id, name, appearance, colors, variants, collection, root.TryGetProperty("managed", out var managed) && managed.ValueKind == JsonValueKind.True);
    }
    private static Dictionary<string, string> ParseColors(JsonElement value, string appearance)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException("Theme colors must be an object.");
        var colors = Defaults(appearance, t3: true); var seen = new HashSet<string>();
        foreach (var property in value.EnumerateObject())
        {
            if (!Roles.Contains(property.Name) || !seen.Add(property.Name)) throw new FormatException($"Unknown or repeated theme color: {property.Name}.");
            if (property.Value.ValueKind != JsonValueKind.String || !ThemeColor.TryParse(property.Value.GetString(), out _)) throw new FormatException($"Invalid color for {property.Name}.");
            // Preserve valid original literals and all T3 roles for lossless theme interchange.
            colors[property.Name] = property.Value.GetString()!.Trim();
        }
        if (seen.Count == 0) throw new FormatException("A palette must contain at least one color.");
        return colors;
    }
    private static string RequiredString(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new FormatException($"Theme requires {key}.");
    public static string Export(ThemePalette theme) => JsonSerializer.Serialize(new { version = 1, theme.Id, theme.Name, theme.Appearance, theme.Colors, theme.Variants, theme.Collection, theme.Managed },
        ExportOptions) + "\n";
    public static ThemePalette Copy(ThemePalette theme) => Import(Export(theme), "t3");
}
