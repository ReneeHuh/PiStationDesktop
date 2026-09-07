using System.Net.Http.Json;
using System.Security.Cryptography;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime;

public sealed record SavedRemoteEnvironment(EnvironmentId EnvironmentId, string Name, Uri Address,
    string CertificateFingerprint, string DeviceCredential, ClientId ClientId)
{
    // UI/accessibility fallbacks must never stringify a saved bearer credential.
    public override string ToString() => Name;

    public ClientRuntimeOptions CreateOptions() => new()
    {
        HubAddress = new Uri(Address, "/environment"), BearerCredential = DeviceCredential,
        CertificateFingerprint = CertificateFingerprint, ExpectedEnvironmentId = EnvironmentId, ClientId = ClientId,
    };
}

public static class RemotePairingClient
{
    public static async Task<SavedRemoteEnvironment> PairAsync(RemoteInvitation invitation, string deviceName,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        try { return await PairCoreAsync(invitation, deviceName, progress, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Pairing could not complete in time. Check that the host is reachable, then try a fresh link."); }
    }

    private static async Task<SavedRemoteEnvironment> PairCoreAsync(RemoteInvitation invitation, string deviceName,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        RemoteEndpoint.Validate(invitation.Address);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Allow the host's short approved-result grace to cover a poll crossing the
        // pending deadline, while still bounding an abandoned pairing attempt.
        deadline.CancelAfter(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(60)));
        using var http = new HttpClient(RemoteTransport.CreateHandler(invitation.CertificateFingerprint))
        { BaseAddress = invitation.Address, Timeout = TimeSpan.FromSeconds(15) };
        var credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var response = await http.PostAsJsonAsync("remote/pair", new PairingRequest(invitation.Token, deviceName, credential),
            ProtocolJsonContext.Default.PairingRequest, deadline.Token).ConfigureAwait(false);
        EnsurePairingSuccess(response, isPoll: false);
        var state = await response.Content.ReadFromJsonAsync(ProtocolJsonContext.Default.PairingStatus, deadline.Token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The host returned an invalid pairing response.");
        var initial = state;
        var verificationCode = PairingVerification.ComputeCode(initial.RequestId, credential);
        progress?.Report($"Verification code: {verificationCode}. Compare it with the code shown on the host, then open Connections to approve this device. Waiting for approval on {state.EnvironmentName}.");
        while (state.State == "pending")
        {
            await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token).ConfigureAwait(false);
            using var poll = await http.PostAsJsonAsync("remote/pair/status", new PairingPollRequest(initial.RequestId, credential),
                ProtocolJsonContext.Default.PairingPollRequest, deadline.Token).ConfigureAwait(false);
            EnsurePairingSuccess(poll, isPoll: true);
            state = await poll.Content.ReadFromJsonAsync(ProtocolJsonContext.Default.PairingStatus, deadline.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The host returned an invalid pairing response.");
            if (state.EnvironmentId != initial.EnvironmentId || state.RequestId != initial.RequestId)
                throw new InvalidOperationException("The host identity changed during pairing.");
        }
        if (state.State != "approved") throw new InvalidOperationException("The host declined or expired this pairing request.");
        return new(state.EnvironmentId, state.EnvironmentName, invitation.Address, invitation.CertificateFingerprint, credential, ClientId.New());
    }

    private static void EnsurePairingSuccess(HttpResponseMessage response, bool isPoll)
    {
        if (response.IsSuccessStatusCode) return;
        var message = response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized when isPoll => "The pairing request expired or was rejected by the host.",
            System.Net.HttpStatusCode.Unauthorized => "The pairing link is invalid, expired, or already used.",
            System.Net.HttpStatusCode.Conflict => "The host has reached its pairing/device limit. Wait for pending requests to expire or revoke an approved device, then try again.",
            (System.Net.HttpStatusCode)429 => "Too many pairing attempts. Wait a moment and try again.",
            _ => "The host could not start pairing. Check the link and try again."
        };
        throw new InvalidOperationException(message);
    }
}
