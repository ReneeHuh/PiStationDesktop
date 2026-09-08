using PiStation.App.Composition;
using PiStation.ClientRuntime;
using PiStation.Protocol.Identifiers;

namespace PiStation.CommandSystem.Tests;

public sealed class DesktopLifecycleTests
{
    [Fact]
    public async Task ShutdownSuppressesFlushFailureAndStillReleasesEveryResource()
    {
        var released = new List<string>();
        await DesktopLifecycle.ShutdownAsync(
            _ => throw new NotSupportedException("arbitrary final flush failure"),
            [
                () => { released.Add("view-model"); throw new TimeoutException(); },
                () => { released.Add("client"); return Task.CompletedTask; },
                () => { released.Add("host"); return Task.CompletedTask; },
            ], TimeSpan.FromMilliseconds(20), flush: true);

        Assert.Equal(["view-model", "client", "host"], released);
    }

    [Fact]
    public void ProfileDecisionDistinguishesEveryRemoteProfileField()
    {
        var id = EnvironmentId.New();
        var clientId = ClientId.New();
        var same = new SavedRemoteEnvironment(id, "env", new Uri("https://host/"), "AA", "credential", clientId);
        Assert.False(DesktopLifecycle.ProfileChanged(same, same));
        Assert.True(DesktopLifecycle.ProfileChanged(same, same with { DeviceCredential = "new-credential" }));
        Assert.True(DesktopLifecycle.ProfileChanged(same, same with { Address = new Uri("https://other/") }));
        Assert.True(DesktopLifecycle.ProfileChanged(same, same with { CertificateFingerprint = "BB" }));
        Assert.True(DesktopLifecycle.ProfileChanged(same, same with { ClientId = ClientId.New() }));
    }

    [Fact]
    public async Task StopSharingAttemptsPersistenceAfterListenerFailure()
    {
        var persisted = false;
        await Assert.ThrowsAsync<AggregateException>(() => DesktopLifecycle.StopSharingAsync(
            () => throw new InvalidOperationException("listener"),
            () => { persisted = true; throw new IOException("settings"); }));
        Assert.True(persisted);
    }

    [Fact]
    public async Task StopSharingDisposesListenerBeforeReportingSettingsFailure()
    {
        var disposed = false;
        await Assert.ThrowsAsync<IOException>(() => DesktopLifecycle.StopSharingAsync(
            () => { disposed = true; return Task.CompletedTask; },
            () => throw new IOException("settings")));
        Assert.True(disposed);
    }

    [Fact]
    public async Task ReplacementCanSkipFlush()
    {
        var flushed = false;
        await DesktopLifecycle.ShutdownAsync(
            _ => { flushed = true; return Task.CompletedTask; },
            [], TimeSpan.FromSeconds(1), flush: false);
        Assert.False(flushed);
    }
}
