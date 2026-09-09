using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

// Public, non-secret identity for checking a pinned endpoint before pairing.
public sealed record RemoteEndpointIdentity(EnvironmentId EnvironmentId, int ProtocolVersion)
{
    public const string Path = "/.well-known/pistation/environment";
}
