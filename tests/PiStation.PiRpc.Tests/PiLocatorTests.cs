using System.Text;
using PiStation.PiRpc.Discovery;

namespace PiStation.PiRpc.Tests;

public sealed class PiLocatorTests
{
    [Fact]
    public async Task NativeExecutableMustMeetMinimumPiVersion()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var executable = temporaryDirectory.GetPath("pi.exe");
        File.WriteAllBytes(executable, []);
        var locator = new PiLocator(new FakeProbe((executable, "0.84.1")));

        var exception = await Assert.ThrowsAsync<PiDiscoveryException>(() => locator.LocateAsync(
            new PiLocatorOptions { ExplicitPiPath = executable }));

        Assert.Equal(PiDiscoveryFailure.UnsupportedPiVersion, exception.Failure);
    }

    [Theory]
    [InlineData("pi.cmd")]
    [InlineData("pi.ps1")]
    public async Task PackageDiscoveryUsesBinPiAndPrefersCompatibleAdjacentNode(string launcherName)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installDirectory = temporaryDirectory.CreateDirectory("installation");
        var packageDirectory = System.IO.Path.Combine(
            installDirectory,
            "node_modules",
            "@earendil-works",
            "pi-coding-agent");
        Directory.CreateDirectory(System.IO.Path.Combine(packageDirectory, "dist", "bundle"));
        var command = System.IO.Path.Combine(installDirectory, launcherName);
        var adjacentNode = System.IO.Path.Combine(installDirectory, "node.exe");
        var pathDirectory = temporaryDirectory.CreateDirectory("path");
        var pathNode = System.IO.Path.Combine(pathDirectory, "node.exe");
        var entrypoint = System.IO.Path.Combine(packageDirectory, "dist", "bundle", "cli.js");
        File.WriteAllBytes(command, []);
        File.WriteAllBytes(adjacentNode, []);
        File.WriteAllBytes(pathNode, []);
        File.WriteAllBytes(entrypoint, []);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(packageDirectory, "package.json"),
            """
            {
              "name": "@earendil-works/pi-coding-agent",
              "version": "0.84.4",
              "bin": { "pi": "dist/bundle/cli.js" },
              "engines": { "node": ">=22.19.0" }
            }
            """,
            new UTF8Encoding(false));
        var locator = new PiLocator(new FakeProbe(
            (adjacentNode, "v22.23.2"),
            (pathNode, "v99.0.0")));

        var installation = await locator.LocateAsync(new PiLocatorOptions
        {
            ExplicitPiPath = command,
            SearchPath = pathDirectory,
        });

        Assert.Equal(PiInstallationKind.NodePackage, installation.Kind);
        Assert.Equal(adjacentNode, installation.ExecutablePath);
        Assert.Equal(entrypoint, Assert.Single(installation.LaunchPrefixArguments));
        Assert.Equal(new SemanticVersion(22, 23, 2), installation.NodeVersion);
    }

    [Fact]
    public async Task PackageDiscoveryRejectsEscapingBinEntrypoint()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var packageDirectory = temporaryDirectory.CreateDirectory("package");
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(packageDirectory, "package.json"),
            """
            {
              "name": "@earendil-works/pi-coding-agent",
              "version": "0.84.4",
              "bin": { "pi": "../outside.js" },
              "engines": { "node": ">=22.19.0" }
            }
            """,
            new UTF8Encoding(false));
        File.WriteAllBytes(temporaryDirectory.GetPath("outside.js"), []);

        var exception = await Assert.ThrowsAsync<PiDiscoveryException>(() => new PiLocator(new FakeProbe()).LocateAsync(
            new PiLocatorOptions { ExplicitPiPath = packageDirectory }));

        Assert.Equal(PiDiscoveryFailure.MissingEntrypoint, exception.Failure);
    }

    [Theory]
    [InlineData("v0.84.4", 0, 84, 4)]
    [InlineData("22.19.0+build", 22, 19, 0)]
    public void SemanticVersionParsesSupportedForms(string value, int major, int minor, int patch)
    {
        var version = SemanticVersion.Parse(value);

        Assert.Equal(new SemanticVersion(major, minor, patch), version);
    }

    private sealed class FakeProbe(params (string Path, string Version)[] results) : IExecutableProbe
    {
        private readonly Dictionary<string, string> _results = results.ToDictionary(
            static result => System.IO.Path.GetFullPath(result.Path),
            static result => result.Version,
            StringComparer.OrdinalIgnoreCase);

        public Task<string> GetVersionOutputAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(["--version"], arguments);
            return Task.FromResult(_results[System.IO.Path.GetFullPath(executablePath)]);
        }
    }
}
