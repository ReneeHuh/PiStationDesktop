using System.Text.Json;
using System.Text;

namespace PiStation.ClientRuntime.Themes;

/// <summary>Local library with atomic replacement, a last-good backup and transactional in-memory state.</summary>
public sealed class ThemeLibrary
{
    private readonly string? _path;
    private string? _loadedContent;
    private const int MaximumLibraryCharacters = 4 * 1024 * 1024;
    public IReadOnlyList<ThemePalette> Themes { get; private set; } = [];
    public string? ActiveId { get; private set; }
    public string? Error { get; private set; }
    public ThemePalette? Active => Themes.FirstOrDefault(theme => theme.Id == ActiveId);
    public ThemeLibrary(string? path)
    {
        _path = path;
        Reload();
    }
    public void Reload()
    {
        try
        {
            var content = ReadCurrent(_path);
            if (content is null) { Themes = []; ActiveId = null; _loadedContent = null; }
            else Load(content);
            Error = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        { Error = "Theme library could not be loaded. Restore its backup or reset the damaged library. " + error.Message; }
    }
    private static string? ReadCurrent(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        using var reader = File.OpenText(path);
        var content = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (content.Length + count > MaximumLibraryCharacters) throw new FormatException("Theme library is too large.");
            content.Append(buffer, 0, count);
        }
        return content.ToString();
    }
    private void Load(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
        if (doc.RootElement.GetProperty("version").GetInt32() != 1) throw new FormatException("Unsupported theme library version.");
        var themes = doc.RootElement.GetProperty("themes").EnumerateArray().Select(ThemeFiles.ParseT3).ToArray();
        if (themes.Length > 100 || themes.Select(theme => theme.Id).Distinct().Count() != themes.Length) throw new FormatException("Theme library is too large or contains duplicate ids.");
        var active = doc.RootElement.GetProperty("activeId").GetString();
        Themes = themes; ActiveId = themes.Any(theme => theme.Id == active) ? active : null; _loadedContent = json;
    }
    public void RecoverBackup()
    {
        if (_path is null || !File.Exists(_path + ".bak")) throw new IOException("No theme backup is available. Reset the damaged library, then import a saved theme export.");
        var previousThemes = Themes; var previousActive = ActiveId; var previousContent = _loadedContent;
        try { Load(ReadCurrent(_path + ".bak")!); Write(Themes, ActiveId, recovering: true); Error = null; }
        catch { Themes = previousThemes; ActiveId = previousActive; _loadedContent = previousContent; throw; }
    }
    public void ResetDamagedLibrary()
    {
        if (Error is null) throw new InvalidOperationException("The library is healthy. Use Delete to remove an individual theme.");
        if (_path is not null && File.Exists(_path)) File.Copy(_path, _path + ".damaged-" + Guid.NewGuid().ToString("N"));
        Write([], null, recovering: true); Themes = []; ActiveId = null; Error = null;
    }
    public ThemePalette Install(ThemePalette input, bool replace = false)
    {
        var theme = ThemeFiles.Copy(input);
        if (!replace)
        {
            var root = theme.Id.Length > 40 ? theme.Id[..40] : theme.Id; var index = 2;
            while (Themes.Any(existing => existing.Id == theme.Id)) theme = theme with { Id = root + "-" + index++ };
        }
        var next = Themes.Where(existing => existing.Id != theme.Id).Append(theme).ToArray();
        if (next.Length > 100) throw new InvalidOperationException("The local library supports up to 100 themes.");
        Write(next, theme.Id); Themes = next; ActiveId = theme.Id;
        return theme;
    }
    public void Select(string? id)
    {
        if (id is not null && Themes.All(theme => theme.Id != id)) throw new InvalidOperationException("Theme no longer exists.");
        Write(Themes, id); ActiveId = id;
    }
    public void Delete(string id)
    {
        var next = Themes.Where(theme => theme.Id != id).ToArray(); var active = ActiveId == id ? null : ActiveId;
        Write(next, active); Themes = next; ActiveId = active;
    }
    private void Write(IReadOnlyList<ThemePalette> themes, string? active, bool recovering = false)
    {
        if (Error is not null && !recovering) throw new InvalidOperationException(Error);
        if (_path is null) return;
        var json = JsonSerializer.Serialize(new { version = 1, activeId = active, themes = themes.Select(theme => JsonSerializer.Deserialize<JsonElement>(ThemeFiles.Export(theme))) }, ThemeFiles.JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        using var lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!recovering && !string.Equals(ReadCurrent(_path), _loadedContent, StringComparison.Ordinal))
            throw new InvalidOperationException("The theme library changed in another window. Reload the library before saving; your preview is retained.");
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            if (File.Exists(_path)) File.Replace(temporary, _path, recovering ? null : _path + ".bak");
            else File.Move(temporary, _path);
            _loadedContent = json;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
