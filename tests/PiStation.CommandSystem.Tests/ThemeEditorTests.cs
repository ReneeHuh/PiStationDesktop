using PiStation.App.ViewModels;

namespace PiStation.CommandSystem.Tests;

public sealed class ThemeEditorTests
{
    [Fact]
    public void DraftDoesNotPersistUntilSavedAndCancelRestoresTheSavedPalette()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pistation-theme-editor-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "layout.json");
        try
        {
            var layout = new ShellLayoutViewModel(path); var editor = layout.Themes;
            editor.New(); editor.Name = "Ocean"; editor.ColorInput = "#123456"; editor.ApplyColor();
            Assert.True(editor.IsPreviewing); Assert.Empty(new ShellLayoutViewModel(path).Themes.Themes);
            editor.Save(); Assert.False(editor.IsPreviewing);
            var restored = new ShellLayoutViewModel(path).Themes; Assert.Single(restored.Themes); Assert.Equal("#123456", restored.EffectivePalette!.Colors["canvas"]);
            editor.Edit(); editor.ColorInput = "#abcdef"; editor.ApplyColor(); editor.Cancel();
            Assert.Equal("#123456", editor.EffectivePalette!.Colors["canvas"]);
            editor.UseDefaults(); Assert.Null(editor.EffectivePalette); Assert.Single(editor.Themes);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void InvalidEditsKeepPreviewAndVariantChangesRetainTheOppositeMode()
    {
        var editor = new ShellLayoutViewModel().Themes; editor.New();
        editor.ColorInput = "#112233"; editor.ApplyColor(); editor.ColorInput = "var(--bad)"; editor.Run(editor.ApplyColor);
        Assert.Equal("#112233", editor.EffectivePalette!.Colors["canvas"]);
        editor.AppearanceIndex = 1; editor.ColorInput = "#eeeeee"; editor.ApplyColor(); editor.Save();
        Assert.Equal("#112233", editor.EffectivePalette!.Colors["canvas"]);
        Assert.Equal("#eeeeee", editor.EffectivePalette.Variants!["light"]["canvas"]);
        editor.InspectRole("sidebar"); Assert.Equal("sidebar", editor.SelectedRole); Assert.True(editor.HasDraft);
    }
    [Fact]
    public void ApplyingDualAppearanceThemePreservesSystemMode()
    {
        var layout = new ShellLayoutViewModel(); var editor = layout.Themes;
        editor.New(); editor.AppearanceIndex = 1; editor.Save();
        layout.ThemePreference = AppThemePreference.System; editor.ApplySelected();
        Assert.Equal(AppThemePreference.System, layout.ThemePreference);
        Assert.NotNull(editor.EffectivePalette!.ForAppearance("dark")); Assert.NotNull(editor.EffectivePalette.ForAppearance("light"));
    }
}
