using PiStation.Protocol.Models;
using QRCoder;

namespace PiStation.App.Composition;

/// <summary>Secrets live only for this Settings visit; database rows contain metadata, never recoverable links.</summary>
internal sealed class RemotePairingPageState
{
    private readonly Dictionary<string, CreatedRemotePairing> _created = new(StringComparer.Ordinal);

    public int Generation { get; private set; }

    public bool Remember(CreatedRemotePairing pairing, int generation)
    {
        if (generation != Generation) return false;
        _created[pairing.Invitation.Id] = pairing;
        return true;
    }

    public void Synchronize(IReadOnlyList<RemotePairingInvitation> invitations, bool sharing, DateTimeOffset now)
    {
        if (!sharing) { Clear(); return; }
        var active = invitations.Where(item => item.ExpiresAt > now).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _created.Keys.Where(id => !active.Contains(id)).ToArray()) _created.Remove(id);
    }

    public string? GetUrl(string? id, DateTimeOffset now)
    {
        if (id is null || !_created.TryGetValue(id, out var pairing)) return null;
        if (pairing.Invitation.ExpiresAt > now) return pairing.Url;
        _created.Remove(id);
        return null;
    }

    public void Clear()
    {
        Generation++;
        _created.Clear();
    }

    public static TimeSpan LifetimeFromMinutes(double minutes)
    {
        if (!double.IsFinite(minutes) || minutes < 1 || minutes > 1440 || Math.Truncate(minutes) != minutes)
            throw new ArgumentException("Choose a whole number of minutes from 1 through 1440 (one day).");
        return TimeSpan.FromMinutes(minutes);
    }

    public static string? NormalizeLabel(string text)
    {
        if (text.Length > 80 || text.Any(char.IsControl))
            throw new ArgumentException("The pairing label must be at most 80 characters without control characters.");
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public static byte[] CreateQrPng(string url)
    {
        // Generate the exact pairing URL locally; no image service, temporary file or web navigation.
        _ = RemoteInvitation.Parse(url);
        using var data = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var qr = new PngByteQRCode(data);
        return qr.GetGraphic(4, drawQuietZones: true);
    }

    public static string AccessLabel(RemoteAccessLevel access) => access == RemoteAccessLevel.ReadOnly ? "Read only" : "Operate";
    public static string ExpiryLabel(DateTimeOffset expiry) => $"Expires {expiry.ToLocalTime():g}";
}

internal sealed record CreatedRemotePairing(RemotePairingInvitation Invitation, string Url)
{
    public override string ToString() => Invitation.Id;
}

internal sealed record RemoteInvitationRow(RemotePairingInvitation Invitation)
{
    public string Name => Invitation.Label ?? "Unlabelled pairing link";
    public string Summary => $"{RemotePairingPageState.AccessLabel(Invitation.AccessLevel)} · {RemotePairingPageState.ExpiryLabel(Invitation.ExpiresAt)}";
    public string Details => $"{Summary}\nInvitation ID: {Invitation.Id}";
}

internal sealed record RemoteSessionRow(RemoteDevice Device)
{
    public string Name => Device.DeviceName;
    public string Summary => $"{RemotePairingPageState.AccessLabel(Device.AccessLevel)} · {RemotePairingPageState.ExpiryLabel(Device.ExpiresAt)}";
    public string Details => $"{Summary}\nConnections: {Device.ActiveConnections}\nLast seen: {Device.LastSeenAt?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "Never"}\n" +
        $"Last connected: {Device.LastConnectedAt?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "Never"}\nSubject: {Device.Subject ?? "Not specified"}\nSession ID: {Device.DeviceId}";
}
