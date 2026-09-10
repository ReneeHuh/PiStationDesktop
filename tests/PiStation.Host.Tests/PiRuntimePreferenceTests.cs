using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class PiRuntimePreferenceTests
{
    [Fact]
    public async Task PreferencesPersistAndEncodePresenceFlagsWithoutChangingUnmanagedValues()
    {
        using var directory = new HostTestDirectory();
        var launch = new PiLaunchConfiguration(EnvironmentVariables: new Dictionary<string, string?> { ["UNRELATED"] = "keep" },
            Preferences: new(true, false, false, false));
        await PiRuntimeSettingsStore.SaveAsync(directory.Path, new(null, new(), launch));
        var saved = PiRuntimeSettingsStore.Load(directory.Path);
        Assert.Equal(launch.Preferences, saved.Launch!.Preferences);
        var environment = PiRuntimePreferenceRules.Environment(saved.Launch);
        Assert.Equal("long", environment["PI_CACHE_RETENTION"]);
        Assert.Equal("0", environment["PI_TELEMETRY"]);
        Assert.Null(environment["PI_OFFLINE"]);
        Assert.Null(environment["PI_SKIP_VERSION_CHECK"]);
        Assert.Equal("keep", environment["UNRELATED"]);
        Assert.Empty(PiRuntimePreferenceRules.Environment(new()));
        Assert.NotEqual(PiRuntimeSettingsStore.Revision(saved), PiRuntimeSettingsStore.Revision(saved with { Launch = new() }));
    }

    [Fact]
    public void DedicatedControlsRejectAmbiguousLegacyAndOfflineVersionConflicts()
    {
        Assert.Throws<ArgumentException>(() => PiRuntimeSettingsStore.ValidateLaunch(new(["--offline"], Preferences: new(Offline: false))));
        Assert.Throws<ArgumentException>(() => PiRuntimeSettingsStore.ValidateLaunch(new(EnvironmentVariables: new Dictionary<string, string?> { ["pi_telemetry"] = "1" }, Preferences: new(Telemetry: false))));
        Assert.Throws<ArgumentException>(() => PiRuntimeSettingsStore.ValidateLaunch(new(Preferences: new(Offline: true, SkipVersionCheck: false))));
        Assert.Equal("custom", PiRuntimePreferenceRules.Environment(new(EnvironmentVariables: new Dictionary<string, string?> { ["PI_TELEMETRY"] = "custom" }))["PI_TELEMETRY"]);
    }
}
