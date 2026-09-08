using PiStation.Protocol.Identifiers;

namespace PiStation.ClientRuntime;

public sealed record ClientRuntimeOptions
{
    public required Uri HubAddress { get; init; }

    public required string BearerCredential { get; init; }

    public string? CertificateFingerprint { get; init; }

    public EnvironmentId? ExpectedEnvironmentId { get; init; }

    public ClientId ClientId { get; init; } = ClientId.New();

    /// <summary>Restores an owned transport before initial connection and SignalR reconnect.</summary>
    public Func<CancellationToken, Task>? EnsureTransportAsync { get; init; }

    public IReadOnlyList<TimeSpan> ReconnectDelays { get; init; } =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public double RetryJitter { get; init; } = 0.15;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(HubAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(BearerCredential);
        ArgumentNullException.ThrowIfNull(TimeProvider);
        if (ReconnectDelays.Count == 0 || ReconnectDelays.Any(delay => delay < TimeSpan.Zero) ||
            RetryJitter is < 0 or > 0.5 || double.IsNaN(RetryJitter))
            throw new ArgumentException("Retry delays must be nonnegative and jitter must be between zero and 0.5.");
        if (!HubAddress.IsAbsoluteUri || HubAddress.UserInfo.Length != 0 ||
            HubAddress.Query.Length != 0 || HubAddress.Fragment.Length != 0 ||
            (HubAddress.Scheme != Uri.UriSchemeHttps &&
             (HubAddress.Scheme != Uri.UriSchemeHttp || !HubAddress.IsLoopback)))
        {
            throw new ArgumentException("Use HTTPS for remote hosts and credential-free hub addresses. HTTP is permitted only on loopback.", nameof(HubAddress));
        }
        if (CertificateFingerprint is not null)
        {
            Protocol.Models.RemoteEndpoint.ValidateFingerprint(CertificateFingerprint);
            if (HubAddress.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("Certificate pinning requires HTTPS.", nameof(HubAddress));
        }
    }
}
