namespace PiStation.ClientRuntime;

public enum ConnectionFailure { None, Network, Timeout, Authentication, Certificate, Identity, Protocol }

// Contains no credentials, endpoint queries, raw exception text, or user content.
public sealed record ConnectionDiagnostics(
    EnvironmentConnectionState State,
    ConnectionFailure Failure = ConnectionFailure.None,
    int Attempt = 0,
    DateTimeOffset? NextRetry = null,
    DateTimeOffset? LastConnected = null);

public sealed class ConnectionValidationException(ConnectionFailure failure, string message) : InvalidOperationException(message)
{
    public ConnectionFailure Failure { get; } = failure;
}
