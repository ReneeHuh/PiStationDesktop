using PiStation.PiRpc.Discovery;
using PiStation.PiRpc.Process;
using PiStation.PiRpc.Transport;
using PiStation.PiRpc.Wire.Events;

namespace PiStation.PiRpc.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    private static readonly string TestRoot = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PiStationDesktop.Tests"));

    public TemporaryDirectory()
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

    public void Dispose()
    {
        var fullPath = System.IO.Path.GetFullPath(Path);
        var requiredPrefix = TestRoot + System.IO.Path.DirectorySeparatorChar;
        if (Directory.Exists(fullPath) &&
            fullPath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}

internal static class FakePiTestHost
{
    public static async Task<PiProcess> StartAsync(
        TemporaryDirectory temporaryDirectory,
        string scenario = "normal",
        string sessionId = "test-session",
        PiRpcConnectionOptions? connectionOptions = null,
        int standardErrorCharacterLimit = 64 * 1024,
        CancellationToken cancellationToken = default)
    {
        var projectDirectory = temporaryDirectory.CreateDirectory("project");
        var sessionDirectory = temporaryDirectory.CreateDirectory("sessions");
        var executable = FindExecutable();
        var installation = new PiInstallation(
            PiInstallationKind.NativeExecutable,
            executable,
            [],
            new SemanticVersion(0, 84, 4),
            null,
            null,
            "test");

        return await PiProcessLauncher.StartAsync(
            new PiProcessLaunchOptions
            {
                Installation = installation,
                ProjectDirectory = projectDirectory,
                SessionDirectory = sessionDirectory,
                SessionId = sessionId,
                AdditionalArguments = ["--fake-pi-scenario", scenario],
                ConnectionOptions = connectionOptions ?? new PiRpcConnectionOptions(),
                StandardErrorCharacterLimit = standardErrorCharacterLimit,
            },
            cancellationToken);
    }

    public static async Task<IReadOnlyList<PiRpcEvent>> ReadUntilSettledAsync(
        PiRpcConnection connection,
        CancellationToken cancellationToken = default)
    {
        var events = new List<PiRpcEvent>();
        await foreach (var @event in connection.ReadEventsAsync(cancellationToken))
        {
            events.Add(@event);
            if (@event is PiAgentSettledEvent)
            {
                return events;
            }
        }

        throw new InvalidOperationException("Pi event stream ended before agent_settled.");
    }

    private static string FindExecutable()
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
            if (File.Exists(executable))
            {
                return executable;
            }

            throw new FileNotFoundException("The FakePi test executable was not built.", executable);
        }

        throw new DirectoryNotFoundException("Could not locate the Pi Station Desktop solution root.");
    }
}
