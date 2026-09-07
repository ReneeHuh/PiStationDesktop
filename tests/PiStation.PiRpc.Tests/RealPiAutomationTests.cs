using System.Text.Json;
using PiStation.PiRpc.Discovery;
using PiStation.PiRpc.Process;

namespace PiStation.PiRpc.Tests;

public sealed class RealPiAutomationTests
{
    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task AutomationCommandsPersistInIsolatedPiSettingsAcrossProcessRestart()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var agent = directory.CreateDirectory("agent");
        var options = new PiProcessLaunchOptions
        {
            Installation = await new PiLocator().LocateAsync(new PiLocatorOptions { ExplicitPiPath = Environment.GetEnvironmentVariable("PISTATION_PI_PATH") }, timeout.Token),
            ProjectDirectory = directory.CreateDirectory("project"), SessionDirectory = directory.CreateDirectory("sessions"),
            SessionId = Guid.NewGuid().ToString("N"),
            EnvironmentVariables = new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = agent },
        };
        await using (var process = await PiProcessLauncher.StartAsync(options, timeout.Token))
        {
            foreach (var enabled in new[] { true, false })
            {
                await process.Connection.SetAutoCompactionAsync(enabled, timeout.Token);
                await process.Connection.SetAutoRetryAsync(enabled, timeout.Token);
                Assert.Equal(enabled, (await process.Connection.GetStateAsync(timeout.Token)).ReportedAutoCompactionEnabled);
            }
        }
        // The fixture uses its own agent directory: never reads or modifies user credentials/settings.
        using (var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(agent, "settings.json"), timeout.Token)))
        {
            Assert.False(settings.RootElement.GetProperty("compaction").GetProperty("enabled").GetBoolean());
            Assert.False(settings.RootElement.GetProperty("retry").GetProperty("enabled").GetBoolean());
        }
        await using var restarted = await PiProcessLauncher.StartAsync(options, timeout.Token);
        Assert.False((await restarted.Connection.GetStateAsync(timeout.Token)).ReportedAutoCompactionEnabled);
        await restarted.Connection.SetAutoRetryAsync(true, timeout.Token);
        await restarted.Connection.SetAutoCompactionAsync(true, timeout.Token);
        Assert.True((await restarted.Connection.GetStateAsync(timeout.Token)).ReportedAutoCompactionEnabled);
    }
}
