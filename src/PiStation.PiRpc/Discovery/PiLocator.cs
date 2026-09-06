using System.Text.Json;
using System.Text.RegularExpressions;

namespace PiStation.PiRpc.Discovery;

public sealed partial class PiLocator
{
    private const string PackageName = "@earendil-works/pi-coding-agent";
    private readonly IExecutableProbe _probe;

    public PiLocator(IExecutableProbe? probe = null)
    {
        _probe = probe ?? new ExecutableProbe();
    }

    public async Task<PiInstallation> LocateAsync(
        PiLocatorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PiLocatorOptions();
        if (!string.IsNullOrWhiteSpace(options.ExplicitPiPath))
        {
            return await ResolveCandidateAsync(
                options.ExplicitPiPath,
                "explicit",
                options,
                cancellationToken).ConfigureAwait(false);
        }

        var candidates = EnumerateCandidates(options).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var attempts = new List<string>();
        PiDiscoveryException? mostSpecificFailure = null;

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                attempts.Add($"not found: {candidate}");
                continue;
            }

            try
            {
                return await ResolveCandidateAsync(
                    candidate,
                    "discovered",
                    options,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (PiDiscoveryException exception)
            {
                attempts.Add($"{candidate}: {exception.Message}");
                mostSpecificFailure = exception;
            }
        }

        if (mostSpecificFailure is not null)
        {
            throw new PiDiscoveryException(
                mostSpecificFailure.Failure,
                mostSpecificFailure.Message,
                attempts,
                mostSpecificFailure);
        }

        throw new PiDiscoveryException(
            PiDiscoveryFailure.NotFound,
            "No Pi installation was found. Configure an explicit Pi path or install Pi on PATH.",
            attempts);
    }

