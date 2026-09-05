namespace PiStation.FakePi;

internal sealed record FakePiArguments(string Scenario, string SessionDirectory, string SessionId)
{
    public static FakePiArguments Parse(string[] arguments)
    {
        var scenario = ReadOption(arguments, "--fake-pi-scenario") ?? "normal";
        var sessionDirectory = ReadOption(arguments, "--session-dir") ??
            throw new ArgumentException("FakePi requires --session-dir.", nameof(arguments));
        var sessionId = ReadOption(arguments, "--session-id") ??
            throw new ArgumentException("FakePi requires --session-id.", nameof(arguments));
        return new FakePiArguments(scenario, Path.GetFullPath(sessionDirectory), sessionId);
    }

    private static string? ReadOption(string[] arguments, string option)
    {
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (string.Equals(arguments[index], option, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }
}
