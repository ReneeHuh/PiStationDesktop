namespace PiStation.PiRpc.Transport;

public sealed record PiRpcConnectionOptions
{
    public TimeSpan DefaultCommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan LongRunningCommandTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public int MaximumRecordBytes { get; init; } = 16 * 1024 * 1024;

    public long MaximumPromptImageBytes { get; init; } = 10L * 1024 * 1024;

    public Action<string>? DiagnosticSink { get; init; }
}
