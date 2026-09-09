using System.Text.Json;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Protocol.Tests;

public sealed class PiToolSelectionTests
{
    [Fact]
    public void DefaultsDoNotOverridePiAndEmptyAllowlistReallyDisablesTools()
    {
        Assert.Empty(PiToolSelectionRules.LaunchArguments(null));
        Assert.Empty(PiToolSelectionRules.LaunchArguments(new()));
        Assert.Equal(["--no-tools"], PiToolSelectionRules.LaunchArguments(new(PiToolSelectionMode.None)));
        Assert.Equal(["--no-tools"], PiToolSelectionRules.LaunchArguments(new(PiToolSelectionMode.Allowlist, [])));
        Assert.False(PiToolSelectionRules.IsManaged(new(Allowed: ["powershell"])));
    }

    [Fact]
    public void AllowlistAndExclusionsIncludeEquivalentPlanningReaders()
    {
        var selection = new PiToolSelection(PiToolSelectionMode.Allowlist,
            ["read", "read", "grep", "powershell", "extension_tool"], ["grep", "powershell"]);
        Assert.Equal(["--tools", "read,pistation_plan_read,grep,pistation_plan_grep,powershell,extension_tool",
            "--exclude-tools", "grep,pistation_plan_grep,powershell"], PiToolSelectionRules.LaunchArguments(selection));
        Assert.Equal(["--exclude-tools", "read,pistation_plan_read"],
            PiToolSelectionRules.LaunchArguments(new(Excluded: ["read"])));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" read")]
    [InlineData("read,write")]
    [InlineData("*")]
    [InlineData("shell\nwrite")]
    [InlineData("工具")]
    public void RejectsInvalidNames(string name) => Assert.Throws<ArgumentException>(() =>
        PiToolSelectionRules.Normalize(new(PiToolSelectionMode.Allowlist, [name])));

    [Fact]
    public void RejectsInvalidModesAndOversizedLists()
    {
        Assert.Throws<ArgumentException>(() => PiToolSelectionRules.Normalize(new((PiToolSelectionMode)99)));
        Assert.Throws<ArgumentException>(() => PiToolSelectionRules.Normalize(new(Excluded: [new string('a', 129)])));
        Assert.Throws<ArgumentException>(() => PiToolSelectionRules.Normalize(new(Allowed: Enumerable.Repeat("read", 129).ToArray())));
    }

    [Theory]
    [InlineData("--tools")]
    [InlineData("-t")]
    [InlineData("--exclude-tools=read")]
    [InlineData("-xt")]
    [InlineData("--no-tools")]
    [InlineData("-nt")]
    [InlineData("--no-builtin-tools")]
    [InlineData("-nbt")]
    public void RejectsAmbiguousManagedArgumentsButPreservesLegacyDefaults(string argument)
    {
        PiToolSelectionRules.ValidateArguments(null, [argument]);
        PiToolSelectionRules.ValidateArguments(new(), [argument]);
        Assert.Throws<ArgumentException>(() => PiToolSelectionRules.ValidateArguments(new(PiToolSelectionMode.None), [argument]));
        Assert.Throws<ArgumentException>(() => PiToolSelectionRules.ValidateArguments(new(Excluded: ["bash"]), [argument]));
    }

    [Fact]
    public void ToolPolicyAndInventoryRoundTripAndOldConfigurationHasNoOverride()
    {
        var tools = new PiToolSelection(PiToolSelectionMode.Allowlist, ["read", "powershell"], ["write"]);
        var configuration = new PiRuntimeConfiguration(null, new(), new(Tools: tools));
        var json = JsonSerializer.Serialize(configuration, ProtocolJsonContext.Default.PiRuntimeConfiguration);
        var restored = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.PiRuntimeConfiguration)!;
        Assert.Equal(tools.Mode, restored.Launch!.Tools!.Mode);
        Assert.Equal(tools.Allowed, restored.Launch.Tools.Allowed);
        Assert.Equal(tools.Excluded, restored.Launch.Tools.Excluded);
        Assert.Null(JsonSerializer.Deserialize("""{"executablePath":null,"extensions":{}}""",
            ProtocolJsonContext.Default.PiRuntimeConfiguration)!.Launch?.Tools);
        var snapshot = new PiResourcesSnapshot("agent", "project", true, null, [], [], [], "", "",
            ToolInventory: new([new("powershell", "Windows shell", "builtin", true)], tools));
        var loaded = JsonSerializer.Deserialize(JsonSerializer.Serialize(snapshot, ProtocolJsonContext.Default.PiResourcesSnapshot),
            ProtocolJsonContext.Default.PiResourcesSnapshot)!;
        Assert.Equal("powershell", Assert.Single(loaded.ToolInventory!.Tools).Name);
        Assert.True(loaded.ToolInventory.Tools[0].Active);
    }
}
