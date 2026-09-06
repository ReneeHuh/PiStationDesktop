using PiStation.PiRpc.Diagnostics;

namespace PiStation.PiRpc.Tests;

public sealed class PiManagementTests
{
    [Fact]
    public async Task ManagementResponseDoesNotCreateSessionMessagesAndRejectsStaleChanges()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var process = await FakePiTestHost.StartAsync(directory, "resource-management", cancellationToken: timeout.Token);
        var snapshot = await process.Connection.ManageAsync(new() { ["action"] = "inspect" }, timeout.Token);
        var resource = Assert.Single(snapshot.GetProperty("resources").EnumerateArray());
        var request = new System.Text.Json.Nodes.JsonObject { ["action"] = "toggle",
            ["resourceId"] = resource.GetProperty("id").GetString(), ["enabled"] = false, ["revision"] = resource.GetProperty("revision").GetString() };
        await process.Connection.ManageAsync(request, timeout.Token);
        await Assert.ThrowsAsync<PiRpcCommandException>(() => process.Connection.ManageAsync(request, timeout.Token));
        Assert.Empty((await process.Connection.GetEntriesAsync(cancellationToken: timeout.Token)).Entries);
    }

    [Fact]
    public async Task ProcessExitInterruptsAnOutstandingManagementRequest()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var process = await FakePiTestHost.StartAsync(directory, "resource-management-crash", cancellationToken: timeout.Token);
        await Assert.ThrowsAnyAsync<PiRpcException>(() => process.Connection.ManageAsync(new() { ["action"] = "inspect" }, timeout.Token));
    }

    [Fact]
    public async Task MissingManagementExtensionFailsWithoutSendingAPrompt()
    {
        using var directory = new TemporaryDirectory();
        await using var process = await FakePiTestHost.StartAsync(directory);
        await Assert.ThrowsAsync<PiRpcCommandException>(() => process.Connection.ManageAsync(new() { ["action"] = "inspect" }));
        Assert.DoesNotContain("\"command\":\"prompt\"", await File.ReadAllTextAsync(directory.GetPath("sessions/command-log.jsonl")));
    }
}
