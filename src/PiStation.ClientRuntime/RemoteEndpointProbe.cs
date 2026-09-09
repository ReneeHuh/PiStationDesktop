using System.Text.Json;
using PiStation.Protocol;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime;

public static class RemoteEndpointProbe
{
    public static async Task VerifyAsync(Uri address, string fingerprint, EnvironmentId expectedEnvironment,
        CancellationToken cancellationToken = default)
    {
        RemoteEndpoint.Validate(address);
        RemoteEndpoint.ValidateFingerprint(fingerprint);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var http = new HttpClient(RemoteTransport.CreateHandler(fingerprint));
        // No credentials, redirects, or unbounded response bodies during discovery.
        using var response = await http.GetAsync(new Uri(address, RemoteEndpointIdentity.Path),
            HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        var bytes = new byte[4097];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), deadline.Token).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (count > 4096) throw new InvalidDataException("The remote identity response is too large.");
        var identity = JsonSerializer.Deserialize(bytes.AsSpan(0, count), ProtocolJsonContext.Default.RemoteEndpointIdentity);
        if (identity?.EnvironmentId != expectedEnvironment)
            throw new InvalidDataException("This endpoint belongs to a different PiStation environment.");
        if (identity.ProtocolVersion != ProtocolVersion.Current)
            throw new InvalidDataException("Update PiStation manually on both computers before connecting.");
    }
}
