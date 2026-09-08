using PiStation.Host.Preview;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class PreviewLeaseTests
{
    [Fact]
    public void LeasesAreConnectionScopedBoundedAndRevokedOnDisconnect()
    {
        using var registry = new PreviewLeaseRegistry();
        using var disconnected = new CancellationTokenSource();
        var request = new OpenPreviewRequest(ProjectId.New(), new Uri("http://localhost:5173/"));
        var first = registry.Open(request, "device", "connection", disconnected.Token);
        Assert.Throws<UnauthorizedAccessException>(() => registry.Renew(first.Id, "wrong-device", "connection"));
        Assert.Throws<UnauthorizedAccessException>(() => registry.Renew(first.Id, "device", "wrong-connection"));
        registry.Close(first.Id, "wrong-device");
        Assert.Equal(first.Id, registry.Renew(first.Id, "device", "connection").Id);
        for (var i = 1; i < 12; i++) registry.Open(request, "device", "connection", disconnected.Token);
        Assert.Throws<InvalidOperationException>(() => registry.Open(request, "device", "connection", disconnected.Token));
        disconnected.Cancel();
        Assert.Throws<UnauthorizedAccessException>(() => registry.Renew(first.Id, "device", "connection"));
        var replacement = registry.Open(request, "device", "new-connection", CancellationToken.None);
        registry.Close(replacement.Id, "device");
        Assert.Throws<UnauthorizedAccessException>(() => registry.Renew(replacement.Id, "device", "new-connection"));
    }

    [Fact]
    public void TargetsCannotReachControlPortsOrArbitraryDestinations()
    {
        using var registry = new PreviewLeaseRegistry();
        registry.RegisterControlPort(this, 52740);
        foreach (var address in new[] { "http://localhost:52740/", "https://example.com/", "file:///C:/private", "http://user:secret@localhost:5173/" })
            Assert.Throws<ArgumentException>(() => registry.Open(new(ProjectId.New(), new Uri(address)), "device", "connection", CancellationToken.None));
    }
}