    private async Task<PiInstallation> ResolveCandidateAsync(
        string candidate,
        string source,
        PiLocatorOptions options,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate));
        if (File.Exists(fullPath) && string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return await ResolveNativeExecutableAsync(fullPath, source, options, cancellationToken).ConfigureAwait(false);
        }

        var packageRoot = FindPackageRoot(fullPath);
        if (packageRoot is null)
        {
            throw new PiDiscoveryException(
                PiDiscoveryFailure.InvalidInstallation,
                $"Could not resolve the {PackageName} package root from '{fullPath}'.");
        }

        return await ResolveNodePackageAsync(
            fullPath,
            packageRoot,
            source,
            options,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PiInstallation> ResolveNativeExecutableAsync(
        string executablePath,
        string source,
        PiLocatorOptions options,
        CancellationToken cancellationToken)
    {
        var output = await _probe.GetVersionOutputAsync(executablePath, ["--version"], cancellationToken)
            .ConfigureAwait(false);
        var version = ParseVersionOutput(output, "Pi");
        EnsureMinimumPiVersion(version, options.MinimumPiVersion);

        return new PiInstallation(
            PiInstallationKind.NativeExecutable,
            executablePath,
            [],
            version,
            null,
            null,
            source);
    }

    private async Task<PiInstallation> ResolveNodePackageAsync(
        string candidatePath,
        string packageRoot,
        string source,
        PiLocatorOptions options,
        CancellationToken cancellationToken)
    {
        var packagePath = Path.Combine(packageRoot, "package.json");
        using var package = JsonDocument.Parse(await File.ReadAllTextAsync(packagePath, cancellationToken).ConfigureAwait(false));
        var root = package.RootElement;
        var name = GetRequiredString(root, "name", packagePath);
        if (!string.Equals(name, PackageName, StringComparison.Ordinal))
        {
            throw new PiDiscoveryException(
                PiDiscoveryFailure.InvalidInstallation,
                $"Package '{packagePath}' is '{name}', not '{PackageName}'.");
        }

        var piVersion = SemanticVersion.Parse(GetRequiredString(root, "version", packagePath));
        EnsureMinimumPiVersion(piVersion, options.MinimumPiVersion);

        var entrypoint = ResolveBinEntrypoint(root, packageRoot, packagePath);
        var minimumNodeVersion = ResolveMinimumNodeVersion(root, packagePath);
        var (nodePath, nodeVersion) = await ResolveNodeAsync(
            candidatePath,
            packageRoot,
            minimumNodeVersion,
            options,
            cancellationToken).ConfigureAwait(false);

        return new PiInstallation(
            PiInstallationKind.NodePackage,
            nodePath,
            [entrypoint],
            piVersion,
            nodeVersion,
            packageRoot,
            source);
    }

    private async Task<(string Path, SemanticVersion Version)> ResolveNodeAsync(
        string candidatePath,
        string packageRoot,
        SemanticVersion minimumVersion,
        PiLocatorOptions options,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.ExplicitNodePath))
        {
            candidates.Add(Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.ExplicitNodePath)));
        }
        else
        {
            var candidateDirectory = Directory.Exists(candidatePath)
                ? candidatePath
                : Path.GetDirectoryName(candidatePath);
            if (candidateDirectory is not null)
            {
                candidates.Add(Path.Combine(candidateDirectory, "node.exe"));
            }

            for (var directory = new DirectoryInfo(packageRoot); directory is not null; directory = directory.Parent)
            {
                candidates.Add(Path.Combine(directory.FullName, "node.exe"));
            }

            candidates.AddRange(EnumeratePathExecutables(options.SearchPath, "node.exe"));
        }

        var attempts = new List<string>();
        var sawUnsupportedVersion = false;
        foreach (var nodePath in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(nodePath))
            {
                attempts.Add($"not found: {nodePath}");
                continue;
            }

            try
            {
                var output = await _probe.GetVersionOutputAsync(nodePath, ["--version"], cancellationToken)
                    .ConfigureAwait(false);
                var version = ParseVersionOutput(output, "Node");
                if (version < minimumVersion)
                {
                    attempts.Add($"unsupported Node {version}: {nodePath} (requires >= {minimumVersion})");
                    sawUnsupportedVersion = true;
                    continue;
                }

                return (nodePath, version);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                attempts.Add($"probe failed: {nodePath}: {exception.Message}");
            }
        }

        throw new PiDiscoveryException(
            sawUnsupportedVersion ? PiDiscoveryFailure.UnsupportedNodeVersion : PiDiscoveryFailure.NodeNotFound,
            $"No compatible Node executable was found for Pi. Required Node version is >= {minimumVersion}.",
            attempts);
    }

    private static IEnumerable<string> EnumerateCandidates(PiLocatorOptions options)
    {
        foreach (var candidate in options.CandidatePiPaths)
        {
            yield return Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate));
        }

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            yield return Path.Combine(localApplicationData, "pi-node", "current", "pi.cmd");
        }

        foreach (var path in EnumeratePathExecutables(options.SearchPath, "pi.exe", "pi.cmd", "pi"))
        {
            yield return path;
        }
    }

    private static IEnumerable<string> EnumeratePathExecutables(string? searchPath, params string[] fileNames)
    {
        if (string.IsNullOrWhiteSpace(searchPath))
        {
            yield break;
        }

        foreach (var rawDirectory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (var fileName in fileNames)
            {
                yield return Path.GetFullPath(Path.Combine(directory, fileName));
            }
        }
    }

    private static string? FindPackageRoot(string candidatePath)
    {
        if (Directory.Exists(candidatePath))
        {
            var directPackage = Path.Combine(candidatePath, "package.json");
            if (File.Exists(directPackage))
            {
                return candidatePath;
            }

            var nested = Path.Combine(candidatePath, "node_modules", "@earendil-works", "pi-coding-agent");
            return File.Exists(Path.Combine(nested, "package.json")) ? nested : null;
        }

        if (!File.Exists(candidatePath))
        {
            return null;
        }

        if (string.Equals(Path.GetFileName(candidatePath), "package.json", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(candidatePath);
        }

        var startDirectory = Path.GetDirectoryName(candidatePath);
        if (string.Equals(Path.GetExtension(candidatePath), ".cmd", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetExtension(candidatePath), ".ps1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(candidatePath), "pi", StringComparison.OrdinalIgnoreCase))
        {
            var nested = startDirectory is null
                ? null
                : Path.Combine(startDirectory, "node_modules", "@earendil-works", "pi-coding-agent");
            if (nested is not null && File.Exists(Path.Combine(nested, "package.json")))
            {
                return nested;
            }
        }

        for (var directory = startDirectory is null ? null : new DirectoryInfo(startDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static string ResolveBinEntrypoint(JsonElement root, string packageRoot, string packagePath)
    {
        if (!root.TryGetProperty("bin", out var bin))
        {
            throw new PiDiscoveryException(
                PiDiscoveryFailure.MissingEntrypoint,
                $"Pi package '{packagePath}' does not declare bin.pi.");
        }

        string? relativeEntrypoint = bin.ValueKind switch
        {
            JsonValueKind.String => bin.GetString(),
            JsonValueKind.Object when bin.TryGetProperty("pi", out var pi) && pi.ValueKind == JsonValueKind.String =>
                pi.GetString(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(relativeEntrypoint))
        {
            throw new PiDiscoveryException(
                PiDiscoveryFailure.MissingEntrypoint,
                $"Pi package '{packagePath}' does not declare a string bin.pi entrypoint.");
        }

        var entrypoint = Path.GetFullPath(Path.Combine(packageRoot, relativeEntrypoint));
        var packagePrefix = Path.GetFullPath(packageRoot) + Path.DirectorySeparatorChar;
        if (!entrypoint.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(entrypoint))
        {
            throw new PiDiscoveryException(
                PiDiscoveryFailure.MissingEntrypoint,
                $"Pi bin.pi entrypoint '{entrypoint}' is missing or escapes the package root.");
        }

        return entrypoint;
    }

    private static SemanticVersion ResolveMinimumNodeVersion(JsonElement root, string packagePath)
    {
        if (!root.TryGetProperty("engines", out var engines) ||
            !engines.TryGetProperty("node", out var node) ||
            node.ValueKind != JsonValueKind.String)
        {
            throw new PiDiscoveryException(
                PiDiscoveryFailure.InvalidInstallation,
                $"Pi package '{packagePath}' does not declare engines.node.");
        }

        var requirement = node.GetString()!;
        var match = MinimumVersionPattern().Match(requirement);
        if (!match.Success || !SemanticVersion.TryParse(match.Groups[1].Value, out var version))
        {
            throw new PiDiscoveryException(
                PiDiscoveryFailure.InvalidInstallation,
                $"Unsupported Pi engines.node requirement '{requirement}'.");
        }

        return version;
    }

    private static string GetRequiredString(JsonElement element, string name, string packagePath)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new PiDiscoveryException(
                PiDiscoveryFailure.InvalidInstallation,
                $"Pi package '{packagePath}' is missing string property '{name}'.");
        }

        return property.GetString()!;
    }

    private static SemanticVersion ParseVersionOutput(string output, string executableName)
    {
        var matches = VersionPattern().Matches(output);
        if (matches.Count == 0 || !SemanticVersion.TryParse(matches[^1].Groups[1].Value, out var version))
        {
            throw new PiDiscoveryException(
                PiDiscoveryFailure.InvalidInstallation,
                $"Could not parse {executableName} version output '{output}'.");
        }

        return version;
    }

    private static void EnsureMinimumPiVersion(SemanticVersion version, SemanticVersion minimumVersion)
    {
        if (version < minimumVersion)
        {
            throw new PiDiscoveryException(
                PiDiscoveryFailure.UnsupportedPiVersion,
                $"Pi {version} is unsupported. Pi Station Desktop requires Pi >= {minimumVersion}.");
        }
    }

    [GeneratedRegex(@">=\s*(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex MinimumVersionPattern();

    [GeneratedRegex(@"v?(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
