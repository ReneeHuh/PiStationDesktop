using PiStation.PiRpc.Discovery;
using PiStation.PiRpc.Process;
using PiStation.PiRpc.Sessions;

namespace PiStation.PiRpc.Tests;

public sealed class RealPiSessionCopyTests
{
    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task ImportedAndForkedSessionsResumeIndependentlyInRealPi()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var options = new PiProcessLaunchOptions
        {
            Installation = await new PiLocator().LocateAsync(new PiLocatorOptions { ExplicitPiPath = Environment.GetEnvironmentVariable("PISTATION_PI_PATH") }, timeout.Token),
            ProjectDirectory = directory.CreateDirectory("project"), SessionDirectory = directory.CreateDirectory("sessions"), SessionId = Guid.NewGuid().ToString("N"),
            AdditionalArguments = ["--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "offline-provider.ts"), "--provider", "pistation-offline", "--model", "deterministic"],
            EnvironmentVariables = new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = directory.CreateDirectory("agent"), ["PISTATION_TEST_TRACE"] = directory.GetPath("trace.jsonl") },
        };
        string sourcePath;
        string firstAnswer;
        await using (var source = await PiProcessLauncher.StartAsync(options, timeout.Token))
        {
            await source.Connection.PromptAsync("First conversation turn", timeout.Token);
            await FakePiTestHost.ReadUntilSettledAsync(source.Connection, timeout.Token);
            firstAnswer = (await source.Connection.GetEntriesAsync(cancellationToken: timeout.Token)).LeafId!;
            await source.Connection.PromptAsync("Second conversation turn", timeout.Token);
            await FakePiTestHost.ReadUntilSettledAsync(source.Connection, timeout.Token);
            sourcePath = (await source.Connection.GetStateAsync(timeout.Token)).SessionFile!;
        }
        var original = await File.ReadAllBytesAsync(sourcePath, timeout.Token);
        var document = await PiSessionDocument.ReadAsync(sourcePath, timeout.Token);
        var targetProject = directory.CreateDirectory("target-project");
        var importedId = Guid.NewGuid().ToString("N");
        var importedPath = Path.Combine(options.SessionDirectory, importedId + ".jsonl");
        await File.WriteAllBytesAsync(importedPath, document.Copy(importedId, targetProject, sourcePath), timeout.Token);
        var forkId = Guid.NewGuid().ToString("N");
        var forkPath = Path.Combine(options.SessionDirectory, forkId + ".jsonl");
        await File.WriteAllBytesAsync(forkPath, document.Copy(forkId, targetProject, sourcePath, firstAnswer), timeout.Token);
        foreach (var id in new[] { importedId, forkId })
        {
            await using var copy = await PiProcessLauncher.StartAsync(options with { SessionId = id, ProjectDirectory = targetProject }, timeout.Token);
            var entries = await copy.Connection.GetEntriesAsync(cancellationToken: timeout.Token);
            var restored = string.Join('\n', entries.Entries.Select(entry => entry.GetRawText()));
            Assert.Contains("First conversation turn", restored);
            Assert.Equal(id == importedId, restored.Contains("Second conversation turn", StringComparison.Ordinal));
            await copy.Connection.PromptAsync("Continue only this copied session", timeout.Token);
            await FakePiTestHost.ReadUntilSettledAsync(copy.Connection, timeout.Token);
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(sourcePath, timeout.Token));
        await using var resumed = await PiProcessLauncher.StartAsync(options with { SessionId = forkId, ProjectDirectory = targetProject }, timeout.Token);
        Assert.Contains((await resumed.Connection.GetEntriesAsync(cancellationToken: timeout.Token)).Entries,
            entry => entry.GetRawText().Contains("Continue only this copied session", StringComparison.Ordinal));
        Assert.Equal(("pistation-offline", "deterministic", "off"), (await PiSessionDocument.ReadAsync(forkPath, timeout.Token)).Configuration());
    }
}
