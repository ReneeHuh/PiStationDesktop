using System.Text.Json.Nodes;
using PiStation.PiRpc.Discovery;
using PiStation.PiRpc.Process;
using PiStation.PiRpc.Sessions;

namespace PiStation.PiRpc.Tests;

public sealed class RealPiSessionLabelTests
{
    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task SdkLabelsPersistAndReplayWithoutChangingConversationAndCanBeRenamedAndCleared()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var options = new PiProcessLaunchOptions
        {
            Installation = await new PiLocator().LocateAsync(new PiLocatorOptions { ExplicitPiPath = Environment.GetEnvironmentVariable("PISTATION_PI_PATH") }, timeout.Token),
            ProjectDirectory = directory.CreateDirectory("project"), SessionDirectory = directory.CreateDirectory("sessions"), SessionId = Guid.NewGuid().ToString("N"),
            AdditionalArguments = ["--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "offline-provider.ts"),
                "--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "pistation-sessions.ts"), "--provider", "pistation-offline", "--model", "deterministic"],
            EnvironmentVariables = new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = directory.CreateDirectory("agent") },
        };
        string path, target, revision;
        JsonObject request;
        await using (var pi = await PiProcessLauncher.StartAsync(options, timeout.Token))
        {
            await pi.Connection.PromptAsync("Bookmark this turn", timeout.Token);
            await FakePiTestHost.ReadUntilSettledAsync(pi.Connection, timeout.Token);
            path = (await pi.Connection.GetStateAsync(timeout.Token)).SessionFile!;
            var document = await PiSessionDocument.ReadAsync(path, timeout.Token);
            target = document.LeafId!;
            request = Request(document, target, "Architecture");
            await pi.Connection.SetSessionLabelAsync(request, timeout.Token);
            var labeled = await PiSessionDocument.ReadAsync(path, timeout.Token);
            Assert.Equal("Architecture", labeled.Labels()[target].Label);
            Assert.Equal(document.Branch().Count(entry => entry["type"]?.ToString() == "message"), labeled.Branch().Count(entry => entry["type"]?.ToString() == "message"));
            Assert.Equal(document.Entries.Count + 2, labeled.Entries.Count);
            revision = labeled.Revision;
        }
        await using var resumed = await PiProcessLauncher.StartAsync(options, timeout.Token);
        await resumed.Connection.SetSessionLabelAsync(request, timeout.Token);
        var restored = await PiSessionDocument.ReadAsync(path, timeout.Token);
        Assert.Equal(revision, restored.Revision);
        await resumed.Connection.SetSessionLabelAsync(Request(restored, target, "Renamed"), timeout.Token);
        var renamed = await PiSessionDocument.ReadAsync(path, timeout.Token);
        Assert.Equal("Renamed", renamed.Labels()[target].Label);
        await resumed.Connection.SetSessionLabelAsync(request, timeout.Token);
        Assert.Equal(renamed.Revision, (await PiSessionDocument.ReadAsync(path, timeout.Token)).Revision);
        await resumed.Connection.SetSessionLabelAsync(Request(renamed, target, null), timeout.Token);
        Assert.Empty((await PiSessionDocument.ReadAsync(path, timeout.Token)).Labels());
        await resumed.Connection.PromptAsync("Continue after bookmark edits", timeout.Token);
        await FakePiTestHost.ReadUntilSettledAsync(resumed.Connection, timeout.Token);
        Assert.Equal(4, (await PiSessionDocument.ReadAsync(path, timeout.Token)).Branch().Count(entry => entry["type"]?.ToString() == "message"));
    }

    private static JsonObject Request(PiSessionDocument document, string entryId, string? label) => new()
    {
        ["action"] = "label", ["operationId"] = Guid.NewGuid().ToString("N"), ["requestHash"] = new string('A', 64),
        ["sessionId"] = document.SessionId, ["entryId"] = entryId, ["expectedRevision"] = document.Revision, ["label"] = label,
    };
}
