namespace PiStation.ClientRuntime;

public enum EnvironmentConnectionState
{
    Disconnected,
    Connecting,
    Authenticating,
    Synchronizing,
    Connected,
    Retrying,
    AuthenticationRequired,
    Incompatible,
    TrustRequired,
}

public sealed class ConnectionStateChangedEventArgs(
    EnvironmentConnectionState state,
    Exception? error = null,
    ConnectionDiagnostics? diagnostics = null) : EventArgs
{
    public EnvironmentConnectionState State { get; } = state;

    public Exception? Error { get; } = error;

    public ConnectionDiagnostics? Diagnostics { get; } = diagnostics;
}
