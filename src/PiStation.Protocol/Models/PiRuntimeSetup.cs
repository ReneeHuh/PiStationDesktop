namespace PiStation.Protocol.Models;

public sealed record PiExtensionConfiguration(bool DiscoverInstalled = false, IReadOnlyList<string>? Paths = null);
public sealed record PiRuntimeConfiguration(string? ExecutablePath, PiExtensionConfiguration Extensions);
public sealed record ConfigurePiRuntimeRequest(string? ExecutablePath, PiExtensionConfiguration? Extensions = null);
public sealed record PiRuntimeSetupResult(bool Available, string? ExecutablePath, string? Version, string Message,
    PiExtensionConfiguration? Extensions = null);
