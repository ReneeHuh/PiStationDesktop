using Microsoft.AspNetCore.SignalR;

namespace PiStation.Host.Hosting;

public static class EnvironmentTransportLimits
{
    // A 1 MiB UTF-8 file can require six JSON bytes per content byte, plus the RPC envelope.
    public const long MaximumHubMessageBytes = 8 * 1024 * 1024;

    public static void Configure(HubOptions options) => options.MaximumReceiveMessageSize = MaximumHubMessageBytes;
}
