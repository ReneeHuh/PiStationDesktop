namespace PiStation.Protocol.Models;

public sealed record PiExtensionConfiguration(bool DiscoverInstalled = false, IReadOnlyList<string>? Paths = null);
public sealed record PiLaunchConfiguration(IReadOnlyList<string>? Arguments = null,
    IReadOnlyDictionary<string, string?>? EnvironmentVariables = null, int CommandTimeoutSeconds = 30,
    int ShutdownTimeoutSeconds = 3, PiToolSelection? Tools = null, PiRuntimePreferences? Preferences = null);
public sealed record PiRuntimeConfiguration(string? ExecutablePath, PiExtensionConfiguration Extensions,
    PiLaunchConfiguration? Launch = null, string? Revision = null);
public sealed record ConfigurePiRuntimeRequest(string? ExecutablePath, PiExtensionConfiguration? Extensions = null,
    PiLaunchConfiguration? Launch = null, string? Revision = null);
public sealed record PiRuntimeSetupResult(bool Available, string? ExecutablePath, string? Version, string Message,
    PiExtensionConfiguration? Extensions = null, string? Revision = null);
