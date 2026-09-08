using PiStation.Host;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class ServerRuntimeOptionsTests
{
    [Fact]
    public async Task HeadlessHostKeepsSavedRuntimeOptionsAndShipsItsPiFeatures()
    {
        using var directory = new ClientTestDirectory();
        var defaults = directory.CreateHostOptions();
        var saved = new PiRuntimeConfiguration(defaults.PiInstallation!.ExecutablePath,
            new(DiscoverInstalled: false), new(CommandTimeoutSeconds: 45, ShutdownTimeoutSeconds: 8));
        await PiRuntimeSettingsStore.SaveAsync(defaults.ApplicationDataRoot, saved);
        var loaded = PiRuntimeSettingsStore.Load(defaults.ApplicationDataRoot);
        var options = PiStation.Server.Program.CreateHostOptions(defaults.ApplicationDataRoot, defaults.PiInstallation, loaded);
        Assert.Equal(saved.ExecutablePath, loaded.ExecutablePath);
        Assert.False(options.Extensions.DiscoverInstalled);
        Assert.Equal(45, options.LaunchConfiguration.CommandTimeoutSeconds);
        Assert.Equal(8, options.LaunchConfiguration.ShutdownTimeoutSeconds);
        Assert.True(File.Exists(options.PlanExtensionPath));
        Assert.True(File.Exists(options.AgentExtensionPath));
        Assert.True(File.Exists(options.ManagementExtensionPath));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(options.ManagementExtensionPath)!, "pistation-permissions.ts")));
    }
}
