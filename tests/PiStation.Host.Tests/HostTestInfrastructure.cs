using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using PiStation.Host;
using PiStation.Host.Hosting;
using PiStation.PiRpc.Discovery;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Tests;

internal sealed class HostTestDirectory : IDisposable
{
    private static readonly string TestRoot = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PiStationDesktop.HostTests"));

    public HostTestDirectory()
    {
        Path = System.IO.Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateDirectory(string name)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetPath(string name) => System.IO.Path.Combine(Path, name);

    public HostOptions CreateOptions(string scenario = "normal", int? toolOutputCharacterLimit = null) => new()
    {
        ApplicationDataRoot = CreateDirectory("data"),
        EnvironmentName = "Test Station",
        PiInstallation = new PiInstallation(
            PiInstallationKind.NativeExecutable,
            FindFakePiExecutable(),
            [],
            new SemanticVersion(0, 84, 4),
            null,
            null,
            "test"),
        AdditionalPiArguments = ["--fake-pi-scenario", scenario],
        JournalEventLimit = 128,
        SubscriberCapacity = 64,
        ToolOutputCharacterLimit = toolOutputCharacterLimit ?? 32 * 1024,
    };

    public void Dispose()
    {
        var fullPath = System.IO.Path.GetFullPath(Path);
        var requiredPrefix = TestRoot + System.IO.Path.DirectorySeparatorChar;
        if (Directory.Exists(fullPath) &&
            fullPath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            ClearReadOnlyAttributes(fullPath);
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    private static string FindFakePiExecutable()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (!File.Exists(System.IO.Path.Combine(directory.FullName, "PiStationDesktop.slnx")))
            {
                continue;
            }

            var configuration = AppContext.BaseDirectory.Contains(
                $"{System.IO.Path.DirectorySeparatorChar}Release{System.IO.Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase)
                ? "Release"
                : "Debug";
            var executable = System.IO.Path.Combine(
                directory.FullName,
                "tests",
                "PiStation.FakePi",
                "bin",
                configuration,
                "net10.0",
                "PiStation.FakePi.exe");
            return File.Exists(executable)
                ? executable
                : throw new FileNotFoundException("The FakePi test executable was not built.", executable);
        }

        throw new DirectoryNotFoundException("Could not locate the solution root.");
    }
}

internal static class HostTestConnection
{
    public static HubConnection Create(EmbeddedEnvironmentHost host, bool authenticated = true)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(host.HubAddress, options =>
            {
                if (authenticated)
                {
                    options.Headers["Authorization"] = $"Bearer {host.BearerCredential}";
                }
            })
            .AddJsonProtocol(json =>
                json.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ProtocolJsonContext.Default))
            .Build();
        return connection;
    }
}
