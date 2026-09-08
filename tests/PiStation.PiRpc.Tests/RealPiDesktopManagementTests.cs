using System.Text.Json.Nodes;
using PiStation.PiRpc.Discovery;
using PiStation.PiRpc.Process;
using PiStation.PiRpc.Wire.Events;

namespace PiStation.PiRpc.Tests;

public sealed class RealPiDesktopManagementTests
{
    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task ChildWriteRequiresItsOwnApprovalAfterParentWorkflowIsApproved()
    {
        using var directory = new TemporaryDirectory(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var project = directory.CreateDirectory("project");
        await using var process = await PiProcessLauncher.StartAsync(new PiProcessLaunchOptions
        {
            Installation = await new PiLocator().LocateAsync(new() { ExplicitPiPath = Environment.GetEnvironmentVariable("PISTATION_PI_PATH") }, timeout.Token),
            ProjectDirectory = project, SessionDirectory = directory.CreateDirectory("sessions"), SessionId = Guid.NewGuid().ToString("N"),
            AdditionalArguments = ["--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "pistation-resources.ts"), "--extension",
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "pistation-agents.ts"), "--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "agents-provider.ts"),
                "--provider", "pistation-agents-offline", "--model", "deterministic"],
            EnvironmentVariables = new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = directory.CreateDirectory("agent"),
                ["PISTATION_AGENT_ROOT"] = directory.CreateDirectory("children"), ["PISTATION_AGENT_SETTINGS"] = directory.GetPath("agent-settings.json") },
        }, timeout.Token);
        await process.Connection.ManageAsync(new() { ["action"] = "permission", ["mode"] = "supervised" }, timeout.Token);
        await process.Connection.PromptAsync("PISTATION_AGENT_WORKFLOW\n{\"mode\":\"single\",\"tasks\":[{\"agent\":\"worker\",\"task\":\"WRITE\"}]}", timeout.Token);
        var parentApproved = false; var childDeclined = false;
        await foreach (var item in process.Connection.ReadEventsAsync(timeout.Token))
        {
            if (item is PiConfirmRequestedEvent confirm)
            {
                var allow = confirm.Title.Contains("pistation_subagent", StringComparison.Ordinal);
                parentApproved |= allow;
                childDeclined |= confirm.Title.Contains("write", StringComparison.Ordinal) && !allow;
                await process.Connection.RespondToExtensionConfirmAsync(confirm.RequestId, allow, timeout.Token);
            }
            if (item is PiAgentSettledEvent) break;
        }
        Assert.True(parentApproved); Assert.True(childDeclined); Assert.False(File.Exists(Path.Combine(project, "child-write.txt")));
    }

    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task NativePermissionDenialAndRuntimeReadbacksUseRealPiWithoutNetwork()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var agent = directory.CreateDirectory("agent");
        await using var process = await PiProcessLauncher.StartAsync(new PiProcessLaunchOptions
        {
            Installation = await new PiLocator().LocateAsync(new() { ExplicitPiPath = Environment.GetEnvironmentVariable("PISTATION_PI_PATH") }, timeout.Token),
            ProjectDirectory = directory.CreateDirectory("project"), SessionDirectory = directory.CreateDirectory("sessions"), SessionId = Guid.NewGuid().ToString(),
            AdditionalArguments = ["--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "pistation-resources.ts"), "--extension",
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "offline-provider.ts"), "--provider", "pistation-offline", "--model", "deterministic"],
            EnvironmentVariables = new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = agent, ["PISTATION_TEST_TRACE"] = directory.GetPath("trace.jsonl") },
        }, timeout.Token);
        var capabilities = await process.Connection.ManageAsync(new() { ["action"] = "capabilities" }, timeout.Token);
        Assert.Equal("full-access", capabilities.GetProperty("permissionMode").GetString());
        Assert.Contains(capabilities.GetProperty("models").EnumerateArray(), model => model.GetProperty("providerId").GetString() == "pistation-offline");
        await process.Connection.SetAutoRetryAsync(false, timeout.Token);
        var effective = await process.Connection.ManageAsync(new() { ["action"] = "automation" }, timeout.Token);
        Assert.False(effective.GetProperty("autoRetry").GetBoolean());
        await process.Connection.ManageAsync(new() { ["action"] = "permission", ["mode"] = "supervised" }, timeout.Token);
        await process.Connection.PromptAsync("call probe tool", timeout.Token);
        var requested = false;
        var blocked = false;
        await foreach (var item in process.Connection.ReadEventsAsync(timeout.Token))
        {
            if (item is PiConfirmRequestedEvent confirm)
            {
                requested = true;
                await process.Connection.RespondToExtensionConfirmAsync(confirm.RequestId, false, timeout.Token);
            }
            if (item is PiToolExecutionCompletedEvent { ToolName: "pistation_probe", IsError: true }) blocked = true;
            if (item is PiAgentSettledEvent) break;
        }
        Assert.True(requested);
        Assert.True(blocked);
        await process.Connection.ManageAsync(new() { ["action"] = "permission", ["mode"] = "full-access" }, timeout.Token);
        await process.Connection.PromptAsync("call probe tool", timeout.Token);
        var allowed = await FakePiTestHost.ReadUntilSettledAsync(process.Connection, timeout.Token);
        Assert.Contains(allowed, item => item is PiToolExecutionCompletedEvent { ToolName: "pistation_probe", IsError: false });
        var package = directory.CreateDirectory("local-package");
        await File.WriteAllTextAsync(Path.Combine(package, "package.json"), "{\"name\":\"pistation-offline-package\",\"version\":\"1.0.0\",\"pi\":{\"extensions\":[]}}", timeout.Token);
        await process.Connection.ManageAsync(new() { ["action"] = "packageInstall", ["packageSource"] = package }, timeout.Token);
        var installed = await process.Connection.ManageAsync(new() { ["action"] = "inspect" }, timeout.Token);
        var configured = Assert.Single(installed.GetProperty("packages").EnumerateArray());
        Assert.Equal(package, configured.GetProperty("installedPath").GetString());
        var configuredSource = configured.GetProperty("source").GetString();
        await process.Connection.ManageAsync(new() { ["action"] = "packageUpdate", ["packageSource"] = configuredSource }, timeout.Token);
        await process.Connection.ManageAsync(new() { ["action"] = "packageRemove", ["packageSource"] = configuredSource }, timeout.Token);
        var removed = await process.Connection.ManageAsync(new() { ["action"] = "inspect" }, timeout.Token);
        Assert.Empty(removed.GetProperty("packages").EnumerateArray());
    }
}
