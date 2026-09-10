using System.Text.Json.Nodes;
using PiStation.PiRpc.Discovery;
using PiStation.PiRpc.Process;

namespace PiStation.PiRpc.Tests;

public sealed class RealPiRuntimePreferenceTests
{
    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task TransportSavesWithRevisionPreservesOtherSettingsAndAppliesAfterRestart()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var agent = directory.CreateDirectory("agent");
        var settingsPath = Path.Combine(agent, "settings.json");
        await File.WriteAllTextAsync(settingsPath, "{\"theme\":\"light\",\"transport\":\"auto\"}", timeout.Token);
        var options = new PiProcessLaunchOptions
        {
            Installation = await new PiLocator().LocateAsync(new PiLocatorOptions { ExplicitPiPath = Environment.GetEnvironmentVariable("PISTATION_PI_PATH") }, timeout.Token),
            ProjectDirectory = directory.CreateDirectory("project"), SessionDirectory = directory.CreateDirectory("sessions"), SessionId = Guid.NewGuid().ToString(),
            AdditionalArguments = ["--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "pistation-resources.ts"),
                "--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "offline-provider.ts"), "--provider", "pistation-offline", "--model", "deterministic"],
            EnvironmentVariables = new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = agent, ["PI_OFFLINE"] = "1", ["PI_TELEMETRY"] = "0", ["PI_CACHE_RETENTION"] = "long" },
        };
        await using (var process = await PiProcessLauncher.StartAsync(options, timeout.Token))
        {
            var read = await process.Connection.ManageAsync(new() { ["action"] = "inspect" }, timeout.Token);
            var native = read.GetProperty("nativePreferences");
            Assert.Equal("auto", native.GetProperty("startupTransport").GetString());
            Assert.True(native.GetProperty("offline").GetBoolean());
            Assert.True(native.GetProperty("skipVersionCheck").GetBoolean());
            Assert.Equal("long", native.GetProperty("cacheRetention").GetString());
            var revision = native.GetProperty("transportRevision").GetString();
            var saved = await process.Connection.ManageAsync(new() { ["action"] = "saveTransport", ["transport"] = "sse", ["revision"] = revision }, timeout.Token);
            Assert.Equal("sse", saved.GetProperty("nativePreferences").GetProperty("savedTransport").GetString());
            Assert.Equal("auto", saved.GetProperty("nativePreferences").GetProperty("startupTransport").GetString());
            await Assert.ThrowsAnyAsync<Exception>(() => process.Connection.ManageAsync(new() { ["action"] = "saveTransport", ["transport"] = "auto", ["revision"] = revision }, timeout.Token));
            Assert.Equal("light", JsonNode.Parse(await File.ReadAllTextAsync(settingsPath, timeout.Token))!["theme"]!.GetValue<string>());
        }
        await using var restarted = await PiProcessLauncher.StartAsync(options, timeout.Token);
        var refreshed = await restarted.Connection.ManageAsync(new() { ["action"] = "inspect" }, timeout.Token);
        Assert.Equal("sse", refreshed.GetProperty("nativePreferences").GetProperty("startupTransport").GetString());
    }
}
