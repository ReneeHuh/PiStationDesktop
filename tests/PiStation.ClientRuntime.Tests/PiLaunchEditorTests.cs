using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class PiLaunchEditorTests
{
    [Fact]
    public void EditingAdvancedFieldsPreservesDedicatedToolPolicy()
    {
        var selection = new PiToolSelection(PiToolSelectionMode.Allowlist, ["powershell"], ["bash"]);
        var original = new PiLaunchConfiguration(["--provider", "old"], Tools: selection);
        var edited = PiLaunchEditor.Parse("--provider\nnew", "EXAMPLE=1", 60, 5, original);
        Assert.Equal(selection, edited.Tools);
        Assert.Equal(["--provider", "new"], edited.Arguments);
        Assert.Equal("1", edited.EnvironmentVariables!["EXAMPLE"]);
    }
}
