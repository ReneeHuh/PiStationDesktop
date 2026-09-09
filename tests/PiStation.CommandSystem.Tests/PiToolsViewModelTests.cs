using PiStation.App.ViewModels;
using PiStation.Protocol.Models;

namespace PiStation.CommandSystem.Tests;

public sealed class PiToolsViewModelTests
{
    [Fact]
    public void EditorPresetsNormalizeNamesKeepExclusionsAndRoundTrip()
    {
        var editor = new PiToolSelectionViewModel();
        Assert.Equal(PiToolSelectionMode.PiDefault, editor.Create().Mode);
        editor.Excluded = "bash, write";
        editor.UseWindowsPreset();
        Assert.Contains("powershell", editor.Create().Allowed!);
        Assert.Contains("write", editor.Create().Excluded!);
        editor.AddPowerShell();
        Assert.Single(editor.Create().Allowed!, name => name == "powershell");
        editor.Allowed = " read,\r\n powershell, read ";
        Assert.Equal(["read", "powershell"], editor.Create().Allowed);
        var restored = new PiToolSelectionViewModel();
        restored.Apply(editor.Create());
        Assert.Equal(editor.Create().Allowed, restored.Create().Allowed);
        restored.ModeIndex = 99;
        Assert.Equal(1, restored.ModeIndex);
        restored.UseReadOnlyPreset();
        Assert.DoesNotContain("powershell", restored.Create().Allowed!);
        Assert.Equal(["bash", "write"], restored.Create().Excluded);
        restored.Allowed = "*";
        Assert.Throws<ArgumentException>(() => restored.Create());
    }

    [Fact]
    public void InventoryDistinguishesRegisteredActiveMissingAndUnknown()
    {
        var view = new PiToolInventoryViewModel();
        view.Apply(null);
        Assert.Contains("unknown", view.PowerShell);
        view.Apply(new([new("powershell", "Windows shell", "builtin", false), new("read", "Read", "builtin", true)],
            new(PiToolSelectionMode.Allowlist, ["read", "missing", "excluded"], ["excluded"])));
        Assert.Contains("1 active / 2 registered", view.Summary);
        Assert.Contains("Requested but not registered: missing.", view.Summary);
        Assert.Contains("registered but inactive", view.PowerShell);
        Assert.Equal("powershell", view.Tools[0].Name);
        view.Apply(new([new("powershell", "Windows shell", "builtin", true)]));
        Assert.Contains("PowerShell is active", view.PowerShell);
        view.Apply(new([], new(PiToolSelectionMode.None)));
        Assert.Contains("no tools", view.Summary);
        Assert.Contains("not in the reported registry", view.PowerShell);
        view.Clear();
        Assert.Empty(view.Tools);
        Assert.Contains("not been inspected", view.PowerShell);
    }

    [Fact]
    public void TruncatedInventoryDoesNotClaimRequestedToolsAreMissing()
    {
        var view = new PiToolInventoryViewModel();
        view.Apply(new([], new(PiToolSelectionMode.Allowlist, ["missing"]), Truncated: true));
        Assert.Contains("absence is not conclusive", view.Summary);
        Assert.DoesNotContain("Requested but not registered", view.Summary);
    }
}
