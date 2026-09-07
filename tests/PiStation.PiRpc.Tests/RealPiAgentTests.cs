using System.Text.Json;
using System.Text.Json.Nodes;
using PiStation.PiRpc.Discovery;
using PiStation.PiRpc.Process;
using PiStation.PiRpc.Transport;
using PiStation.PiRpc.Wire.Events;

namespace PiStation.PiRpc.Tests;

public sealed class RealPiAgentTests
{
    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task RealChildrenRunAllModesFailStopIndividuallyAndContinueSavedContext()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var project = directory.CreateDirectory("project");
        await File.WriteAllTextAsync(Path.Combine(project, "README.md"), "Child session evidence", timeout.Token);
        var options = new PiProcessLaunchOptions
        {
            Installation = await new PiLocator().LocateAsync(new PiLocatorOptions { ExplicitPiPath = Environment.GetEnvironmentVariable("PISTATION_PI_PATH") }, timeout.Token),
            ProjectDirectory = project, SessionDirectory = directory.CreateDirectory("sessions"), SessionId = Guid.NewGuid().ToString("N"),
            AdditionalArguments = ["--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "pistation-plan.ts"), "--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "pistation-agents.ts"),
                "--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "agents-provider.ts"), "--provider", "pistation-agents-offline", "--model", "deterministic"],
            EnvironmentVariables = new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = directory.CreateDirectory("agent"),
                ["PISTATION_AGENT_ROOT"] = directory.CreateDirectory("children"), ["PISTATION_AGENT_SETTINGS"] = directory.GetPath("agent-settings.json"), ["PISTATION_PLAN_STATE_PATH"] = directory.GetPath("plan.json") },
        };
        static string Prompt(object request) => "PISTATION_AGENT_WORKFLOW\n" + JsonSerializer.Serialize(request);
        async Task<JsonElement> Run(PiRpcConnection connection, object request)
        {
            var setup = await connection.ManageAgentsAsync(new JsonObject { ["action"] = "inspect" }, timeout.Token);
            await connection.ManageAgentsAsync(new JsonObject { ["action"] = "prepare", ["expectedRevision"] = setup.GetProperty("revision").GetString(), ["workflow"] = JsonSerializer.SerializeToNode(request) }, timeout.Token);
            await connection.PromptAsync("Run the requested agent workflow.", timeout.Token);
            var events = await FakePiTestHost.ReadUntilSettledAsync(connection, timeout.Token);
            return events.OfType<PiToolExecutionCompletedEvent>().Last(e => e.ToolName == "pistation_subagent").Result.GetProperty("details").Clone();
        }
        string resumeId;
        await using (var process = await PiProcessLauncher.StartAsync(options, timeout.Token))
        {
            var setup = await process.Connection.ManageAgentsAsync(new JsonObject { ["action"] = "inspect" }, timeout.Token);
            Assert.True(setup.GetProperty("available").GetBoolean());
            Assert.Equal(4, setup.GetProperty("presets").GetArrayLength());
            var custom = JsonNode.Parse(setup.GetProperty("presets")[0].GetRawText())!;
            custom["name"] = "custom";
            custom["systemPrompt"] = new string('語', 8192);
            var saved = await process.Connection.ManageAgentsAsync(new JsonObject { ["action"] = "save", ["expectedRevision"] = setup.GetProperty("revision").GetString(), ["preset"] = custom }, timeout.Token);
            await Assert.ThrowsAsync<PiStation.PiRpc.Diagnostics.PiRpcCommandException>(() => process.Connection.ManageAgentsAsync(new JsonObject { ["action"] = "disable", ["expectedRevision"] = setup.GetProperty("revision").GetString() }, timeout.Token));
            var disabled = await process.Connection.ManageAgentsAsync(new JsonObject { ["action"] = "disable", ["expectedRevision"] = saved.GetProperty("revision").GetString() }, timeout.Token);
            await Assert.ThrowsAsync<PiStation.PiRpc.Diagnostics.PiRpcCommandException>(() => Run(process.Connection, new { mode = "single", tasks = new[] { new { agent = "scout", task = "READ" } } }));
            await process.Connection.ManageAgentsAsync(new JsonObject { ["action"] = "enable", ["expectedRevision"] = disabled.GetProperty("revision").GetString() }, timeout.Token);
            var plan = await process.Connection.ManagePlanAsync(new JsonObject { ["action"] = "plan", ["expectedRevision"] = 0 }, timeout.Token);
            await process.Connection.PromptAsync(Prompt(new { mode = "single", tasks = new[] { new { agent = "worker", task = "READ" } } }), timeout.Token);
            var blocked = await FakePiTestHost.ReadUntilSettledAsync(process.Connection, timeout.Token);
            Assert.True(blocked.OfType<PiToolExecutionCompletedEvent>().Last().IsError);
            Assert.Empty(Directory.GetDirectories(directory.GetPath("children")));
            await process.Connection.ManagePlanAsync(new JsonObject { ["action"] = "off", ["expectedRevision"] = plan.GetProperty("revision").GetInt64() }, timeout.Token);
            var single = await Run(process.Connection, new { mode = "single", tasks = new[] { new { agent = "scout", task = "READ" } } });
            Assert.Equal("completed", single.GetProperty("results")[0].GetProperty("status").GetString());
            Assert.Contains("Child session evidence", single.GetProperty("results")[0].GetProperty("transcript").GetString());
            resumeId = single.GetProperty("results")[0].GetProperty("controlId").GetString()!;
            Assert.True(single.GetProperty("results")[0].GetProperty("canResume").GetBoolean());
            var chain = await Run(process.Connection, new { mode = "chain", tasks = new[] { new { agent = "scout", task = "FIRST" }, new { agent = "planner", task = "SECOND {previous}" } } });
            Assert.All(chain.GetProperty("results").EnumerateArray(), item => Assert.Equal("completed", item.GetProperty("status").GetString()));
            Assert.Contains("Result: FIRST", chain.GetProperty("results")[1].GetProperty("task").GetString());
            var failed = await Run(process.Connection, new { mode = "chain", tasks = new[] { new { agent = "scout", task = "FAIL" }, new { agent = "worker", task = "NEVER" } } });
            Assert.Equal("failed", failed.GetProperty("results")[0].GetProperty("status").GetString());
            Assert.Equal("interrupted", failed.GetProperty("results")[1].GetProperty("status").GetString());
            var parallel = await Run(process.Connection, new { mode = "parallel", tasks = new[] { new { agent = "scout", task = "ONE" }, new { agent = "reviewer", task = "TWO" } } });
            Assert.All(parallel.GetProperty("results").EnumerateArray(), item => Assert.Equal("completed", item.GetProperty("status").GetString()));
            var large = await Run(process.Connection, new { mode = "parallel", tasks = Enumerable.Range(0, 8).Select(_ => new { agent = "custom", task = new string('語', 8192) }).ToArray() });
            Assert.Equal(8, large.GetProperty("results").GetArrayLength());
            Assert.All(large.GetProperty("results").EnumerateArray(), item => Assert.Equal("completed", item.GetProperty("status").GetString()));
            await process.Connection.PromptAsync(Prompt(new { mode = "parallel", tasks = new[] { new { agent = "scout", task = "WAIT ONE" }, new { agent = "scout", task = "WAIT TWO" } } }), timeout.Token);
            string? childId = null;
            await foreach (var item in process.Connection.ReadEventsAsync(timeout.Token))
            {
                if (item is not PiToolExecutionUpdatedEvent update) continue;
                var children = update.PartialResult.GetProperty("details").GetProperty("results");
                if (children.EnumerateArray().All(child => child.GetProperty("transcript").GetString()!.Contains("Child session evidence")))
                { childId = children[0].GetProperty("controlId").GetString(); break; }
            }
            Assert.NotNull(childId);
            await process.Connection.ManageAgentsAsync(new JsonObject { ["action"] = "stop", ["controlId"] = childId }, timeout.Token);
            await foreach (var item in process.Connection.ReadEventsAsync(timeout.Token))
            {
                if (item is not PiToolExecutionUpdatedEvent update) continue;
                var children = update.PartialResult.GetProperty("details").GetProperty("results");
                if (children[0].GetProperty("status").GetString() == "interrupted")
                { Assert.Equal("running", children[1].GetProperty("status").GetString()); break; }
            }
            Assert.True((await process.Connection.GetStateAsync(timeout.Token)).IsStreaming);
            await process.Connection.StopAsync(timeout.Token);
            await FakePiTestHost.ReadUntilSettledAsync(process.Connection, timeout.Token);
        }
        await using var resumed = await PiProcessLauncher.StartAsync(options, timeout.Token);
        Assert.Contains((await resumed.Connection.ManageAgentsAsync(new JsonObject { ["action"] = "inspect" }, timeout.Token)).GetProperty("presets").EnumerateArray(), preset => preset.GetProperty("name").GetString() == "custom");
        var continued = await Run(resumed.Connection, new { mode = "single", tasks = new[] { new { agent = "scout", task = "CONTINUE" } }, resumeId });
        Assert.Equal("completed", continued.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Contains("Remembered the previous child result.", continued.GetProperty("results")[0].GetProperty("transcript").GetString());
        Assert.Equal(10, continued.GetProperty("results")[0].GetProperty("usage").GetProperty("input").GetInt32());
    }
}
