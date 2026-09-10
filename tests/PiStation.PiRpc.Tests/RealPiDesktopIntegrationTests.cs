using System.Text.Json;
using System.Text.Json.Nodes;
using PiStation.PiRpc.Discovery;
using PiStation.PiRpc.Process;
using PiStation.PiRpc.Sessions;

namespace PiStation.PiRpc.Tests;

public sealed class RealPiDesktopIntegrationTests
{
    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task ArbitraryComponentFactoryReceivesNativeInputAndDisposesAfterCompletion()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var extension = directory.GetPath("component.ts");
        await File.WriteAllTextAsync(extension, """
            import { writeFileSync } from 'node:fs';
            export default function(pi) {
              pi.registerCommand('desktop-component-test', { handler: async (args, ctx) => {
                let disposed = false;
                const result = await ctx.ui.custom((_tui, _theme, _keys, done) => {
                  if (args === 'async') setTimeout(() => done('automatic'), 100);
                  return { render: () => ['Custom component fixture'], handleInput: value => { if (value === '\r') done('selected'); }, dispose: () => { disposed = true; } };
                });
                writeFileSync(process.env.PISTATION_TEST_TRACE, JSON.stringify({ result, disposed }));
              } });
            }
            """);
        var options = await Options(directory, timeout.Token);
        options = options with { AdditionalArguments = [..options.AdditionalArguments, "--extension", extension] };
        await using var pi = await PiProcessLauncher.StartAsync(options, timeout.Token);
        var command = pi.Connection.PromptAsync("/desktop-component-test", timeout.Token);
        await foreach (var item in pi.Connection.ReadEventsAsync(timeout.Token))
        {
            if (item is not PiStation.PiRpc.Wire.Events.PiInputRequestedEvent input) continue;
            Assert.Contains("Custom component fixture", input.Title);
            Assert.NotNull(input.ComponentId);
            await pi.Connection.RespondToExtensionTextAsync(input.RequestId, "Enter", timeout.Token);
            break;
        }
        await command;
        var result = JsonNode.Parse(await File.ReadAllTextAsync(options.EnvironmentVariables["PISTATION_TEST_TRACE"]!, timeout.Token))!;
        Assert.Equal("selected", result["result"]!.GetValue<string>());
        Assert.True(result["disposed"]!.GetValue<bool>());
        var automatic = pi.Connection.PromptAsync("/desktop-component-test async", timeout.Token);
        string? pendingComponent = null;
        await foreach (var item in pi.Connection.ReadEventsAsync(timeout.Token))
        {
            if (item is PiStation.PiRpc.Wire.Events.PiInputRequestedEvent input) pendingComponent = input.ComponentId;
            if (item is PiStation.PiRpc.Wire.Events.PiComponentClosedEvent closed && closed.ComponentId == pendingComponent) break;
        }
        Assert.NotNull(pendingComponent);
        await automatic;
        result = JsonNode.Parse(await File.ReadAllTextAsync(options.EnvironmentVariables["PISTATION_TEST_TRACE"]!, timeout.Token))!;
        Assert.Equal("automatic", result["result"]!.GetValue<string>());
        Assert.True(result["disposed"]!.GetValue<bool>());
    }

    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task BatchModeChangesActualOverlapAndHonorsPerToolSequentialRestrictions()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var options = await Options(directory, timeout.Token);
        options = options with { AdditionalArguments = options.AdditionalArguments.Select(argument => argument.Replace("offline-provider.ts", "batch-provider.ts", StringComparison.Ordinal).Replace("pistation-offline", "pistation-batch-offline", StringComparison.Ordinal)).ToArray() };
        await using var pi = await PiProcessLauncher.StartAsync(options, timeout.Token);
        var trace = options.EnvironmentVariables["PISTATION_TEST_TRACE"]!;
        foreach (var (mode, prompt, expected) in new[] {
            ("parallel", "Run batch", new[] { "start0", "start1", "end0", "end1" }),
            ("sequential", "Run batch", new[] { "start0", "end0", "start1", "end1" }),
            ("parallel", "Run barrier", new[] { "start0", "end0", "start1", "end1" }) })
        {
            await File.WriteAllTextAsync(trace, "", timeout.Token);
            await pi.Connection.ManageAsync(new() { ["action"] = "toolExecution", ["toolExecution"] = mode }, timeout.Token);
            await pi.Connection.PromptAsync(prompt, timeout.Token);
            await FakePiTestHost.ReadUntilSettledAsync(pi.Connection, timeout.Token);
            var actual = await File.ReadAllLinesAsync(trace, timeout.Token);
            if (mode == "parallel" && prompt == "Run batch") { Assert.Equal(expected[..2], actual[..2]); Assert.Equal(expected[2..].Order(), actual[2..].Order()); }
            else Assert.Equal(expected, actual);
        }
    }

    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task FreshApiKeyPromptUsesSecretInputAndWritesOnlyIsolatedPiCredentials()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var options = await Options(directory, timeout.Token);
        await using var pi = await PiProcessLauncher.StartAsync(options, timeout.Token);
        var snapshot = await pi.Connection.ManageAsync(new() { ["action"] = "inspect" }, timeout.Token);
        Assert.True(snapshot.GetProperty("providers").EnumerateArray().Single(provider => provider.GetProperty("providerId").GetString() == "openai").GetProperty("supportsApiKey").GetBoolean());
        var login = pi.Connection.ManageAsync(new() { ["action"] = "login", ["resourceId"] = "openai", ["authType"] = "api_key" }, timeout.Token);
        await foreach (var item in pi.Connection.ReadEventsAsync(timeout.Token))
        {
            if (item is not PiStation.PiRpc.Wire.Events.PiInputRequestedEvent input) continue;
            Assert.True(input.IsSecret);
            await pi.Connection.RespondToExtensionTextAsync(input.RequestId, "isolated-dummy-key-no-provider-request", timeout.Token);
            break;
        }
        var result = await login;
        Assert.DoesNotContain("isolated-dummy-key", result.GetRawText());
        var auth = Path.Combine(options.EnvironmentVariables["PI_CODING_AGENT_DIR"]!, "auth.json");
        Assert.Contains("isolated-dummy-key-no-provider-request", await File.ReadAllTextAsync(auth, timeout.Token));
        await pi.Connection.ManageAsync(new() { ["action"] = "logout", ["resourceId"] = "openai" }, timeout.Token);
        Assert.DoesNotContain("isolated-dummy-key", await File.ReadAllTextAsync(auth, timeout.Token));
    }

    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task ExactLoaderAttributionReloadAndToolExecutionPreserveTheConversation()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var hook = directory.GetPath("event-only.ts");
        var broken = directory.GetPath("broken.ts");
        await File.WriteAllTextAsync(hook, "export default function(pi) { pi.on('session_start', (_, ctx) => ctx.ui.setStatus('event-only', 'v1')); }");
        await File.WriteAllTextAsync(broken, "export default function(pi) { pi.on('session_start', () => {}); }");
        var options = await Options(directory, timeout.Token);
        options = options with { AdditionalArguments = [..options.AdditionalArguments, "--extension", hook, "--extension", broken] };
        await using var pi = await PiProcessLauncher.StartAsync(options, timeout.Token);
        var read = await pi.Connection.ManageAsync(new() { ["action"] = "inspect" }, timeout.Token);
        Assert.True(read.GetProperty("authoritativeResources").GetBoolean());
        var resources = read.GetProperty("resources").EnumerateArray().ToArray();
        Assert.True(resources.Single(r => r.GetProperty("path").GetString() == hook).GetProperty("confirmedLoaded").GetBoolean());
        Assert.True(resources.Single(r => r.GetProperty("path").GetString() == broken).GetProperty("confirmedLoaded").GetBoolean());
        await pi.Connection.PromptAsync("Keep this conversation through reload", timeout.Token);
        await FakePiTestHost.ReadUntilSettledAsync(pi.Connection, timeout.Token);
        var state = await pi.Connection.GetStateAsync(timeout.Token);
        var before = await PiSessionDocument.ReadAsync(state.SessionFile!, timeout.Token);
        await pi.Connection.ManageAsync(new() { ["action"] = "toolExecution", ["toolExecution"] = "sequential" }, timeout.Token);
        await File.WriteAllTextAsync(broken, "export default function() { throw new Error('isolated-load-failure'); }");
        await pi.Connection.ManageAsync(new() { ["action"] = "reload" }, timeout.Token);
        var failed = await pi.Connection.ManageAsync(new() { ["action"] = "inspect" }, timeout.Token);
        Assert.Contains("isolated-load-failure", failed.GetProperty("resources").EnumerateArray().Single(r => r.GetProperty("path").GetString() == broken).GetProperty("loadError").GetString());
        await File.WriteAllTextAsync(broken, "export default function(pi) { pi.on('session_start', () => {}); }");
        await pi.Connection.ManageAsync(new() { ["action"] = "reload" }, timeout.Token);
        read = await pi.Connection.ManageAsync(new() { ["action"] = "inspect" }, timeout.Token);
        Assert.Equal("sequential", read.GetProperty("toolExecution").GetString());
        Assert.True(read.GetProperty("resources").EnumerateArray().Single(r => r.GetProperty("path").GetString() == broken).GetProperty("confirmedLoaded").GetBoolean());
        Assert.Equal(state.SessionId, (await pi.Connection.GetStateAsync(timeout.Token)).SessionId);
        var after = await PiSessionDocument.ReadAsync(state.SessionFile!, timeout.Token);
        Assert.Equal(before.Entries.Count, after.Entries.Count);
        await pi.Connection.PromptAsync("Continue", timeout.Token);
        await FakePiTestHost.ReadUntilSettledAsync(pi.Connection, timeout.Token);
    }

    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task TemporaryPiKeepsConversationInMemoryWithoutWritingJsonl()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var options = (await Options(directory, timeout.Token)) with { TemporaryHistory = true };
        await using (var pi = await PiProcessLauncher.StartAsync(options, timeout.Token))
        {
            await pi.Connection.PromptAsync("Temporary conversation", timeout.Token);
            await FakePiTestHost.ReadUntilSettledAsync(pi.Connection, timeout.Token);
            var state = await pi.Connection.GetStateAsync(timeout.Token);
            Assert.Equal(options.SessionId, state.SessionId);
            Assert.Null(state.SessionFile);
            Assert.True(state.MessageCount >= 2);
        }
        Assert.Empty(Directory.EnumerateFiles(options.SessionDirectory, "*.jsonl", SearchOption.AllDirectories));
    }

    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task PiQuotaPublisherUsesTheRealExtensionBusAndSurvivesReload()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var options = await Options(directory, timeout.Token);
        var root = directory.CreateDirectory("quota-feeds");
        var extension = directory.GetPath("quota-fixture.ts");
        await File.WriteAllTextAsync(extension, """
            export default function(pi) {
              pi.registerCommand("quota-fixture", { handler: async () => {
                let result;
                pi.events.emit("pistation:usage-limits:v1", { id: "real-pi-fixture", label: "Offline subscription",
                  accounts: [{ id: "isolated", provider: "pistation-offline", label: "Isolated account",
                    windows: [{ id: "s", kind: "session", label: "Session", usedPercent: 81,
                      resetsAt: new Date(Date.now() + 3600000).toISOString(), windowDurationMins: 300 }] }],
                  reply: value => { result = value; } });
                if (!result?.success) throw new Error("Quota publisher did not acknowledge the fixture.");
              } });
            }
            """, timeout.Token);
        options = options with { AdditionalArguments = [.. options.AdditionalArguments, "--extension", extension],
            EnvironmentVariables = new Dictionary<string, string?>(options.EnvironmentVariables) { ["PISTATION_QUOTA_ROOT"] = root } };
        await using var pi = await PiProcessLauncher.StartAsync(options, timeout.Token);
        await pi.Connection.PromptAsync("/quota-fixture", timeout.Token);
        var file = Assert.Single(Directory.EnumerateFiles(root, "*.json"));
        using (var json = JsonDocument.Parse(await File.ReadAllBytesAsync(file, timeout.Token)))
        {
            Assert.Equal("real-pi-fixture", json.RootElement.GetProperty("id").GetString());
            Assert.Equal(81, json.RootElement.GetProperty("accounts")[0].GetProperty("windows")[0].GetProperty("usedPercent").GetInt32());
        }
        await pi.Connection.ManageAsync(new() { ["action"] = "reload" }, timeout.Token);
        File.Delete(file);
        await pi.Connection.PromptAsync("/quota-fixture", timeout.Token);
        Assert.Equal(file, Assert.Single(Directory.EnumerateFiles(root, "*.json")));
    }

    private static async Task<PiProcessLaunchOptions> Options(TemporaryDirectory directory, CancellationToken token) => new()
    {
        Installation = await new PiLocator().LocateAsync(new PiLocatorOptions { ExplicitPiPath = Environment.GetEnvironmentVariable("PISTATION_PI_PATH") }, token),
        SdkAdapterPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pistation-sdk.ts"),
        ProjectDirectory = directory.CreateDirectory("project"), SessionDirectory = directory.CreateDirectory("sessions"), SessionId = Guid.NewGuid().ToString("N"),
        AdditionalArguments = ["--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "pistation-resources.ts"),
            "--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "offline-provider.ts"), "--provider", "pistation-offline", "--model", "deterministic"],
        EnvironmentVariables = new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = directory.CreateDirectory("agent"), ["PISTATION_TEST_TRACE"] = directory.GetPath("trace.jsonl"), ["PI_OFFLINE"] = "1", ["PI_TELEMETRY"] = "0" },
    };
}
