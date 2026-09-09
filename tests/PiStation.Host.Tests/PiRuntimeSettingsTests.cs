using PiStation.Host;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class PiRuntimeSettingsTests
{
    [Fact]
    public async Task DedicatedToolPolicyPersistsAndValidatesBeforeLaunch()
    {
        using var directory = new HostTestDirectory();
        var tools = new PiToolSelection(PiToolSelectionMode.Allowlist, ["read", "powershell", "read"], ["write"]);
        var launch = PiRuntimeSettingsStore.ValidateLaunch(new(Tools: tools));
        await PiRuntimeSettingsStore.SaveAsync(directory.Path, new(null, new(), launch));
        var restored = PiRuntimeSettingsStore.Load(directory.Path).Launch!.Tools!;
        Assert.Equal(["read", "powershell"], restored.Allowed);
        Assert.Equal(["write"], restored.Excluded);
        Assert.Equal(PiToolSelectionMode.Allowlist, restored.Mode);
        Assert.Throws<ArgumentException>(() => PiRuntimeSettingsStore.ValidateLaunch(new(["--tools", "bash"], Tools: tools)));
        Assert.Throws<ArgumentException>(() => PiRuntimeSettingsStore.ValidateLaunch(new(Tools: new(Excluded: ["*"]))));
    }

    [Fact]
    public async Task OlderPiRejectsManagedSelectionWithoutReplacingSavedSettings()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions();
        await PiRuntimeSettingsStore.SaveAsync(options.CanonicalDataRoot, new(null, new(), new(CommandTimeoutSeconds: 75)));
        await using var host = await PiStation.Host.Hosting.EmbeddedEnvironmentHost.StartAsync(options);
        var result = await host.Environment.ConfigurePiRuntimeAsync(new(options.PiInstallation!.ExecutablePath,
            Launch: new(Tools: new(PiToolSelectionMode.Allowlist, ["powershell"]))));
        Assert.False(result.Available);
        Assert.Contains("0.85.0", result.Message);
        Assert.Equal(75, PiRuntimeSettingsStore.Load(options.CanonicalDataRoot).Launch!.CommandTimeoutSeconds);
        Assert.Null((await host.Environment.GetPiRuntimeConfigurationAsync()).Launch!.Tools);
    }

    [Theory]
    [InlineData("--")]
    [InlineData("--mode=interactive")]
    [InlineData("--session-id")]
    [InlineData("--fork")]
    [InlineData("-ne")]
    [InlineData("--list-models")]
    public void AdvancedArgumentsCannotOverrideManagedLifecycle(string argument) =>
        Assert.Throws<ArgumentException>(() => PiRuntimeSettingsStore.ValidateLaunch(new([argument])));

    [Fact]
    public async Task AdvancedArgumentsEnvironmentAndTimeoutsRoundTrip()
    {
        using var directory = new HostTestDirectory();
        var launch = PiRuntimeSettingsStore.ValidateLaunch(new(["--append-system-prompt", "a value with spaces"], new Dictionary<string, string?> { ["EXAMPLE_VALUE"] = "value with spaces" }, 120, 10));
        await PiRuntimeSettingsStore.SaveAsync(directory.Path, new("pi", new(), launch));
        var loaded = PiRuntimeSettingsStore.Load(directory.Path).Launch!;
        Assert.Equal(launch.Arguments, loaded.Arguments); Assert.Equal("value with spaces", loaded.EnvironmentVariables!["EXAMPLE_VALUE"]);
        Assert.Equal(120, loaded.CommandTimeoutSeconds); Assert.Equal(10, loaded.ShutdownTimeoutSeconds);
        Assert.Throws<ArgumentException>(() => PiRuntimeSettingsStore.ValidateLaunch(new(EnvironmentVariables: new Dictionary<string, string?> { ["PISTATION_PERMISSION_MODE"] = "full-access" })));
    }

    [Fact]
    public async Task MigratesLegacyPathAndPersistsExplicitExtensionPolicy()
    {
        using var directory = new HostTestDirectory();
        await File.WriteAllTextAsync(directory.GetPath("pi-executable.txt"), "C:/pi/old.exe");
        Assert.Equal("C:/pi/old.exe", PiRuntimeSettingsStore.Load(directory.Path).ExecutablePath);
        var extension = directory.GetPath("extension with spaces.ts");
        await File.WriteAllTextAsync(extension, "export default () => {}");
        var extensions = PiRuntimeSettingsStore.Validate(new(true, [extension, extension]));
        Assert.Single(extensions.Paths!);
        await PiRuntimeSettingsStore.SaveAsync(directory.Path, new("C:/pi/new.exe", extensions));
        var loaded = PiRuntimeSettingsStore.Load(directory.Path);
        Assert.Equal("C:/pi/new.exe", loaded.ExecutablePath);
        Assert.True(loaded.Extensions.DiscoverInstalled);
        Assert.Equal(extension, Assert.Single(loaded.Extensions.Paths!));
        await PiRuntimeSettingsStore.SaveAsync(directory.Path, loaded with { Extensions = new(false, []) });
        Assert.False(PiRuntimeSettingsStore.Load(directory.Path).Extensions.DiscoverInstalled);
        Assert.Empty(PiRuntimeSettingsStore.Load(directory.Path).Extensions.Paths!);
    }

    [Fact]
    public async Task OversizedConfigurationDoesNotReplaceReadableSettings()
    {
        using var directory = new HostTestDirectory();
        await PiRuntimeSettingsStore.SaveAsync(directory.Path, new("C:/pi/working.exe", new()));
        await Assert.ThrowsAsync<IOException>(() => PiRuntimeSettingsStore.SaveAsync(directory.Path,
            new(new string('x', 64 * 1024), new())));
        Assert.Equal("C:/pi/working.exe", PiRuntimeSettingsStore.Load(directory.Path).ExecutablePath);
    }

    [Fact]
    public void RejectsRelativeAndMissingExplicitExtensions()
    {
        using var directory = new HostTestDirectory();
        Assert.Throws<ArgumentException>(() => PiRuntimeSettingsStore.Validate(new(false, ["../extension.ts"])));
        Assert.Throws<FileNotFoundException>(() => PiRuntimeSettingsStore.Validate(new(false, [directory.GetPath("missing.ts")])));
    }
}
