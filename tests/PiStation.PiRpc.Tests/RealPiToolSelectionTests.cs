using System.Text.Json;
using System.Text.Json.Nodes;
using PiStation.PiRpc.Discovery;
using PiStation.PiRpc.Process;
using PiStation.PiRpc.Transport;
using PiStation.PiRpc.Wire.Events;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.PiRpc.Tests;

public sealed class RealPiToolSelectionTests
{
    private static async Task<PiProcessLaunchOptions> Options(TemporaryDirectory directory, PiToolSelection selection,
        CancellationToken token, bool agents = false)
    {
        var installation = await new PiLocator().LocateAsync(new PiLocatorOptions
            { ExplicitPiPath = Environment.GetEnvironmentVariable("PISTATION_PI_PATH") }, token);
        Assert.True(OperatingSystem.IsWindows(), "This acceptance requires Windows PowerShell.");
        Assert.True(installation.PiVersion >= new SemanticVersion(0, 85, 0), "Select a Pi 0.85.0+ runtime with PISTATION_PI_PATH.");
        var project = directory.CreateDirectory("project");
        await File.WriteAllTextAsync(Path.Combine(project, "README.md"), "Read-only policy evidence", token);
        var args = new List<string>();
        foreach (var extension in new[] { "pistation-plan.ts", "pistation-resources.ts", "pistation-agents.ts", agents ? "agents-provider.ts" : "tools-provider.ts" })
            args.AddRange(["--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", extension)]);
        args.AddRange(["--provider", agents ? "pistation-agents-offline" : "pistation-tools-offline", "--model", "deterministic"]);
        args.AddRange(PiToolSelectionRules.LaunchArguments(selection));
        return new()
        {
            Installation = installation, ProjectDirectory = project, SessionDirectory = directory.CreateDirectory("sessions"),
            SessionId = Guid.NewGuid().ToString("N"), AdditionalArguments = args,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["PI_CODING_AGENT_DIR"] = directory.CreateDirectory("agent"),
                ["PISTATION_PLAN_STATE_PATH"] = directory.GetPath("plan.json"),
                ["PISTATION_AGENT_ROOT"] = directory.CreateDirectory("children"),
                ["PISTATION_AGENT_SETTINGS"] = directory.GetPath("agent-settings.json"),
                ["PISTATION_TOOL_SELECTION"] = JsonSerializer.Serialize(selection, ProtocolJsonContext.Default.PiToolSelection),
                ["PISTATION_PERMISSION_MODE"] = "full-access",
            },
        };
    }

    private static async Task<PiToolInventory> Inventory(PiRpcConnection connection, CancellationToken token)
    {
        var result = await connection.ManageAsync(new JsonObject { ["action"] = "inspect" }, token);
        return result.Deserialize(ProtocolJsonContext.Default.PiResourcesSnapshot)!.ToolInventory!;
    }
    private static async Task<IReadOnlyList<PiRpcEvent>> Prompt(PiRpcConnection connection, string text, CancellationToken token)
    {
        await connection.PromptAsync(text, token);
        return await FakePiTestHost.ReadUntilSettledAsync(connection, token);
    }

    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task PowerShellAndRegistryRestrictionsSurvivePlanningReloadAndRestart()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        var selection = new PiToolSelection(PiToolSelectionMode.Allowlist,
            ["read", "powershell", "write", "tool_policy_probe"], ["write", "tool_policy_probe"]);
        var options = await Options(directory, selection, token);
        await using (var process = await PiProcessLauncher.StartAsync(options, token))
        {
            var inventory = await Inventory(process.Connection, token);
            Assert.True(inventory.Tools.Single(tool => tool.Name == "powershell").Active);
            Assert.DoesNotContain(inventory.Tools, tool => tool.Name is "write" or "tool_policy_probe");
            var success = (await Prompt(process.Connection, "TOOLS_POWERSHELL", token)).OfType<PiToolExecutionCompletedEvent>().Single();
            Assert.Equal("powershell", success.ToolName);
            Assert.False(success.IsError, success.Result.GetRawText());
            Assert.Contains("PISTATION_POWERSHELL_OK", success.Result.GetRawText());
            var blocked = await Prompt(process.Connection, "TOOLS_BLOCKED", token);
            Assert.All(blocked.OfType<PiToolExecutionCompletedEvent>(), item => Assert.True(item.IsError));
            Assert.False(File.Exists(Path.Combine(options.ProjectDirectory, "blocked-write.txt")));
            Assert.False(File.Exists(Path.Combine(options.ProjectDirectory, "blocked-extension.txt")));
            var plan = await process.Connection.ManagePlanAsync(new JsonObject { ["action"] = "plan", ["expectedRevision"] = 0 }, token);
            Assert.Equal("pistation_plan_read", Assert.Single((await Inventory(process.Connection, token)).Tools, tool => tool.Active).Name);
            var read = (await Prompt(process.Connection, "TOOLS_READ", token)).OfType<PiToolExecutionCompletedEvent>().Single();
            Assert.False(read.IsError, read.Result.GetRawText());
            var planningBlocked = (await Prompt(process.Connection, "TOOLS_PLAN_BLOCK", token)).OfType<PiToolExecutionCompletedEvent>().Single();
            Assert.True(planningBlocked.IsError);
            Assert.Contains("planning policy blocks", planningBlocked.Result.GetRawText());
            Assert.False(File.Exists(Path.Combine(options.ProjectDirectory, "blocked-powershell.txt")));
            await process.Connection.PromptAsync("/tools-test-reload", token);
            Assert.DoesNotContain((await Inventory(process.Connection, token)).Tools, tool => tool.Name == "write");
            await process.Connection.ManagePlanAsync(new JsonObject { ["action"] = "off", ["expectedRevision"] = plan.GetProperty("revision").GetInt64() }, token);
        }
        await using var restarted = await PiProcessLauncher.StartAsync(options, token);
        Assert.True((await Inventory(restarted.Connection, token)).Tools.Single(tool => tool.Name == "powershell").Active);
        Assert.False((await Prompt(restarted.Connection, "TOOLS_POWERSHELL", token)).OfType<PiToolExecutionCompletedEvent>().Single().IsError);
    }

    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task NoToolsKeepsRegistryEmptyEvenWhenAnExtensionTriesToEnableTools()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var options = await Options(directory, new(PiToolSelectionMode.None), timeout.Token);
        await using var process = await PiProcessLauncher.StartAsync(options, timeout.Token);
        Assert.Empty((await Inventory(process.Connection, timeout.Token)).Tools);
        var setup = await process.Connection.ManageAgentsAsync(new JsonObject { ["action"] = "inspect" }, timeout.Token);
        Assert.False(setup.GetProperty("available").GetBoolean());
        await Assert.ThrowsAsync<PiStation.PiRpc.Diagnostics.PiRpcCommandException>(() => process.Connection.ManageAgentsAsync(
            new JsonObject { ["action"] = "prepare", ["expectedRevision"] = setup.GetProperty("revision").GetString(),
                ["workflow"] = JsonSerializer.SerializeToNode(new { mode = "single", tasks = new[] { new { agent = "worker", task = "WRITE" } } }) }, timeout.Token));
        await Prompt(process.Connection, "TOOLS_BLOCKED", timeout.Token);
        Assert.Empty((await Inventory(process.Connection, timeout.Token)).Tools);
        Assert.False(File.Exists(Path.Combine(options.ProjectDirectory, "blocked-write.txt")));
        Assert.False(File.Exists(Path.Combine(options.ProjectDirectory, "blocked-extension.txt")));
    }

    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task PowerShellFailuresAndCancellationReturnToolResults()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var options = await Options(directory, new(PiToolSelectionMode.Allowlist, ["powershell"]), timeout.Token);
        await using var process = await PiProcessLauncher.StartAsync(options, timeout.Token);
        var failed = (await Prompt(process.Connection, "TOOLS_FAIL", timeout.Token)).OfType<PiToolExecutionCompletedEvent>().Single();
        Assert.True(failed.IsError);
        Assert.Contains("TOOL_EXPECTED_FAILURE", failed.Result.GetRawText());
        await process.Connection.PromptAsync("TOOLS_WAIT", timeout.Token);
        await foreach (var item in process.Connection.ReadEventsAsync(timeout.Token))
            if (item is PiToolExecutionStartedEvent { ToolName: "powershell" }) break;
        await process.Connection.StopAsync(timeout.Token);
        await FakePiTestHost.ReadUntilSettledAsync(process.Connection, timeout.Token);
        Assert.False((await process.Connection.GetStateAsync(timeout.Token)).IsStreaming);
    }

    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task PowerShellRequiresApprovalInEveryNonFullAccessMode()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var options = await Options(directory, new(PiToolSelectionMode.Allowlist, ["powershell"]), timeout.Token);
        await using var process = await PiProcessLauncher.StartAsync(options, timeout.Token);
        foreach (var mode in new[] { "supervised", "auto-accept-edits", "auto" })
        {
            await process.Connection.ManageAsync(new JsonObject { ["action"] = "permission", ["mode"] = mode }, timeout.Token);
            foreach (var allow in new[] { false, true })
            {
                var confirmed = false;
                PiToolExecutionCompletedEvent? completed = null;
                await process.Connection.PromptAsync("TOOLS_POWERSHELL", timeout.Token);
                await foreach (var item in process.Connection.ReadEventsAsync(timeout.Token))
                {
                    if (item is PiConfirmRequestedEvent confirm)
                    {
                        Assert.Contains("powershell", confirm.Title);
                        confirmed = true;
                        await process.Connection.RespondToExtensionConfirmAsync(confirm.RequestId, allow, timeout.Token);
                    }
                    if (item is PiToolExecutionCompletedEvent result) completed = result;
                    if (item is PiAgentSettledEvent) break;
                }
                Assert.True(confirmed, mode);
                Assert.NotNull(completed);
                Assert.Equal(!allow, completed.IsError);
                if (allow) Assert.Contains("PISTATION_POWERSHELL_OK", completed.Result.GetRawText());
                else Assert.Contains("declined", completed.Result.GetRawText());
            }
        }
    }

    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task ChildPresetCannotRestoreAnExcludedTool()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var options = await Options(directory, new(PiToolSelectionMode.PiDefault, Excluded: ["write"]), timeout.Token, agents: true);
        await using var process = await PiProcessLauncher.StartAsync(options, timeout.Token);
        var setup = await process.Connection.ManageAgentsAsync(new JsonObject { ["action"] = "inspect" }, timeout.Token);
        Assert.Contains("powershell", setup.GetProperty("presets").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "worker")
            .GetProperty("tools").EnumerateArray().Select(tool => tool.GetString()));
        var workflow = new { mode = "single", tasks = new[] { new { agent = "worker", task = "WRITE" } } };
        var result = await Prompt(process.Connection, "PISTATION_AGENT_WORKFLOW\n" + JsonSerializer.Serialize(workflow), timeout.Token);
        var child = result.OfType<PiToolExecutionCompletedEvent>().Last(item => item.ToolName == "pistation_subagent");
        Assert.Contains("write", child.Result.GetRawText());
        Assert.False(File.Exists(Path.Combine(options.ProjectDirectory, "child-write.txt")));
    }
}
