using PiStation.PiRpc.Discovery;
using PiStation.PiRpc.Transport;

namespace PiStation.PiRpc.Process;

public sealed record PiProcessLaunchOptions
{
    public required PiInstallation Installation { get; init; }

    public required string ProjectDirectory { get; init; }

    public required string SessionDirectory { get; init; }

    public required string SessionId { get; init; }

    public IReadOnlyList<string> AdditionalArguments { get; init; } = [];

    public bool DiscoverExtensions { get; init; }

    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    public PiRpcConnectionOptions ConnectionOptions { get; init; } = new();

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public int StandardErrorCharacterLimit { get; init; } = 64 * 1024;
}
