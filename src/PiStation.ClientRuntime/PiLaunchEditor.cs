using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

public static class PiLaunchEditor
{
    public static string FormatArguments(PiLaunchConfiguration launch) => string.Join('\n', launch.Arguments ?? []);
    public static string FormatEnvironment(PiLaunchConfiguration launch) => string.Join('\n',
        (launch.EnvironmentVariables ?? new Dictionary<string, string?>()).Select(pair =>
            pair.Value is null ? pair.Key : pair.Key + "=" + pair.Value));

    public static PiLaunchConfiguration Parse(string arguments, string environment, int commandTimeout, int shutdownTimeout,
        PiLaunchConfiguration? original = null)
    {
        var preserveEnvironment = original is not null && environment == FormatEnvironment(original);
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in preserveEnvironment ? [] : environment.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            var separator = line.IndexOf('=');
            var name = (separator < 0 ? line : line[..separator]).Trim();
            if (name.Length == 0 || name.Any(char.IsControl)) throw new ArgumentException("Enter NAME=value, or NAME to remove an inherited variable.");
            if (!variables.TryAdd(name, separator < 0 ? null : line[(separator + 1)..]))
                throw new ArgumentException($"Environment variable '{name}' is listed more than once.");
        }
        // Host configuration can include empty/multiline arguments or values that a line editor cannot recreate.
        // Keep their original representation when the corresponding text has not changed.
        return new(original is not null && arguments == FormatArguments(original) ? original.Arguments :
            arguments.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
            preserveEnvironment ? original!.EnvironmentVariables : variables, commandTimeout, shutdownTimeout);
    }
}
