namespace PiStation.Host.SourceControl;

internal static class HostingCliLocator
{
    public static string Resolve(string executable)
    {
        if (!OperatingSystem.IsWindows() || executable != "gh") return executable;
        return FindGitHubCli(Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), File.Exists) ?? executable;
    }

    internal static string? FindGitHubCli(string? processPath, string? userPath, string? machinePath,
        string programFiles, Func<string, bool> exists)
    {
        // Windows app activation can retain the PATH from before the CLI was installed.
        // Search only configured absolute directories; never resolve a tool from a repository's current directory.
        var directories = new[] { processPath, userPath, machinePath }.Where(value => value is not null)
            .SelectMany(value => value!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(value => Environment.ExpandEnvironmentVariables(value.Trim('"')))
            .Append(Path.Combine(programFiles, "GitHub CLI")).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            if (!Path.IsPathFullyQualified(directory)) continue;
            var path = Path.Combine(directory, "gh.exe");
            if (exists(path)) return path;
        }
        return null;
    }
}
