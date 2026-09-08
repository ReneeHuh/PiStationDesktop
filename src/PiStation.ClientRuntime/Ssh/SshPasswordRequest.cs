namespace PiStation.ClientRuntime.Ssh;

public sealed record SshPasswordRequest(string Target, int Attempt);

internal sealed class SshAuthenticationException(string message) : InvalidOperationException(message);
