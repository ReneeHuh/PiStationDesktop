using PiStation.Protocol.Identifiers;

namespace PiStation.ClientRuntime;

public sealed record ClientRuntimeOptions
{
    public required Uri HubAddress { get; init; }

    public required string BearerCredential { get; init; }

    public ClientId ClientId { get; init; } = ClientId.New();

    public IReadOnlyList<TimeSpan> ReconnectDelays { get; init; } =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
    ];

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(HubAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(BearerCredential);
        if (!HubAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("The environment hub address must be absolute.", nameof(HubAddress));
        }
    }
}
