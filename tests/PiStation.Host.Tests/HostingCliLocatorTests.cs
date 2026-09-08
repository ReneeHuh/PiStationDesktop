using PiStation.Host.SourceControl;

namespace PiStation.Host.Tests;

public sealed class HostingCliLocatorTests
{
    [Fact]
    public void FindsFreshlyInstalledCliWithoutChangingTheProcessPath()
    {
        var root = Path.GetTempPath();
        var installed = Path.Combine(root, "installed", "gh.exe");
        var found = HostingCliLocator.FindGitHubCli(Path.Combine(root, "old-path"), Path.Combine(root, "installed"), null, root,
            path => path == installed);
        Assert.Equal(installed, found);
    }

    [Fact]
    public void HonorsExistingCliAndDoesNotSearchRelativeRepositoryPaths()
    {
        var root = Path.GetTempPath();
        var selected = Path.Combine(root, "selected", "gh.exe");
        var visited = new List<string>();
        var found = HostingCliLocator.FindGitHubCli(".;relative;" + Path.Combine(root, "selected"), Path.Combine(root, "installed"), null, root,
            path => { visited.Add(path); return true; });
        Assert.Equal(selected, found);
        Assert.Equal(selected, Assert.Single(visited));
    }
}
