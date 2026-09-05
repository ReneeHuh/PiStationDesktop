namespace PiStation.PiRpc.Discovery;

public enum PiInstallationKind
{
    NativeExecutable,
    NodePackage,
}

public sealed record PiInstallation(
    PiInstallationKind Kind,
    string ExecutablePath,
    IReadOnlyList<string> LaunchPrefixArguments,
    SemanticVersion PiVersion,
    SemanticVersion? NodeVersion,
    string? PackageRoot,
    string Source);
