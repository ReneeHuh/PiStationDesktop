namespace PiStation.ClientRuntime;

public sealed class EnvironmentConnectionException(EnvironmentConnectionState connectionState)
    : InvalidOperationException($"The environment client is not connected (state: {connectionState}).")
{
    public EnvironmentConnectionState ConnectionState { get; } = connectionState;
}
