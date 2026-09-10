using PiStation.App.ViewModels;

namespace PiStation.CommandSystem.Tests;

public sealed class AppearancePreferencesTests
{
    [Fact]
    public void PersistedPreferencesRemainIndependentAndResetKeepsTerminalAndLayout()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pistation-appearance-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "layout.json");
        try
        {
            var settings = new ShellLayoutViewModel(path) { TerminalFontFamily = "Consolas", TerminalFontSize = 18, IsSidebarCollapsed = true };
            settings.Appearance = new("Arial", 18, "Georgia", 16, "Consolas", 11, 150, 60, false, 1, false, 250);
            var restored = new ShellLayoutViewModel(path);
            Assert.Equal(settings.Appearance, restored.Appearance);
            Assert.Equal("Consolas", restored.TerminalFontFamily);
            Assert.Equal(18, restored.TerminalFontSize);
            restored.ResetAppearance();
            var reset = new ShellLayoutViewModel(path);
            Assert.Equal(new AppearancePreferences(), reset.Appearance);
            Assert.True(reset.IsSidebarCollapsed);
            Assert.Equal(18, reset.TerminalFontSize);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void MissingAppearanceMigratesWithoutChangingTerminal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pistation-appearance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "layout.json");
            File.WriteAllText(path, """{"TerminalFontFamily":"Consolas","TerminalFontSize":16}""");
            var settings = new ShellLayoutViewModel(path);
            Assert.Equal(new AppearancePreferences(), settings.Appearance);
            Assert.Equal(16, settings.TerminalFontSize);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void InvalidValuesAreBoundedAndFamiliesCannotLoadFontUris()
    {
        var preferences = new AppearancePreferences(InterfaceFontFamily: " Arial , Arial, ms-appx:///evil.ttf#Remote ",
            InterfaceFontSize: double.NaN, ComposerFontSize: 500, CodeFontSize: -20, Contrast: 900,
            GlassOpacity: -2, DiffLayout: 99, PanelAnimationDurationMs: double.PositiveInfinity).Normalize();
        Assert.Equal(13, preferences.InterfaceFontSize);
        Assert.Equal(20, preferences.ComposerFontSize);
        Assert.Equal(10, preferences.CodeFontSize);
        Assert.Equal(200, preferences.Contrast);
        Assert.Equal(40, preferences.GlassOpacity);
        Assert.Equal(0, preferences.DiffLayout);
        Assert.Equal(0, preferences.PanelAnimationDurationMs);
        Assert.DoesNotContain(":", preferences.InterfaceFontFamily);
        Assert.DoesNotContain("/", preferences.InterfaceFontFamily);
        Assert.Equal("Arial, ms-appxevil.ttfRemote", preferences.InterfaceFontFamily);
    }

    [Theory]
    [InlineData(true, true, 275)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    public void MotionRespectsSystemAndStartup(bool enabled, bool loaded, double expected) =>
        Assert.Equal(expected, new AppearancePreferences(PanelAnimationDurationMs: 275).EffectiveAnimationDuration(enabled, loaded));
}
