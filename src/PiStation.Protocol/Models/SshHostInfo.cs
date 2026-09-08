using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

/// <summary>Sent only over authenticated SSH or a current-user local pipe; never log this record.</summary>
public sealed record SshHostInfo(int BootstrapVersion, int ProtocolVersion, EnvironmentId EnvironmentId,
    string Name, int Port, string CertificateFingerprint, string BearerCredential, bool StartedByConnection,
    string? ServerVersion = null, string HostKind = "server",
    Uri? PairingAddress = null, string? PairingCertificateFingerprint = null)
{
    public const int CurrentBootstrapVersion = 1;
    public override string ToString() => $"{Name} ({EnvironmentId})";

    public void Validate(bool requireCompatibleVersion = true)
    {
        if (requireCompatibleVersion && (BootstrapVersion != CurrentBootstrapVersion || ProtocolVersion != Protocol.ProtocolVersion.Current))
            throw new InvalidOperationException($"The PiStation SSH host version is incompatible (host {ServerVersion ?? "unknown"}, protocol {ProtocolVersion}; client {ProductVersion.Current}, protocol {Protocol.ProtocolVersion.Current}). Update the host desktop, or stop the separately running server and reconnect using the bundled host. The running host was not stopped.");
        ArgumentException.ThrowIfNullOrWhiteSpace(EnvironmentId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentOutOfRangeException.ThrowIfLessThan(Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Port, 65535);
        RemoteEndpoint.ValidateFingerprint(CertificateFingerprint);
        if (PairingAddress is not null)
        {
            RemoteEndpoint.Validate(PairingAddress);
            RemoteEndpoint.ValidateFingerprint(PairingCertificateFingerprint ?? string.Empty);
        }
        if (BearerCredential.Length != 64 || !BearerCredential.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid SSH host credential.");
    }
}
