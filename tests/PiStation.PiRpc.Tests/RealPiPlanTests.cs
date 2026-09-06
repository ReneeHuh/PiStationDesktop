using System.Text.Json;
using System.Text.Json.Nodes;
using PiStation.PiRpc.Diagnostics;
using PiStation.PiRpc.Discovery;
using PiStation.PiRpc.Process;
using PiStation.PiRpc.Transport;

namespace PiStation.PiRpc.Tests;

public sealed class RealPiPlanTests
{
    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task PlanningBlocksMutationAndApprovedExecutionResumesWithPersistedProgress()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var project = directory.CreateDirectory("project");
        await File.WriteAllTextAsync(Path.Combine(project, "README.md"), "Read-only test project", timeout.Token);
        var options = new PiProcessLaunchOptions
        {
            Installation = await new PiLocator().LocateAsync(new PiLocatorOptions { ExplicitPiPath = Environment.GetEnvironmentVariable("PISTATION_PI_PATH") }, timeout.Token),
            ProjectDirectory = project, SessionDirectory = directory.CreateDirectory("sessions"), SessionId = Guid.NewGuid().ToString("N"),
            AdditionalArguments = ["--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "pistation-plan.ts"),
                "--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "plan-provider.ts"), "--provider", "pistation-plan-offline", "--model", "deterministic"],
            EnvironmentVariables = new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = directory.CreateDirectory("agent"), ["PISTATION_PLAN_STATE_PATH"] = directory.GetPath("plan.json") },
        };
        async Task<JsonElement> Action(PiRpcConnection connection, string action, long revision = 0, string? text = null) =>
            await connection.ManagePlanAsync(new JsonObject { ["action"] = action, ["expectedRevision"] = revision, ["text"] = text }, timeout.Token);
        async Task Prompt(PiRpcConnection connection, string text)
        {
            await connection.PromptAsync(text, timeout.Token);
            await FakePiTestHost.ReadUntilSettledAsync(connection, timeout.Token);
        }
        await using (var first = await PiProcessLauncher.StartAsync(options, timeout.Token))
        {
            Assert.Equal("off", (await Action(first.Connection, "inspect")).GetProperty("mode").GetString());
            await Action(first.Connection, "plan");
        }
        await using (var planning = await PiProcessLauncher.StartAsync(options, timeout.Token))
        {
            Assert.Equal("planning", (await Action(planning.Connection, "inspect")).GetProperty("mode").GetString());
            await Prompt(planning.Connection, "PLAN_TEST_READ");
            var state = await Action(planning.Connection, "inspect");
            Assert.Equal("ready", state.GetProperty("mode").GetString());
            Assert.Equal(2, state.GetProperty("steps").GetArrayLength());
            await Prompt(planning.Connection, "PLAN_TEST_MUTATE");
            Assert.False(File.Exists(Path.Combine(project, "blocked-write.txt")));
            Assert.False(File.Exists(Path.Combine(project, "blocked-shell.txt")));
            Assert.False(File.Exists(Path.Combine(project, "extension-mutation.txt")));
            var entries = await planning.Connection.GetEntriesAsync(cancellationToken: timeout.Token);
            Assert.Contains(entries.Entries, entry => entry.GetRawText().Contains("PiStation planning policy blocks", StringComparison.Ordinal));
            state = await Action(planning.Connection, "save", state.GetProperty("revision").GetInt64(), "Plan:\r1. Implement the change\r2. Verify the change");
            Assert.DoesNotContain("\r", state.GetProperty("text").GetString()!);
            await planning.Connection.PromptAsync("/plan-test-reload", timeout.Token);
            state = await Action(planning.Connection, "inspect");
            Assert.Equal("ready", state.GetProperty("mode").GetString());
            await Assert.ThrowsAsync<PiRpcCommandException>(() => Action(planning.Connection, "execute", state.GetProperty("revision").GetInt64() - 1));
            await Action(planning.Connection, "execute", state.GetProperty("revision").GetInt64());
            await Prompt(planning.Connection, "PLAN_TEST_EXECUTE");
            Assert.Equal("approved", await File.ReadAllTextAsync(Path.Combine(project, "approved-write.txt"), timeout.Token));
            Assert.Equal("Approved edit after reload", await File.ReadAllTextAsync(Path.Combine(project, "README.md"), timeout.Token));
            state = await Action(planning.Connection, "inspect");
            Assert.Equal("paused", state.GetProperty("mode").GetString());
            Assert.True(state.GetProperty("steps")[0].GetProperty("completed").GetBoolean());
        }
        await using (var resumed = await PiProcessLauncher.StartAsync(options, timeout.Token))
        {
            var state = await Action(resumed.Connection, "inspect");
            Assert.Equal("paused", state.GetProperty("mode").GetString());
            Assert.True(state.GetProperty("steps")[0].GetProperty("completed").GetBoolean());
            await Action(resumed.Connection, "execute", state.GetProperty("revision").GetInt64());
            await Prompt(resumed.Connection, "PLAN_TEST_FINISH");
            state = await Action(resumed.Connection, "inspect");
            Assert.Equal("completed", state.GetProperty("mode").GetString());
            Assert.All(state.GetProperty("steps").EnumerateArray(), step => Assert.True(step.GetProperty("completed").GetBoolean()));
            state = await Action(resumed.Connection, "save", state.GetProperty("revision").GetInt64(), "Plan:\n1. Another task");
            await Action(resumed.Connection, "execute", state.GetProperty("revision").GetInt64());
            // A process lost between approval and prompt must not retain execution permission.
        }
        await using var interrupted = await PiProcessLauncher.StartAsync(options, timeout.Token);
        var paused = await Action(interrupted.Connection, "inspect");
        Assert.Equal("paused", paused.GetProperty("mode").GetString());
        await Action(interrupted.Connection, "execute", paused.GetProperty("revision").GetInt64());
        await interrupted.Connection.PromptAsync("PLAN_TEST_WAIT", timeout.Token);
        await interrupted.Connection.StopAsync(timeout.Token);
        await FakePiTestHost.ReadUntilSettledAsync(interrupted.Connection, timeout.Token);
        Assert.Equal("paused", (await Action(interrupted.Connection, "inspect")).GetProperty("mode").GetString());
        var branch = await interrupted.Connection.GetEntriesAsync(cancellationToken: timeout.Token);
        var forkPoint = branch.Entries.First(entry => entry.GetProperty("type").GetString() == "message" && entry.GetProperty("message").GetProperty("role").GetString() == "user").GetProperty("id").GetString()!;
        Assert.False((await interrupted.Connection.ForkAsync(forkPoint, timeout.Token)).Cancelled);
        var forked = await Action(interrupted.Connection, "inspect");
        Assert.Equal((await interrupted.Connection.GetStateAsync(timeout.Token)).SessionId, forked.GetProperty("sessionId").GetString());
        Assert.NotEqual("executing", forked.GetProperty("mode").GetString());
        Assert.False((await interrupted.Connection.NewSessionAsync(timeout.Token)).Cancelled);
        Assert.Equal("off", (await Action(interrupted.Connection, "inspect")).GetProperty("mode").GetString());
    }
}
