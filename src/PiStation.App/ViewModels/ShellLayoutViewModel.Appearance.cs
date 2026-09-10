using Microsoft.UI.Xaml;

namespace PiStation.App.ViewModels;

public sealed record AppearancePreferences(
    string InterfaceFontFamily = "", double InterfaceFontSize = 13,
    string ComposerFontFamily = "", double ComposerFontSize = 14,
    string CodeFontFamily = "", double CodeFontSize = 13,
    double Contrast = 100, double GlassOpacity = 100,
    bool WordWrap = true, int DiffLayout = 0, bool DiffIgnoreWhitespace = true,
    double PanelAnimationDurationMs = 0)
{
    public AppearancePreferences Normalize() => this with
    {
        InterfaceFontFamily = Family(InterfaceFontFamily), ComposerFontFamily = Family(ComposerFontFamily),
        CodeFontFamily = Family(CodeFontFamily), InterfaceFontSize = Number(InterfaceFontSize, 12, 20, 13),
        ComposerFontSize = Number(ComposerFontSize, 12, 20, 14), CodeFontSize = Number(CodeFontSize, 10, 18, 13),
        Contrast = Number(Contrast, 50, 200, 100), GlassOpacity = Number(GlassOpacity, 40, 100, 100),
        DiffLayout = DiffLayout == 1 ? 1 : 0, PanelAnimationDurationMs = Number(PanelAnimationDurationMs, 0, 400, 0),
    };

    // Accept installed family names, never XAML font file/URI sources. Retain native glyph fallback.
    private static string Family(string? value) => string.Join(", ", new string((value ?? "").Take(200).ToArray())
        .Split(',').Select(s => new string(s.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.').ToArray()).Trim())
        .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
    private static double Number(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(Math.Round(value), min, max) : fallback;
    public static string FontStack(string custom, string fallback) => string.IsNullOrWhiteSpace(custom) ? fallback : custom + ", " + fallback;
    public double EffectiveAnimationDuration(bool animationsEnabled, bool loaded) => animationsEnabled && loaded ? PanelAnimationDurationMs : 0;
}

public sealed partial class ShellLayoutViewModel
{
    private AppearancePreferences _appearance = new();
    public AppearancePreferences Appearance
    {
        get => _appearance;
        set
        {
            if (!SetProperty(ref _appearance, (value ?? new()).Normalize())) return;
            foreach (var property in new[] { nameof(InterfaceFontFamily), nameof(InterfaceFontSize), nameof(ComposerFontFamily),
                nameof(ComposerFontSize), nameof(CodeFontFamily), nameof(CodeFontSize), nameof(AppearanceContrast), nameof(GlassOpacity),
                nameof(WordWrap), nameof(DiffLayoutIndex), nameof(DiffIgnoreWhitespace), nameof(PanelAnimationDurationMs),
                nameof(ComposerFontStack), nameof(CodeFontStack), nameof(CodeTextWrapping), nameof(AppearanceSummary) }) OnPropertyChanged(property);
            Save();
        }
    }
    public string InterfaceFontFamily { get => Appearance.InterfaceFontFamily; set => Appearance = Appearance with { InterfaceFontFamily = value }; }
    public double InterfaceFontSize { get => Appearance.InterfaceFontSize; set => Appearance = Appearance with { InterfaceFontSize = value }; }
    public string ComposerFontFamily { get => Appearance.ComposerFontFamily; set => Appearance = Appearance with { ComposerFontFamily = value }; }
    public double ComposerFontSize { get => Appearance.ComposerFontSize; set => Appearance = Appearance with { ComposerFontSize = value }; }
    public string CodeFontFamily { get => Appearance.CodeFontFamily; set => Appearance = Appearance with { CodeFontFamily = value }; }
    public double CodeFontSize { get => Appearance.CodeFontSize; set => Appearance = Appearance with { CodeFontSize = value }; }
    public double AppearanceContrast { get => Appearance.Contrast; set => Appearance = Appearance with { Contrast = value }; }
    public double GlassOpacity { get => Appearance.GlassOpacity; set => Appearance = Appearance with { GlassOpacity = value }; }
    public bool WordWrap { get => Appearance.WordWrap; set => Appearance = Appearance with { WordWrap = value }; }
    public int DiffLayoutIndex { get => Appearance.DiffLayout; set => Appearance = Appearance with { DiffLayout = value }; }
    public bool DiffIgnoreWhitespace { get => Appearance.DiffIgnoreWhitespace; set => Appearance = Appearance with { DiffIgnoreWhitespace = value }; }
    public double PanelAnimationDurationMs { get => Appearance.PanelAnimationDurationMs; set => Appearance = Appearance with { PanelAnimationDurationMs = value }; }
    public string ComposerFontStack => AppearancePreferences.FontStack(ComposerFontFamily, AppearancePreferences.FontStack(InterfaceFontFamily, "Segoe UI Variable Text, Segoe UI"));
    public string CodeFontStack => AppearancePreferences.FontStack(CodeFontFamily, "Cascadia Mono, Consolas");
    public TextWrapping CodeTextWrapping => WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
    public string AppearanceSummary => $"Interface {InterfaceFontSize:0} • composer {ComposerFontSize:0} • code {CodeFontSize:0} • contrast {AppearanceContrast:0}% • opacity {GlassOpacity:0}%";
    public void ResetAppearance() => Appearance = new();
}
