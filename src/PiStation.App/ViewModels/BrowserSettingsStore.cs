using System.Text.Json;

namespace PiStation.App.ViewModels;

internal sealed record BrowserSettingsSnapshot(BrowserDefaults Defaults, BrowserLinkTarget LinkTarget,
    IReadOnlyList<BrowserProfilePreference> Profiles, string DefaultProfileId);

// All environment windows on this desktop share preferences; WebView storage
// remains scoped to each environment's data directory.
internal sealed class BrowserSettingsStore
{
    internal const int MaximumSavedProfiles = 4096;
    private static readonly Dictionary<string, BrowserSettingsStore> Stores = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    internal BrowserSettingsSnapshot Snapshot { get; private set; }
    internal event Action? Changed;

    private BrowserSettingsStore(string path, BrowserSettingsSnapshot initial)
    {
        _path = path;
        Snapshot = initial;
        try
        {
            if (!File.Exists(path))
            {
                ImportLegacyProfiles();
                Update(Snapshot); // Persist the one-time migration before profiles can be removed.
            }
            else if (new FileInfo(path).Length <= 4 * 1024 * 1024)
                Snapshot = JsonSerializer.Deserialize<BrowserSettingsSnapshot>(File.ReadAllText(path)) ?? initial;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { System.Diagnostics.Trace.TraceWarning("Browser settings could not be read: {0}", error.Message); }
    }

    private void ImportLegacyProfiles()
    {
        var remoteRoot = Path.Combine(Path.GetDirectoryName(_path)!, "remote-environments");
        if (!Directory.Exists(remoteRoot)) return;
        var profiles = Snapshot.Profiles.ToList();
        foreach (var directory in Directory.EnumerateDirectories(remoteRoot))
        {
            if (Path.GetFileName(directory) is not { Length: 64 } name || !name.All(Uri.IsHexDigit)) continue;
            var legacyPath = Path.Combine(directory, "layout-settings.json");
            if (!File.Exists(legacyPath) || new FileInfo(legacyPath).Length > 8 * 1024 * 1024) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(legacyPath));
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("BrowserProfiles", out var saved) || saved.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in saved.EnumerateArray())
                {
                    if (profiles.Count >= MaximumSavedProfiles) break;
                    var profile = item.Deserialize<BrowserProfilePreference>();
                    if (profile is not { Id.Length: > 0 and <= 64, Name.Length: > 0 and <= 48 }) continue;
                    profile = profile with { Id = profile.Id.Trim(), Name = profile.Name.Trim() };
                    if (profile.Id.Length == 0 || profile.Name.Length == 0 || profile.Id == "incognito" ||
                        profile.Id.Any(char.IsControl) || profiles.Any(existing => existing.Id == profile.Id)) continue;
                    profiles.Add(profile);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { System.Diagnostics.Trace.TraceWarning("Legacy browser profiles could not be read: {0}", error.Message); }
        }
        Snapshot = Snapshot with { Profiles = profiles };
    }

    internal static BrowserSettingsStore Open(string path, BrowserSettingsSnapshot initial)
    {
        path = Path.GetFullPath(path);
        lock (Stores)
        {
            if (!Stores.TryGetValue(path, out var store)) Stores[path] = store = new(path, initial);
            return store;
        }
    }

    internal void Update(BrowserSettingsSnapshot snapshot, bool requirePersistence = false)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(snapshot));
            File.Move(_path + ".tmp", _path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (requirePersistence) throw;
            System.Diagnostics.Trace.TraceWarning("Browser settings could not be saved: {0}", error.Message);
        }
        Snapshot = snapshot;
        Changed?.Invoke();
    }
}
