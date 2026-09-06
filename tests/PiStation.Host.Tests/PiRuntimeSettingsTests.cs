using PiStation.Host;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class PiRuntimeSettingsTests
{
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
