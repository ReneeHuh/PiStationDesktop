using Microsoft.UI.Xaml;
using PiStation.App.Composition;
using PiStation.ClientRuntime;
using PiStation.Protocol.Models;

namespace PiStation.App.Views.Controls;

public sealed partial class RemoteConnectionsPanel
{
    private async void OnRefreshTailscale(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _pairing = cancellation;
        try
        {
            TailscaleStatus.Text = "Discovering Tailscale computers…";
            var network = await TailscaleDiscovery.DiscoverAsync(cancellation.Token);
            if (!_active) return;
            _tailscale = network;
            TailscaleStatus.Text = network.Message;
            var selected = (TailscalePeers.SelectedItem as TailscaleMachine)?.Address;
            TailscalePeers.ItemsSource = network.Peers;
            TailscalePeers.SelectedItem = network.Peers.FirstOrDefault(peer => peer.Address == selected);
            if (TailscalePeers.SelectedIndex < 0 && network.Peers.Count > 0) TailscalePeers.SelectedIndex = 0;
        }
        finally { _pairing = null; }
    });

    private async void OnUseTailscaleAdapter(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        if (HostController is not { IsSharing: false } || _tailscale?.Self is not { } self) return Task.CompletedTask;
        var addresses = RemoteNetworkAddress.Discover();
        var selected = addresses.FirstOrDefault(address => address.Address == self.Address)
            ?? throw new InvalidOperationException("The Tailscale adapter is no longer available. Refresh Tailscale and try again.");
        ListenAddress.ItemsSource = addresses;
        ListenAddress.SelectedItem = selected;
        Status.Text = "Tailscale adapter selected. Start sharing, then create a pairing link for the other computer.";
        return Task.CompletedTask;
    });

    private async void OnUseTailscalePeer(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        if (TailscalePeers.SelectedItem is not TailscaleMachine peer) throw new InvalidOperationException("Refresh Tailscale and choose a Windows computer first.");
        if (!peer.Online) throw new InvalidOperationException("This computer is offline in Tailscale. Connect it, then refresh.");
        var address = (TailscaleMagicDns.IsChecked == true ? peer : peer with { DnsName = null }).GetHttpsAddress(checked((int)TailscalePort.Value));
        if (!string.IsNullOrWhiteSpace(IncomingLink.Text))
        {
            var invitation = RemoteInvitation.Parse(IncomingLink.Text);
            IncomingLink.Text = (invitation with { Address = address }).Encode();
            Status.Text = "Tailscale address applied to the pairing link. Choose Pair device to connect and compare verification codes.";
        }
        else if (SavedEnvironments.SelectedItem is SavedRemoteEnvironment)
        {
            EndpointAddress.Text = address.AbsoluteUri;
            Status.Text = "Tailscale address selected. Choose Verify and save address to check the paired host before saving.";
        }
        else throw new InvalidOperationException("Paste the host's pairing link below or select a saved environment, then choose this address again.");
        return Task.CompletedTask;
    });
}
