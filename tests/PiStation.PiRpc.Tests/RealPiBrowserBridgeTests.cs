using System.Text.Json;
using PiStation.PiRpc.Discovery;
using PiStation.PiRpc.Process;

namespace PiStation.PiRpc.Tests;

public sealed class RealPiBrowserBridgeTests
{
    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task BundledBridgeForwardsExplicitTargetsAndEnforcesInspectPermission()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var root = directory.CreateDirectory("browser");
        var thread = Directory.CreateDirectory(Path.Combine(root, "thread-probe")).FullName;
        var permission = Path.Combine(thread, "permission.json");
        var resultPath = directory.GetPath("result.json");
        await File.WriteAllTextAsync(permission, "{\"mode\":\"interact\"}", timeout.Token);
        var options = new PiProcessLaunchOptions
        {
            Installation = await new PiLocator().LocateAsync(new PiLocatorOptions { ExplicitPiPath = Environment.GetEnvironmentVariable("PISTATION_PI_PATH") }, timeout.Token),
            ProjectDirectory = directory.CreateDirectory("project"), SessionDirectory = directory.CreateDirectory("sessions"), SessionId = Guid.NewGuid().ToString("N"),
            AdditionalArguments = ["--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "browser-probe.ts")],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["PI_CODING_AGENT_DIR"] = directory.CreateDirectory("agent"), ["PISTATION_BROWSER_AUTOMATION_ROOT"] = root,
                ["PISTATION_BROWSER_THREAD_ID"] = "thread-probe", ["PISTATION_BROWSER_PROBE_RESULT"] = resultPath,
            },
        };
        await using var process = await PiProcessLauncher.StartAsync(options, timeout.Token);
        Assert.Contains(await process.Connection.GetCommandsAsync(timeout.Token), c => c.Name == "browser-probe");
        foreach (var action in new[] { "press_key", "scroll", "wait" })
        {
            var invocation = process.Connection.PromptAsync("/browser-probe " + JsonSerializer.Serialize(new
            {
                action, tabId = "background-tab", key = "Enter", deltaY = 500, condition = "loaded", timeoutMs = 1000,
            }), timeout.Token);
            var requestDirectory = Path.Combine(thread, "requests");
            string? requestPath;
            while ((requestPath = Directory.Exists(requestDirectory) ? Directory.EnumerateFiles(requestDirectory, "*.json").FirstOrDefault() : null) is null)
                await Task.Delay(50, timeout.Token);
            using var request = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath, timeout.Token));
            Assert.Equal(action, request.RootElement.GetProperty("operation").GetString());
            Assert.Equal("background-tab", request.RootElement.GetProperty("input").GetProperty("tabId").GetString());
            var responses = Directory.CreateDirectory(Path.Combine(thread, "responses")).FullName;
            var response = Path.Combine(responses, Path.GetFileName(requestPath));
            await File.WriteAllTextAsync(response + ".tmp", "{\"success\":true,\"data\":{\"ok\":true}}", timeout.Token);
            File.Move(response + ".tmp", response);
            await invocation;
            Assert.False(File.Exists(requestPath));
        }
        await File.WriteAllTextAsync(permission, "{\"mode\":\"inspect\"}", timeout.Token);
        foreach (var action in new[] { "press_key", "scroll" })
        {
            await process.Connection.PromptAsync("/browser-probe " + JsonSerializer.Serialize(new { action, key = "Enter" }), timeout.Token);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, timeout.Token));
            Assert.True(result.RootElement.GetProperty("isError").GetBoolean());
            Assert.Contains("inspect-only", result.RootElement.GetProperty("details").GetProperty("error").GetString());
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(thread, "requests"), "*.json"));
        }
    }
}
