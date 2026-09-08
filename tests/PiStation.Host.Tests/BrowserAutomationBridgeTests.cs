using System.Text.Json;
using PiStation.Host.Preview;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Tests;

public sealed class BrowserAutomationBridgeTests
{
    [Fact]
    public async Task ClaimsAreExclusiveConnectionScopedAndNotRedelivered()
    {
        using var directory = new HostTestDirectory();
        await using var bridge = new BrowserAutomationBridge(directory.Path);
        var thread = ThreadId.New();
        var lease = await bridge.OpenAsync(new(thread, BrowserAutomationAccess.Interact), "device", "connection", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.OpenAsync(new(thread, BrowserAutomationAccess.Interact), "other", "other", CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bridge.PollAsync(lease.Id, "other", "connection", CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bridge.PollAsync(lease.Id, "device", "other", CancellationToken.None));
        var request = WriteRequest(directory.Path, thread, lease.Id, "click");
        var first = await bridge.PollAsync(lease.Id, "device", "connection", CancellationToken.None);
        Assert.Equal(request.Id, first.Request?.Id);
        Assert.Null((await bridge.PollAsync(lease.Id, "device", "connection", CancellationToken.None)).Request);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bridge.CompleteAsync(lease.Id, request.Id, new(true), "other", "connection", CancellationToken.None));
        await bridge.CompleteAsync(lease.Id, request.Id, new(true, JsonSerializer.SerializeToElement(new { ok = true })), "device", "connection", CancellationToken.None);
        Assert.True(ReadResult(directory.Path, thread, request.Id).Success);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory.Path, thread.Value, "requests")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.CompleteAsync(lease.Id, request.Id, new(true), "device", "connection", CancellationToken.None));
    }

    [Theory]
    [InlineData("click")]
    [InlineData("navigate")]
    [InlineData("type")]
    [InlineData("press_key")]
    [InlineData("scroll")]
    public async Task InspectAccessRejectsEveryInteraction(string operation)
    {
        using var directory = new HostTestDirectory();
        await using var bridge = new BrowserAutomationBridge(directory.Path);
        var thread = ThreadId.New();
        var lease = await bridge.OpenAsync(new(thread, BrowserAutomationAccess.Inspect), "d", "c", CancellationToken.None);
        var request = WriteRequest(directory.Path, thread, lease.Id, operation);
        Assert.Null((await bridge.PollAsync(lease.Id, "d", "c", CancellationToken.None)).Request);
        Assert.False(ReadResult(directory.Path, thread, request.Id).Success);
    }

    [Fact]
    public async Task DisconnectAndNewPermissionInvalidateClaimedAndQueuedWork()
    {
        using var directory = new HostTestDirectory();
        await using var bridge = new BrowserAutomationBridge(directory.Path);
        using var disconnect = new CancellationTokenSource();
        var thread = ThreadId.New();
        var lease = await bridge.OpenAsync(new(thread, BrowserAutomationAccess.Interact), "d", "c", disconnect.Token);
        var active = WriteRequest(directory.Path, thread, lease.Id, "click");
        await bridge.PollAsync(lease.Id, "d", "c", CancellationToken.None);
        var queued = WriteRequest(directory.Path, thread, lease.Id, "click");
        disconnect.Cancel();
        var replacement = await bridge.OpenAsync(new(thread, BrowserAutomationAccess.Interact), "d", "new", CancellationToken.None);
        Assert.False(ReadResult(directory.Path, thread, active.Id).Success);
        Assert.Null((await bridge.PollAsync(replacement.Id, "d", "new", CancellationToken.None)).Request);
        Assert.False(ReadResult(directory.Path, thread, queued.Id).Success);
        await bridge.CloseAsync(replacement.Id, "d", "new");
        Assert.False(File.Exists(Path.Combine(directory.Path, thread.Value, "permission.json")));
    }

    [Fact]
    public async Task CancellationExpiryMalformedAndOversizedRequestsNeverDispatch()
    {
        using var directory = new HostTestDirectory();
        await using var bridge = new BrowserAutomationBridge(directory.Path);
        var thread = ThreadId.New();
        var lease = await bridge.OpenAsync(new(thread, BrowserAutomationAccess.Inspect), "d", "c", CancellationToken.None);
        var active = WriteRequest(directory.Path, thread, lease.Id, "wait");
        await bridge.PollAsync(lease.Id, "d", "c", CancellationToken.None);
        File.Delete(Path.Combine(directory.Path, thread.Value, "requests", active.Id + ".json"));
        Assert.Null((await bridge.PollAsync(lease.Id, "d", "c", CancellationToken.None)).ActiveRequestId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.CompleteAsync(lease.Id, active.Id, new(true), "d", "c", CancellationToken.None));
        var expired = WriteRequest(directory.Path, thread, lease.Id, "snapshot", DateTimeOffset.UtcNow.AddMinutes(-1));
        var future = WriteRequest(directory.Path, thread, lease.Id, "snapshot", DateTimeOffset.UtcNow.AddMinutes(1));
        var folder = Path.Combine(directory.Path, thread.Value, "requests");
        File.WriteAllText(Path.Combine(folder, "malformed.json"), "{");
        File.WriteAllText(Path.Combine(folder, "large.json"), new string('x', BrowserAutomationLimits.MaximumRequestBytes + 1));
        Assert.Null((await bridge.PollAsync(lease.Id, "d", "c", CancellationToken.None)).Request);
        Assert.False(ReadResult(directory.Path, thread, expired.Id).Success);
        Assert.False(ReadResult(directory.Path, thread, future.Id).Success);
        Assert.Empty(Directory.EnumerateFiles(folder));
    }

    [Fact]
    public async Task CrashMarkerPreventsReplayAndHeartbeatExpiresPermission()
    {
        using var directory = new HostTestDirectory();
        var thread = ThreadId.New();
        var folder = Directory.CreateDirectory(Path.Combine(directory.Path, thread.Value, "requests")).FullName;
        var request = WriteRequest(directory.Path, thread, "crashed", "click");
        File.WriteAllText(Path.Combine(folder, request.Id + ".claimed"), "");
        await using var bridge = new BrowserAutomationBridge(directory.Path);
        var lease = await bridge.OpenAsync(new(thread, BrowserAutomationAccess.Interact), "d", "c", CancellationToken.None);
        Assert.False(ReadResult(directory.Path, thread, request.Id).Success);
        using var permission = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Path, thread.Value, "permission.json")));
        Assert.True(permission.RootElement.GetProperty("expiresUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        await Task.Delay(TimeSpan.FromSeconds(5.1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bridge.PollAsync(lease.Id, "d", "c", CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(directory.Path, thread.Value, "permission.json")));
    }

    [Fact]
    public void ResultsRejectUnboundedDataAndInvalidImages()
    {
        var withoutImage = JsonSerializer.Serialize(new BrowserAutomationResult(true), ProtocolJsonContext.Default.BrowserAutomationResult);
        Assert.Null(JsonSerializer.Deserialize(withoutImage, ProtocolJsonContext.Default.BrowserAutomationResult)!.ScreenshotPng);
        Assert.Throws<ArgumentException>(() => BrowserAutomationBridge.ValidateResult(new(true, ScreenshotPng: new byte[9])));
        Assert.Throws<ArgumentException>(() => BrowserAutomationBridge.ValidateResult(new(true, ScreenshotPng: new byte[BrowserAutomationLimits.MaximumScreenshotBytes + 1])));
        Assert.Throws<ArgumentException>(() => BrowserAutomationBridge.ValidateResult(new(false, Error: new string('x', 2049))));
        Assert.Throws<ArgumentException>(() => BrowserAutomationBridge.ValidateResult(new(true, JsonSerializer.SerializeToElement(new string('x', BrowserAutomationLimits.MaximumDataBytes)))));
    }

    private static BrowserAutomationRequest WriteRequest(string root, ThreadId thread, string controller, string operation, DateTimeOffset? created = null)
    {
        var request = new BrowserAutomationRequest(Guid.NewGuid().ToString("D"), operation, JsonSerializer.SerializeToElement(new { selector = "button" }), created ?? DateTimeOffset.UtcNow, controller);
        File.WriteAllText(Path.Combine(root, thread.Value, "requests", request.Id + ".json"), JsonSerializer.Serialize(request, ProtocolJsonContext.Default.BrowserAutomationRequest));
        return request;
    }

    private static BrowserAutomationResult ReadResult(string root, ThreadId thread, string id) =>
        JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(root, thread.Value, "responses", id + ".json")), ProtocolJsonContext.Default.BrowserAutomationResult)!;
}
