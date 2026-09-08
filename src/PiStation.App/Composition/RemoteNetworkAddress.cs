using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PiStation.App.Composition;

internal sealed record RemoteNetworkAddress(string Address, string Label)
{
    public override string ToString() => Label;

    public static IReadOnlyList<RemoteNetworkAddress> Discover() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses
            .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(item.Address))
            .Select(item => new RemoteNetworkAddress(item.Address.ToString(),
                $"{item.Address} — {adapter.Name} ({adapter.NetworkInterfaceType}" +
                (item.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal) ? ", link-local" : string.Empty) + ")")))
        .DistinctBy(item => item.Address)
        .ToArray();
}
