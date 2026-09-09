using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class TailscaleDiscoveryTests
{
    [Fact]
    public void DiscoveryUsesWindowsPeersAndKeepsOfflineAndInvalidEndpointsDistinct()
    {
        var network = TailscaleDiscovery.Parse("""
            { "BackendState": "Running", "Self": { "HostName": "local", "DNSName": "Local.Example.ts.net.", "TailscaleIPs": ["fd7a:115c:a1e0::1", "100.64.1.2"] },
              "Peer": {
                "a": { "HostName": "Remote", "OS": "windows", "Online": true, "DNSName": "remote.example.ts.net.", "TailscaleIPs": ["100.100.1.1"] },
                "b": { "HostName": "Linux", "OS": "linux", "Online": true, "TailscaleIPs": ["100.100.1.2"] },
                "c": { "HostName": "Sleeping", "OS": "windows", "Online": false, "DNSName": "https://wrong.invalid/", "TailscaleIPs": ["100.100.1.3"] },
                "d": { "HostName": "Not a peer address", "OS": "windows", "Online": true, "TailscaleIPs": ["192.168.1.2"] }
              } }
            """);
        Assert.True(network.Running);
        Assert.Equal("local.example.ts.net", network.Self!.DnsName);
        Assert.Equal(2, network.Peers.Count);
        Assert.True(network.Peers[0].Online);
        Assert.False(network.Peers[1].Online);
        Assert.Equal(new Uri("https://remote.example.ts.net:52740/"), network.Peers[0].GetHttpsAddress(52740));
        Assert.Equal(new Uri("https://100.100.1.3:52740/"), network.Peers[1].GetHttpsAddress(52740));
    }

    [Theory]
    [InlineData("NeedsLogin")]
    [InlineData("Stopped")]
    [InlineData("Starting")]
    public void DisconnectedStatusDoesNotExposeStalePeers(string state)
    {
        var network = TailscaleDiscovery.Parse("{\"BackendState\":\"" + state + "\",\"Self\":{\"TailscaleIPs\":[\"100.100.1.1\"]}}");
        Assert.False(network.Running);
        Assert.Null(network.Self);
        Assert.Empty(network.Peers);
    }

    [Theory]
    [InlineData("100.64.0.0", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.128.0.0", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("not-an-ip", false)]
    public void OnlyTailscaleIpv4RangeIsAccepted(string address, bool expected) =>
        Assert.Equal(expected, TailscaleDiscovery.IsTailscaleAddress(address));

    [Fact]
    public void DiscoveryDoesNotReplacePairingTrustWhenChangingToMagicDns()
    {
        var original = new RemoteInvitation(new("https://192.168.1.2:52740/"), new string('A', 64), new string('B', 64));
        var machine = new TailscaleMachine("Host", "host.example.ts.net", "100.64.1.1", true);
        var invitation = RemoteInvitation.Parse((original with { Address = machine.GetHttpsAddress(52740) }).Encode());
        Assert.Equal(original.CertificateFingerprint, invitation.CertificateFingerprint);
        Assert.Equal(original.Token, invitation.Token);
        Assert.Equal(machine.DnsName, invitation.Address.Host);
        Assert.Throws<ArgumentOutOfRangeException>(() => machine.GetHttpsAddress(443));
    }

    [Fact]
    public void MalformedOrOversizedStatusIsRejected()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => TailscaleDiscovery.Parse("[]"));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => TailscaleDiscovery.Parse("bad JSON"));
        Assert.Throws<InvalidDataException>(() => TailscaleDiscovery.Parse(new string(' ', 4 * 1024 * 1024 + 1)));
    }
}
