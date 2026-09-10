using PiStation.ClientRuntime.Themes;
using System.Text.Json;

namespace PiStation.ClientRuntime.Tests;

public sealed class ThemePaletteTests
{
    [Theory]
    [InlineData("#f008", "#ff000088")]
    [InlineData("rgb(100% 0% 0% / 50%)", "#ff000080")]
    [InlineData("hsl(120deg 100% 50%)", "#00ff00")]
    [InlineData("oklch(0 0 0)", "#000000")]
    [InlineData("oklab(1 0 0)", "#ffffff")]
    [InlineData("color(display-p3 1 0 0)", "#ff0000")]
    [InlineData("slategrey", "#708090")]
    public void CssColorsConvertToNativeSrgb(string input, string expected) => Assert.Equal(expected, ThemeColor.Parse(input).Hex);

    [Theory]
    [InlineData("var(--accent)")]
    [InlineData("currentColor")]
    [InlineData("url(https://example.invalid/color)")]
    [InlineData("rgb(NaN 0 0)")]
    [InlineData("rgb(1,,2,3)")]
    [InlineData("rgb(1/2/3)")]
    [InlineData("oklch(0.5,0.2,240)")]
    public void DynamicAndInvalidColorsAreRejected(string input) => Assert.False(ThemeColor.TryParse(input, out _));

    [Fact]
    public void T3DefaultsAndVariantsRoundTripWithoutLosingRolesOrLiterals()
    {
        foreach (var mode in new[] { "dark", "light" })
        {
            var colors = ThemeFiles.Defaults(mode, true);
            Assert.Equal(57, colors.Count);
            Assert.All(colors.Values, literal => Assert.True(ThemeColor.TryParse(literal, out _), literal));
        }
        var theme = new ThemePalette("sample", "Sample", "dark", ThemeFiles.Defaults("dark", true),
            new() { ["light"] = ThemeFiles.Defaults("light", true) }, new("sample.family", "Sample family"), true);
        var restored = ThemeFiles.Import(ThemeFiles.Export(theme));
        Assert.Equal(theme.Colors.OrderBy(pair => pair.Key), restored.Colors.OrderBy(pair => pair.Key));
        Assert.Equal(theme.Variants!["light"].OrderBy(pair => pair.Key), restored.Variants!["light"].OrderBy(pair => pair.Key));
        Assert.Equal(theme.Collection, restored.Collection); Assert.True(restored.Managed);
    }

    [Theory]
    [InlineData("{\"version\":2,\"name\":\"Future\",\"appearance\":\"dark\",\"colors\":{\"canvas\":\"#123456\"}}")]
    [InlineData("{\"version\":1,\"id\":\"dark\",\"name\":\"Reserved\",\"appearance\":\"dark\",\"colors\":{\"canvas\":\"#123456\"}}")]
    [InlineData("{\"version\":1,\"name\":\"Unknown\",\"appearance\":\"dark\",\"colors\":{\"badRole\":\"#123456\"}}")]
    [InlineData("{\"version\":1,\"name\":\"Repeat\",\"appearance\":\"dark\",\"colors\":{\"canvas\":\"#123456\"},\"variants\":{\"dark\":{\"text\":\"white\"}}}")]
    public void InvalidT3ThemesFailBeforeInstalling(string json) => Assert.Throws<FormatException>(() => ThemeFiles.Import(json));

    [Fact]
    public void VsCodeJsoncFlattensAlphaAndRepairsUnreadableSidebarText()
    {
        var theme = ThemeFiles.Import("""
            { // standalone VS Code theme
              "name":"Slate", "type":"dark", "colors": {
                "editor.background":"#101010", "sideBar.background":"#fff",
                "sideBar.foreground":"#eee", "list.hoverBackground":"#0008",
                "terminal.background":"#202020", "terminal.selectionBackground":"#ffffff80",
              }, "tokenColors":[]
            }
            """);
        Assert.Equal("#101010", theme.Colors["canvas"]);
        Assert.Equal("#777777", theme.Colors["sidebarRowHover"]);
        Assert.True(ThemeColor.Parse(theme.Colors["sidebarForeground"]).Contrast(ThemeColor.Parse(theme.Colors["sidebar"])) >= 4.5);
        Assert.Equal("#909090", theme.Colors["terminalSelection"]);
        Assert.Equal("Slate", ThemeFiles.Import(ThemeFiles.Export(theme)).Name);
    }

    [Fact]
    public void VsCodeExternalIncludesAndOversizedFilesAreRejected()
    {
        Assert.Throws<FormatException>(() => ThemeFiles.Import("""{"include":"../../elsewhere.json","colors":{"editor.background":"#000"}}"""));
        Assert.Throws<FormatException>(() => ThemeFiles.Import(new string(' ', ThemeFiles.MaximumBytes + 1)));
    }

    [Fact]
    public void PiVariablesAnsiIndicesAndTerminalDefaultsConvertWithoutChangingRuntimeSettings()
    {
        var theme = ThemeFiles.Import("""{"name":"Pi blue","vars":{"brand":33,"alias":"brand"},"colors":{"accent":"alias","text":"","dim":244,"userMessageBg":"#101010"}}""", "pi");
        Assert.Equal("#0087ff", theme.Colors["accent"]); Assert.Equal("#808080", theme.Colors["placeholder"]);
        Assert.Equal(ThemeFiles.Defaults("dark")["text"], theme.Colors["text"]);
        Assert.Throws<FormatException>(() => ThemeFiles.Import("""{"name":"Cycle","vars":{"a":"b","b":"a"},"colors":{"accent":"a"}}""", "pi"));
    }

    [Fact]
    public void LibraryDisambiguatesImportsAndRecoversLastGoodStateWithoutOverwritingCorruptionOnLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pistation-theme-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "themes.json");
        try
        {
            var library = new ThemeLibrary(path); var first = library.Install(ThemeFiles.New("dark"));
            var second = library.Install(first); Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(2, new ThemeLibrary(path).Themes.Count);
            File.WriteAllText(path, "broken"); var damaged = new ThemeLibrary(path);
            Assert.NotNull(damaged.Error); Assert.Equal("broken", File.ReadAllText(path));
            Assert.Throws<InvalidOperationException>(() => damaged.Select(null));
            damaged.RecoverBackup(); Assert.Single(damaged.Themes); Assert.Null(damaged.Error);
            Assert.Equal(first.Id, new ThemeLibrary(path).ActiveId);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void StaleWindowsCannotOverwriteNewThemesAndReloadAllowsSaving()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pistation-theme-windows-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "themes.json");
        try
        {
            var first = new ThemeLibrary(path); var second = new ThemeLibrary(path);
            first.Install(ThemeFiles.New("dark"));
            Assert.Throws<InvalidOperationException>(() => second.Install(ThemeFiles.New("light")));
            Assert.Single(new ThemeLibrary(path).Themes); Assert.Empty(second.Themes);
            second.Reload(); second.Install(ThemeFiles.New("light")); Assert.Equal(2, new ThemeLibrary(path).Themes.Count);
            Assert.Throws<InvalidOperationException>(() => first.Select(null));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void DamagedLibraryWithoutBackupCanBeResetWhilePreservingOriginal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pistation-theme-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "themes.json");
        try
        {
            File.WriteAllText(path, "broken"); var library = new ThemeLibrary(path);
            library.ResetDamagedLibrary(); Assert.Null(library.Error); Assert.Empty(new ThemeLibrary(path).Themes);
            Assert.Equal("broken", File.ReadAllText(Directory.GetFiles(directory, "*.damaged-*").Single()));
        }
        finally { Directory.Delete(directory, true); }
    }
}
