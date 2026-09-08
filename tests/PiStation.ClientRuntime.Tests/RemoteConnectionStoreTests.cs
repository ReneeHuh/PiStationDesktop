using System.Text;
using PiStation.Protocol.Identifiers;

namespace PiStation.ClientRuntime.Tests;

public sealed class RemoteConnectionStoreTests
{
    [Fact]
    public void SavedConnectionsAreProtectedRestorableAndCanBeForgotten()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        var path = Path.Combine(directory.CreateDirectory("connections"), "remote.protected");
        var environment = new SavedRemoteEnvironment(EnvironmentId.New(), "Desktop", new Uri("https://192.168.1.20:52740/"),
            new string('A', 64), new string('B', 64), ClientId.New());
        new RemoteConnectionStore(path).Save(environment);
        var persisted = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        Assert.DoesNotContain(environment.DeviceCredential, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(environment.Address.AbsoluteUri, persisted, StringComparison.Ordinal);
        var reopened = new RemoteConnectionStore(path);
        Assert.Equal(environment, Assert.Single(reopened.Load()));
        reopened.Save(environment with { Name = "Renamed" });
        Assert.Equal("Renamed", Assert.Single(reopened.Load()).Name);
        reopened.Forget(environment.EnvironmentId);
        Assert.Empty(new RemoteConnectionStore(path).Load());
    }

    [Theory]
    [InlineData("http://192.168.1.20/environment")]
    [InlineData("https://name:secret@example.com/environment")]
    [InlineData("https://example.com/environment?token=secret")]
    public void ClientRejectsUnsafeEndpointsBeforeSendingCredentials(string address)
    {
        Assert.Throws<ArgumentException>(() => new EnvironmentClient(new() { HubAddress = new Uri(address), BearerCredential = "secret" }));
    }
}
