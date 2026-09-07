using PiStation.Host.Hosting;

namespace PiStation.Host.Tests;

public sealed class HostDataPathsTests
{
    [Fact]
    public void UsesNormalRootForANewEnvironmentAndReusesExistingPackageDataInPlace()
    {
        using var directory = new HostTestDirectory();
        Assert.Equal(Path.Combine(directory.Path, "PiStationDesktop"), HostDataPaths.ResolveDefaultRoot(directory.Path));
        var legacy = directory.CreateDirectory(Path.Combine("Packages", "584BC26F-2CB5-42F0-A9E5-6DB195B0890E_test", "LocalCache", "Local", "PiStationDesktop"));
        File.WriteAllText(Path.Combine(legacy, "host.db"), "existing data");
        Assert.Equal(legacy, HostDataPaths.ResolveDefaultRoot(directory.Path));
        Assert.Equal("existing data", File.ReadAllText(Path.Combine(legacy, "host.db")));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "PiStationDesktop")));
    }

    [Fact]
    public void AmbiguousDataDirectoriesRequireAnExplicitChoiceWithoutMergingOrDeleting()
    {
        using var directory = new HostTestDirectory();
        var normal = directory.CreateDirectory("PiStationDesktop");
        var legacy = directory.CreateDirectory(Path.Combine("Packages", "584BC26F-2CB5-42F0-A9E5-6DB195B0890E_test", "LocalCache", "Local", "PiStationDesktop"));
        File.WriteAllText(Path.Combine(normal, "host.db"), "normal");
        File.WriteAllText(Path.Combine(legacy, "host.db"), "legacy");
        Assert.Throws<InvalidOperationException>(() => HostDataPaths.ResolveDefaultRoot(directory.Path));
        Assert.Equal("normal", File.ReadAllText(Path.Combine(normal, "host.db")));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(legacy, "host.db")));
    }
}
