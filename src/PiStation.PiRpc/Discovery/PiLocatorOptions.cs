namespace PiStation.PiRpc.Discovery;

public sealed record PiLocatorOptions
{
    public string? ExplicitPiPath { get; init; }

    public string? ExplicitNodePath { get; init; }

    public IReadOnlyList<string> CandidatePiPaths { get; init; } = [];

    public string? SearchPath { get; init; } = Environment.GetEnvironmentVariable("PATH");

    public SemanticVersion MinimumPiVersion { get; init; } = new(0, 84, 4);
}
