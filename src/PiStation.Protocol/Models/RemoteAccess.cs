using PiStation.Protocol.Identifiers;
using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;

namespace PiStation.Protocol.Models;

public enum RemoteAccessLevel { ReadOnly, Operate }

public sealed record PairingRequest(string InvitationToken, string DeviceName, string DeviceCredential);
public sealed record PairingPollRequest(string RequestId, string DeviceCredential);
public sealed record PairingStatus(string RequestId, string State, EnvironmentId EnvironmentId, string EnvironmentName);
public sealed record PendingRemoteDevice(string RequestId, string DeviceName, RemoteAccessLevel AccessLevel, DateTimeOffset ExpiresAt,
    string? VerificationCode = null);
public sealed record RemoteDevice(string DeviceId, string DeviceName, RemoteAccessLevel AccessLevel, DateTimeOffset ExpiresAt,
    string? Subject = null);
public sealed record RemotePairingInvitation(string Id, string? Label, RemoteAccessLevel AccessLevel, DateTimeOffset ExpiresAt);
public sealed record IssuedRemotePairing(RemotePairingInvitation Invitation, string Token)
{
    public override string ToString() => Invitation.Id;
}
public sealed record IssuedRemoteSession(RemoteDevice Device, string Token)
{
    public override string ToString() => Device.DeviceId;
}

/// <summary>The fragment is handled locally and is never sent as part of an HTTP URL.</summary>
public sealed record RemoteInvitation(Uri Address, string CertificateFingerprint, string Token)
{
    public string Encode()
    {
        RemoteEndpoint.Validate(Address);
        RemoteEndpoint.ValidateFingerprint(CertificateFingerprint);
        return $"{Address.AbsoluteUri}#pistation={Uri.EscapeDataString(Token)}&certificate={CertificateFingerprint}";
    }

    public static RemoteInvitation Parse(string value)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            throw new ArgumentException("Enter a complete PiStation pairing link.", nameof(value));
        var parts = uri.Fragment.TrimStart('#').Split('&');
        if (parts.Length != 2 || !parts[0].StartsWith("pistation=", StringComparison.Ordinal) ||
            !parts[1].StartsWith("certificate=", StringComparison.Ordinal))
            throw new ArgumentException("The pairing link is missing its token or certificate identity.", nameof(value));
        var address = new UriBuilder(uri) { Fragment = string.Empty }.Uri;
        RemoteEndpoint.Validate(address);
        var fingerprint = parts[1][12..];
        RemoteEndpoint.ValidateFingerprint(fingerprint);
        var token = Uri.UnescapeDataString(parts[0][10..]);
        if (token.Length is < 32 or > 128)
            throw new ArgumentException("The pairing token is invalid.", nameof(value));
        return new(address, fingerprint.ToUpperInvariant(), token);
    }
}

/// <summary>Derives a short, non-secret code that both pairing participants can compare.</summary>
public static class PairingVerification
{
    public static string ComputeCode(string requestId, string deviceCredential)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        ArgumentException.ThrowIfNullOrEmpty(deviceCredential);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(requestId + ":" + deviceCredential));
        var value = BinaryPrimitives.ReadUInt32BigEndian(digest.AsSpan(0, 4)) % 1_000_000;
        return value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }
}

public static class RemoteEndpoint
{
    public static void Validate(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.IsAbsoluteUri || address.Scheme != Uri.UriSchemeHttps ||
            address.AbsolutePath != "/" || address.UserInfo.Length != 0 ||
            address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new ArgumentException("Remote addresses must be an HTTPS origin without credentials, a path, query, or fragment.", nameof(address));
    }

    public static void ValidateFingerprint(string fingerprint)
    {
        if (fingerprint.Length != 64 || !fingerprint.All(char.IsAsciiHexDigit))
            throw new ArgumentException("The certificate identity must be a SHA-256 fingerprint.", nameof(fingerprint));
    }
}
