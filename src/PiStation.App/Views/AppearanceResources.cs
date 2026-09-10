using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using PiStation.App.ViewModels;
using Windows.UI;
using PiStation.ClientRuntime.Themes;

namespace PiStation.App.Views;

internal static class AppearanceResources
{
    internal const string SettingsKey = "PiAppearanceSettings";
    public static ShellLayoutViewModel? Settings(FrameworkElement owner) =>
        owner.XamlRoot?.Content is FrameworkElement root && root.Resources.TryGetValue(SettingsKey, out var value)
            ? value as ShellLayoutViewModel : null;

    public static void Apply(FrameworkElement root, ShellLayoutViewModel settings, double textScale, bool effectsEnabled)
    {
        root.Resources[SettingsKey] = settings;
        foreach (var name in new[] { "Dark", "Light", "HighContrast" })
        {
            var dictionary = new ResourceDictionary();
            dictionary["PiSansFontFamily"] = new FontFamily(AppearancePreferences.FontStack(settings.InterfaceFontFamily, "Segoe UI Variable Text, Segoe UI"));
            // Default WinUI control templates set their own font instead of inheriting the page font.
            dictionary["ContentControlThemeFontFamily"] = string.IsNullOrEmpty(settings.InterfaceFontFamily)
                ? FontFamily.XamlAutoFontFamily : dictionary["PiSansFontFamily"];
            dictionary["ControlContentThemeFontSize"] = 14 * settings.InterfaceFontSize / 13 * textScale;
            dictionary["PiMonospaceFontFamily"] = new FontFamily(settings.CodeFontStack);
            dictionary["PiComposerFontFamily"] = new FontFamily(settings.ComposerFontStack);
            dictionary["PiComposerFontSize"] = settings.ComposerFontSize * textScale;
            dictionary["PiCodeFontSize"] = settings.CodeFontSize * textScale;
            foreach (var (key, size) in new[] { ("PiFontSizeCaption", 11d), ("PiFontSizeBody", 13d),
                ("PiFontSizeBodyLarge", 14d), ("PiFontSizeHeading", 16d), ("PiFontSizeTitle", 20d) })
                dictionary[key] = size * settings.InterfaceFontSize / 13 * textScale;

            if (name != "HighContrast")
            {
                // Use the original palette each time, so adjustments never accumulate rounding error.
                var tokens = Application.Current.Resources.MergedDictionaries.Last();
                var palette = (ResourceDictionary)tokens.ThemeDictionaries[name];
                var custom = settings.Themes.EffectivePalette?.ForAppearance(name.ToLowerInvariant());
                var defaults = ThemeFiles.Defaults(name.ToLowerInvariant());
                Color Resolve(string key, string role) => custom is not null && custom.TryGetValue(role, out var literal)
                    ? NativeColor(ThemeColor.Parse(literal)) : palette.TryGetValue(key + "Color", out var original) ? (Color)original : NativeColor(ThemeColor.Parse(defaults[role]));
                var canvas = Resolve("PiCanvas", "canvas");
                var target = name == "Dark" ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
                foreach (var (key, role) in ThemeFiles.NativeRoles)
                {
                    var color = Resolve(key, role);
                    var text = key.StartsWith("PiText", StringComparison.Ordinal) || key is "PiSidebarForeground" or "PiMessageForeground" or "PiCodeForeground" or "PiTerminalForeground";
                    if (settings.AppearanceContrast != 100 && (text || key is "PiDivider" or "PiBorder" or "PiBorderStrong"))
                    {
                        var background = key switch { "PiSidebarForeground" => Resolve("PiSidebar", "sidebar"), "PiMessageForeground" => Resolve("PiMessageUser", "messageSurface"),
                            "PiCodeForeground" => Resolve("PiCodeBackground", "codeBackground"), "PiTerminalForeground" => Resolve("PiTerminalBackground", "terminalBackground"), _ => canvas };
                        var border = !text;
                        color = settings.AppearanceContrast <= 100
                            ? Mix(background, color, settings.AppearanceContrast / 100)
                            : Mix(color, target, (settings.AppearanceContrast - 100) / (border ? 400 : 100));
                    }
                    else if (key is "PiCanvas" or "PiSidebar" or "PiChrome" or "PiWorkbench")
                        color.A = effectsEnabled ? (byte)Math.Round(color.A * settings.GlassOpacity / 100) : (byte)255;
                    dictionary[key + "Color"] = color;
                    dictionary[key + "Brush"] = new SolidColorBrush(color);
                }
            }
            if (name == "HighContrast")
            {
                var native = (ResourceDictionary)Application.Current.Resources.MergedDictionaries.Last().ThemeDictionaries[name];
                foreach (var (key, fallback) in new Dictionary<string, string> { ["PiSidebarForeground"] = "PiTextPrimary", ["PiMessageForeground"] = "PiTextPrimary", ["PiCodeForeground"] = "PiTextPrimary", ["PiCodeBackground"] = "PiControlSurface",
                    ["PiTerminalBackground"] = "PiCanvas", ["PiTerminalForeground"] = "PiTextPrimary", ["PiTerminalCursor"] = "PiFocus", ["PiTerminalSelection"] = "PiSurfaceSelected" })
                    dictionary[key + "Brush"] = native[fallback + "Brush"];
            }
            root.Resources.ThemeDictionaries[name] = dictionary;
        }
        // ThemeResource values are reevaluated by WinUI's theme walk. Both changes happen
        // within the same UI turn; no intermediate frame is presented and no controls are recreated.
        var requested = root.RequestedTheme;
        root.RequestedTheme = root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        root.RequestedTheme = requested;
    }

    private static Color NativeColor(ThemeColor color) => Color.FromArgb((byte)Math.Round(color.A * 255),
        (byte)Math.Round(color.R * 255), (byte)Math.Round(color.G * 255), (byte)Math.Round(color.B * 255));

    private static Color Mix(Color from, Color to, double amount) => Color.FromArgb(255,
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));
}
