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
            return settings with { Extensions = Validate(settings.Extensions, requireFiles: false) };
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
}
