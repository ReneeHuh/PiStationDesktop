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
}

public sealed class ConnectionStateChangedEventArgs(
    EnvironmentConnectionState state,
    Exception? error = null) : EventArgs
{
    public EnvironmentConnectionState State { get; } = state;

    public Exception? Error { get; } = error;
}
