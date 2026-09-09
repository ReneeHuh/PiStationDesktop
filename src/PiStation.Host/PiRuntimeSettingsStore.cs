using System.Text.Json;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host;

public static class PiRuntimeSettingsStore
{
    private const int MaximumSettingsBytes = 64 * 1024;
    public static PiRuntimeConfiguration Load(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "pi-runtime.json");
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > MaximumSettingsBytes) throw new IOException("Pi runtime settings exceed their size limit.");
            var settings = JsonSerializer.Deserialize(File.ReadAllText(path), ProtocolJsonContext.Default.PiRuntimeConfiguration)
                ?? throw new JsonException("Pi runtime settings are empty.");
            return settings with { Extensions = Validate(settings.Extensions, requireFiles: false), Launch = ValidateLaunch(settings.Launch ?? new()) };
        }
        var legacy = Path.Combine(dataRoot, "pi-executable.txt");
        return new(File.Exists(legacy) ? File.ReadAllText(legacy).Trim() : null, new());
    }

    public static PiExtensionConfiguration Validate(PiExtensionConfiguration configuration, bool requireFiles = true)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Paths?.Count > 32) throw new ArgumentException("Configure at most 32 explicit extensions.");
        var paths = new List<string>();
        foreach (var value in configuration.Paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl) || !Path.IsPathFullyQualified(value))
                throw new ArgumentException("Each extension must have an absolute local file or directory path.");
            var path = Path.GetFullPath(value.Trim());
            if (requireFiles && !File.Exists(path) && !Directory.Exists(path))
                throw new FileNotFoundException("The configured extension does not exist.", path);
            if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Add(path);
        }
        return configuration with { Paths = paths.ToArray() };
    }

    public static async Task SaveAsync(string dataRoot, PiRuntimeConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(configuration, ProtocolJsonContext.Default.PiRuntimeConfiguration);
        if (content.Length > MaximumSettingsBytes) throw new IOException("Pi runtime settings exceed their size limit.");
        Directory.CreateDirectory(dataRoot);
        var path = Path.Combine(dataRoot, "pi-runtime.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static PiLaunchConfiguration ValidateLaunch(PiLaunchConfiguration configuration)
    {
        if (configuration.Arguments?.Count > 128 || configuration.EnvironmentVariables?.Count > 128)
            throw new ArgumentException("Configure at most 128 arguments and environment variables.");
        var reserved = new HashSet<string>(StringComparer.Ordinal)
        { "--", "--mode", "--session", "--session-id", "--fork", "--session-dir", "--continue", "-c", "--resume", "-r", "--no-session",
          "--extension", "-e", "--no-extensions", "-ne", "--help", "-h", "--version", "-v", "--print", "-p", "--export", "--list-models", "--tui-mode" };
        foreach (var argument in configuration.Arguments ?? [])
            if (argument is null || argument.Length > 32768 || argument.Contains('\0') || reserved.Contains(argument.Split('=')[0]))
                throw new ArgumentException("Launch arguments cannot replace PiStation's RPC mode, session, or extension configuration.");
        foreach (var (name, value) in configuration.EnvironmentVariables ?? new Dictionary<string, string?>())
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_') ||
                name.StartsWith("PISTATION_", StringComparison.OrdinalIgnoreCase) || value?.Contains('\0') == true)
                throw new ArgumentException("Enter valid environment-variable names. PISTATION_ variables are managed by the desktop.");
        if (configuration.CommandTimeoutSeconds is < 5 or > 600 || configuration.ShutdownTimeoutSeconds is < 1 or > 30)
            throw new ArgumentException("Command timeout must be 5–600 seconds; shutdown timeout must be 1–30 seconds.");
        var tools = configuration.Tools is null ? null : PiToolSelectionRules.Normalize(configuration.Tools);
        PiToolSelectionRules.ValidateArguments(tools, configuration.Arguments ?? []);
        return configuration with { Tools = tools };
    }
}
