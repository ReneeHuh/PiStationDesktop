using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using PiStation.Host.Updates;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class RemoteUpdateTests
{
    [Fact]
    public async Task ValidatedActivationIsDurableIdempotentAndWaitsForIdle()
    {
        using var folder = new TestFolder();
        var busy = true;
        var owner = new TestOwner();
        using var coordinator = new RemoteUpdateCoordinator(folder.Root, () => busy);
        coordinator.SetOwner(owner);
        var bytes = "verified package"u8.ToArray();
        var request = Request(bytes);
        Assert.Throws<InvalidOperationException>(() => coordinator.Prepare(request, "host-owner"));
        coordinator.Enabled = true;
        Assert.Equal(coordinator.Prepare(request, "host-owner"), coordinator.Prepare(request, "host-owner"));
        Assert.Throws<InvalidOperationException>(() => coordinator.Prepare(request, "another-device"));
        Assert.Throws<InvalidOperationException>(() => coordinator.Commit(new(request.RequestId), "host-owner"));
        await UploadAsync(coordinator, request.RequestId, bytes);
        Assert.Equal(RemoteUpdateState.Ready, coordinator.GetReceipt(request.RequestId)!.State);
        var pending = coordinator.Commit(new(request.RequestId), "host-owner");
        Assert.Equal(pending, coordinator.Commit(new(request.RequestId), "host-owner"));
        await Task.Delay(650, TestContextToken);
        Assert.False(owner.Activated.Task.IsCompleted);
        busy = false;
        var staged = await owner.Activated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContextToken);
        Assert.True(coordinator.IsDraining);
        Assert.Equal(RemoteUpdateState.Restarting, coordinator.Cancel(request.RequestId, "host-owner").State);
        RemoteUpdateCoordinator.CompleteActivation(staged.ReceiptPath, true, "Fixture owner confirmed readiness.");
        using var reopened = new RemoteUpdateCoordinator(folder.Root, () => false);
        Assert.Equal(RemoteUpdateState.Succeeded, reopened.GetReceipt(request.RequestId)!.State);
        Assert.Single(reopened.GetHistory("host-owner"));
        Assert.Empty(reopened.GetHistory("another-device"));
    }

    [Fact]
    public async Task CorruptUploadAndCanceledWaitingRequestNeverActivate()
    {
        using var folder = new TestFolder();
        var owner = new TestOwner();
        using var coordinator = new RemoteUpdateCoordinator(folder.Root, () => true) { Enabled = true };
        coordinator.SetOwner(owner);
        var bytes = "verified package"u8.ToArray();
        var bad = Request(bytes);
        coordinator.Prepare(bad, "host-owner");
        await UploadAsync(coordinator, bad.RequestId, "corrupt package!"u8.ToArray(), 400);
        Assert.Equal(RemoteUpdateState.Failed, coordinator.GetReceipt(bad.RequestId)!.State);
        var good = Request(bytes);
        coordinator.Prepare(good, "host-owner");
        await UploadAsync(coordinator, good.RequestId, bytes);
        Assert.Throws<UnauthorizedAccessException>(() => coordinator.Commit(new(good.RequestId), "another-device"));
        coordinator.Commit(new(good.RequestId), "host-owner");
        Assert.Equal(RemoteUpdateState.Canceled, coordinator.Cancel(good.RequestId, "host-owner").State);
        await Task.Delay(650, TestContextToken);
        Assert.False(owner.Activated.Task.IsCompleted);
        Assert.False(coordinator.IsDraining);
    }

    [Fact]
    public async Task OwnerRestartFailsAnUnconfirmedActivationWithoutChangingCompletedReceipts()
    {
        using var folder = new TestFolder();
        var owner = new TestOwner();
        var bytes = "verified package"u8.ToArray();
        var request = Request(bytes);
        using (var coordinator = new RemoteUpdateCoordinator(folder.Root, () => false) { Enabled = true })
        {
            coordinator.SetOwner(owner);
            coordinator.Prepare(request, "host-owner");
            await UploadAsync(coordinator, request.RequestId, bytes);
            coordinator.Commit(new(request.RequestId), "host-owner");
            await owner.Activated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContextToken);
        }
        RemoteUpdateCoordinator.FailInterruptedActivations(folder.Root);
        using var reopened = new RemoteUpdateCoordinator(folder.Root, () => false);
        Assert.Equal(RemoteUpdateState.Failed, reopened.GetReceipt(request.RequestId)!.State);
        var staged = await owner.Activated.Task;
        RemoteUpdateCoordinator.CompleteActivation(staged.ReceiptPath, true, "Fixture owner confirmed readiness.");
        RemoteUpdateCoordinator.FailInterruptedActivations(folder.Root);
        Assert.Equal(RemoteUpdateState.Succeeded, reopened.GetReceipt(request.RequestId)!.State);
    }

    private static CancellationToken TestContextToken => CancellationToken.None;
    private static PrepareRemoteUpdateRequest Request(byte[] bytes) => new(Guid.NewGuid(), "fixture.zip", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
    private static async Task UploadAsync(RemoteUpdateCoordinator coordinator, Guid id, byte[] bytes, int expectedStatus = 200)
    {
        var context = new DefaultHttpContext();
        context.Request.RouteValues["request"] = id.ToString();
        context.Request.ContentLength = bytes.Length;
        await using var body = new MemoryStream(bytes);
        await using var output = new MemoryStream();
        context.Request.Body = body;
        context.Response.Body = output;
        await coordinator.UploadAsync(context);
        Assert.Equal(expectedStatus, context.Response.StatusCode);
    }

    private sealed class TestOwner : IRemoteUpdateOwner
    {
        public string Kind => "fixture";
        public string CurrentVersion => "1.0.0";
        public string PackageKind => ".zip";
        public string Trust => "Fixture only";
        public TaskCompletionSource<StagedRemoteUpdate> Activated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> ValidateAsync(string packagePath, string runtimeDirectory, CancellationToken cancellationToken) => Task.FromResult("1.0.1");
        public Task ActivateAsync(StagedRemoteUpdate update, CancellationToken cancellationToken) { Activated.TrySetResult(update); return Task.CompletedTask; }
    }

    private sealed class TestFolder : IDisposable
    {
        private static readonly string Prefix = Path.Combine(Path.GetTempPath(), "PiStationUpdateTests");
        public string Root { get; } = Path.Combine(Prefix, Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Root) && Path.GetFullPath(Root).StartsWith(Prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(Root, recursive: true);
        }
    }
}
