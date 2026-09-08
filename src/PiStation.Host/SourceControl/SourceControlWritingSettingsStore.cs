using System.Text.Json;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.SourceControl;

public sealed class SourceControlWritingSettingsStore(string dataRoot) : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetFullPath(dataRoot), "source-control-writing.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    public async Task<SourceControlWritingSettings> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(_path)) return new();
        var settings = JsonSerializer.Deserialize(await File.ReadAllTextAsync(_path, token).ConfigureAwait(false),
            ProtocolJsonContext.Default.SourceControlWritingSettings)
            ?? throw new InvalidDataException("Source control writing settings are invalid.");
        Validate(settings);
        return settings;
    }

    public async Task<SourceControlWritingSettings> SaveAsync(SourceControlWritingSettings settings, CancellationToken token = default)
    {
        Validate(settings);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var current = await LoadAsync(token).ConfigureAwait(false);
            if (current.Revision != settings.Revision)
                throw new InvalidOperationException("Writing settings changed. Refresh Settings before saving.");
            var saved = settings with { Revision = checked(current.Revision + 1), CustomInstructions = settings.CustomInstructions.Trim() };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(saved,
                    ProtocolJsonContext.Default.SourceControlWritingSettings), token).ConfigureAwait(false);
                File.Move(temporary, _path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return saved;
        }
        finally { _gate.Release(); }
    }

    private static void Validate(SourceControlWritingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.Style) || settings.Revision < 0 || settings.CustomInstructions is null || settings.CustomInstructions.Length > 20_000)
            throw new ArgumentException("Choose a writing style and at most 20,000 characters of instructions.");
        if (settings.Model is { } model && (string.IsNullOrWhiteSpace(model.ProviderId) || string.IsNullOrWhiteSpace(model.ModelId) ||
            model.ProviderId.Length > 256 || model.ModelId.Length > 256 || model.ProviderId.Any(char.IsControl) || model.ModelId.Any(char.IsControl)))
            throw new ArgumentException("Choose a valid Pi provider and model, or use the current model.");
    }
}
