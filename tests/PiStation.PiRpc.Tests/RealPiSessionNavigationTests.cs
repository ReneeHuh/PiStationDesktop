using System.Text.Json.Nodes;
using PiStation.PiRpc.Discovery;
using PiStation.PiRpc.Process;
using PiStation.PiRpc.Sessions;

namespace PiStation.PiRpc.Tests;

public sealed class RealPiSessionNavigationTests
{
    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task SdkNavigationPersistsBranchWithoutATurnAndContinuesWithOnlySelectedContext()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var options = new PiProcessLaunchOptions
        {
            Installation = await new PiLocator().LocateAsync(new PiLocatorOptions { ExplicitPiPath = Environment.GetEnvironmentVariable("PISTATION_PI_PATH") }, timeout.Token),
            ProjectDirectory = directory.CreateDirectory("project"), SessionDirectory = directory.CreateDirectory("sessions"), SessionId = Guid.NewGuid().ToString("N"),
            AdditionalArguments = ["--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "offline-provider.ts"),
                "--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "pistation-sessions.ts"), "--provider", "pistation-offline", "--model", "deterministic"],
            EnvironmentVariables = new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = directory.CreateDirectory("agent"), ["PISTATION_TEST_TRACE"] = directory.GetPath("trace.jsonl") },
        };
        string path;
        string first;
        JsonObject request;
        await using (var pi = await PiProcessLauncher.StartAsync(options, timeout.Token))
        {
            await pi.Connection.PromptAsync("First conversation turn", timeout.Token);
            await FakePiTestHost.ReadUntilSettledAsync(pi.Connection, timeout.Token);
            first = (await pi.Connection.GetEntriesAsync(cancellationToken: timeout.Token)).LeafId!;
            await pi.Connection.PromptAsync("Second abandoned conversation turn", timeout.Token);
            await FakePiTestHost.ReadUntilSettledAsync(pi.Connection, timeout.Token);
            path = (await pi.Connection.GetStateAsync(timeout.Token)).SessionFile!;
            var document = await PiSessionDocument.ReadAsync(path, timeout.Token);
            request = Request(document, first);
            Assert.False((await pi.Connection.NavigateSessionAsync(request, timeout.Token)).GetProperty("cancelled").GetBoolean());
            var switched = await PiSessionDocument.ReadAsync(path, timeout.Token);
            Assert.Equal(first, switched.Entries[^1]["parentId"]!.ToString());
            Assert.Equal(document.Entries.Count + 1, switched.Entries.Count);
            await pi.Connection.NavigateSessionAsync(request, timeout.Token);
            Assert.Equal(switched.Revision, (await PiSessionDocument.ReadAsync(path, timeout.Token)).Revision);
        }
        await using (var resumed = await PiProcessLauncher.StartAsync(options, timeout.Token))
        {
            var restored = await PiSessionDocument.ReadAsync(path, timeout.Token);
            Assert.DoesNotContain(restored.Branch(), entry => entry.ToJsonString().Contains("Second abandoned", StringComparison.Ordinal));
            await resumed.Connection.PromptAsync("Continue selected branch", timeout.Token);
            await FakePiTestHost.ReadUntilSettledAsync(resumed.Connection, timeout.Token);
            var lastContext = (await File.ReadAllLinesAsync(directory.GetPath("trace.jsonl"), timeout.Token))[^1];
            Assert.Contains("First conversation turn", lastContext);
            Assert.DoesNotContain("Second abandoned", lastContext);
            var document = await PiSessionDocument.ReadAsync(path, timeout.Token);
            var summary = Request(document, first);
            summary["summarize"] = true;
            summary["customInstructions"] = "Keep the important context.";
            Assert.False((await resumed.Connection.NavigateSessionAsync(summary, timeout.Token)).GetProperty("cancelled").GetBoolean());
            var summarized = await PiSessionDocument.ReadAsync(path, timeout.Token);
            Assert.Contains(summarized.Branch(), entry => entry["type"]?.ToString() == "branch_summary");
            var user = summarized.Entries.First(entry => entry["message"]?["role"]?.ToString() == "user");
            var returned = await resumed.Connection.NavigateSessionAsync(Request(summarized, user["id"]!.ToString()), timeout.Token);
            Assert.Equal("First conversation turn", returned.GetProperty("editorText").GetString());
            Assert.DoesNotContain((await PiSessionDocument.ReadAsync(path, timeout.Token)).Branch(), entry => entry["type"]?.ToString() == "message");
        }
    }

    private static JsonObject Request(PiSessionDocument document, string entryId) => new()
    {
        ["operationId"] = Guid.NewGuid().ToString("N"), ["requestHash"] = new string('A', 64), ["sessionId"] = document.SessionId,
        ["entryId"] = entryId, ["expectedRevision"] = document.Revision, ["summarize"] = false,
    };
}
