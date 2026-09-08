using System.Globalization;
using System.Text;

namespace PiStation.ClientRuntime.Ssh;

public sealed record DiscoveredSshHost(string Target, string Source, int? Port = null)
{
    public override string ToString() => Port is { } port ? $"{Target} · port {port} ({Source})" : $"{Target} ({Source})";
}

public static class SshHostDiscovery
{
    public static async Task<IReadOnlyList<DiscoveredSshHost>> DiscoverAsync(
        string? userDirectory = null, CancellationToken cancellationToken = default)
    {
        userDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sshDirectory = Path.Combine(userDirectory, ".ssh");
        var found = new Dictionary<string, DiscoveredSshHost>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await ReadConfigAsync(Path.Combine(sshDirectory, "config")).ConfigureAwait(false);
        var knownHosts = await ReadSmallFileAsync(Path.Combine(sshDirectory, "known_hosts"), cancellationToken).ConfigureAwait(false);
        foreach (var host in ParseKnownHosts(knownHosts))
            found.TryAdd(host.Port is null ? host.Target : $"{host.Target}:{host.Port}", host);
        return found.Values.OrderBy(static item => item.Target, StringComparer.OrdinalIgnoreCase).ThenBy(static item => item.Port).ToArray();

        async Task ReadConfigAsync(string path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            path = Path.GetFullPath(path);
            // Discovery is a local suggestion list, not an SSH config evaluator. Never execute
            // Match/ProxyCommand directives or visit network shares, and bound Include cycles.
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || visited.Count >= 128 || !visited.Add(path)) return;
            var source = await ReadSmallFileAsync(path, cancellationToken).ConfigureAwait(false);
            foreach (var line in source.Split('\n'))
            {
                var parts = Tokenize(line);
                if (parts.Count < 2) continue;
                if (parts[0].Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var alias in parts.Skip(1).Where(IsTarget))
                        found.TryAdd(alias, new(alias, "SSH config"));
                }
                else if (parts[0].Equals("Include", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var pattern in parts.Skip(1))
                    {
                        var expanded = pattern.StartsWith("~/", StringComparison.Ordinal) || pattern.StartsWith(@"~\", StringComparison.Ordinal)
                            ? Path.Combine(userDirectory, pattern[2..]) : pattern;
                        var full = Path.GetFullPath(expanded, sshDirectory);
                        if (full.StartsWith(@"\\", StringComparison.Ordinal)) continue;
                        var parent = Path.GetDirectoryName(full)!;
                        if (!Directory.Exists(parent)) continue;
                        foreach (var include in Directory.EnumerateFiles(parent, Path.GetFileName(full)).Order(StringComparer.OrdinalIgnoreCase).Take(128))
                            await ReadConfigAsync(include).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    internal static IReadOnlyList<DiscoveredSshHost> ParseKnownHosts(string source)
    {
        var found = new List<DiscoveredSshHost>();
        foreach (var line in source.Split('\n'))
        {
            var fields = Tokenize(line);
            if (fields.Count == 0) continue;
            var offset = fields[0].StartsWith('@') ? 1 : 0;
            if (fields.Count < offset + 3 || fields[0] == "@revoked") continue;
            foreach (var entry in fields[offset].Split(','))
            {
                var target = entry;
                int? port = null;
                if (entry.StartsWith('[') && entry.IndexOf("]:", StringComparison.Ordinal) is var bracket && bracket > 1)
                {
                    target = entry[1..bracket];
                    if (!int.TryParse(entry[(bracket + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < 1 or > 65535) continue;
                    port = parsed;
                }
                if (IsTarget(target)) found.Add(new(target, "known hosts", port));
            }
        }
        return found;
    }

    private static bool IsTarget(string target) => target.Length is > 0 and <= 255 && target[0] != '-' &&
        target.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@' or ':' or '[' or ']');

    private static async Task<string> ReadSmallFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return string.Empty;
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) { return string.Empty; }
        catch (UnauthorizedAccessException) { return string.Empty; }
    }

    private static List<string> Tokenize(string line)
    {
        var result = new List<string>();
        var word = new StringBuilder();
        var quote = '\0';
        foreach (var character in line)
        {
            if (quote == '\0' && character == '#') break;
            if (character is '"' or '\'')
            {
                if (quote == character) { quote = '\0'; continue; }
                if (quote == '\0') { quote = character; continue; }
            }
            if (quote == '\0' && (char.IsWhiteSpace(character) || character == '='))
            {
                if (word.Length > 0) { result.Add(word.ToString()); word.Clear(); }
            }
            else word.Append(character);
        }
        if (word.Length > 0) result.Add(word.ToString());
        return result;
    }
}
