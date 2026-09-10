using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PiStation.ClientRuntime.Themes;

namespace PiStation.App.ViewModels;

public sealed class ThemeEditorViewModel : ObservableObject
{
    private readonly ShellLayoutViewModel _layout;
    private readonly ThemeLibrary _library;
    private ThemePalette? _draft;
    private string? _editingId;
    private ThemePalette? _selectedTheme;
    private string _name = "Custom theme", _role = "canvas", _colorInput = "", _status = "Choose a theme or create a palette.", _json = "";
    private int _mode, _format;
    private bool _preview;
    public event EventHandler? PaletteChanged;
    public ThemeEditorViewModel(ShellLayoutViewModel layout, string? path)
    {
        _layout = layout; _library = new(path); RefreshLibrary();
        if (_library.Error is { } error) Status = error;
    }
    public ObservableCollection<ThemePalette> Themes { get; } = [];
    public string[] Roles { get; } = ThemeFiles.NativeRoles.Values.Distinct().Order().ToArray();
    public ThemePalette? SelectedTheme { get => _selectedTheme; set => SetProperty(ref _selectedTheme, value); }
    public ThemePalette? EffectivePalette => _preview && _draft is not null ? _draft : _library.Active;
    public string? PreviewAppearance => _preview && _draft is not null ? ModeName : null;
    public bool IsPreviewing => _preview && _draft is not null;
    public bool HasDraft => _draft is not null;
    public bool IsLibraryAvailable => _library.Error is null;
    public string ActiveSummary => _library.Active is { } active ? "Saved theme: " + active.Name : "Saved theme: Native defaults";
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Json { get => _json; set => SetProperty(ref _json, value); }
    public int ImportFormat { get => _format; set => SetProperty(ref _format, value); }
    public string ColorInput { get => _colorInput; set => SetProperty(ref _colorInput, value); }
    public string SelectedRole
    {
        get => _role;
        set { if (value is not null && SetProperty(ref _role, value)) RefreshColor(); }
    }
    public int AppearanceIndex
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value == 1 ? 1 : 0)) return;
            if (_draft is not null && _draft.ForAppearance(ModeName) is null)
            {
                var variants = _draft.Variants is null ? new Dictionary<string, Dictionary<string, string>>() : new(_draft.Variants);
                variants[ModeName] = ThemeFiles.Defaults(ModeName); _draft = _draft with { Variants = variants };
            }
            RefreshColor(); NotifyPalette();
        }
    }
    private string ModeName => _mode == 1 ? "light" : "dark";
    public bool PreviewEnabled { get => _preview; set { if (SetProperty(ref _preview, value)) NotifyPalette(); } }
    public void New()
    {
        var mode = _layout.ThemePreference == AppThemePreference.Light ? "light" : "dark";
        StartDraft(ThemeFiles.New(mode), null); Status = "New palette preview. Save to keep it, or cancel to restore the saved theme.";
    }
    public void Edit(bool duplicate = false)
    {
        if (SelectedTheme is not { } selected) throw new InvalidOperationException("Select a saved theme first.");
        var theme = ThemeFiles.Copy(selected);
        if (duplicate) theme = theme with { Id = "custom-" + Guid.NewGuid().ToString("N")[..8], Name = (theme.Name.Length > 40 ? theme.Name[..40] : theme.Name) + " copy" };
        StartDraft(theme, duplicate ? null : theme.Id); Status = "Editing preview. Save to keep changes, or cancel.";
    }
    private void StartDraft(ThemePalette theme, string? editingId)
    {
        _draft = theme; _editingId = editingId; _mode = theme.Appearance == "light" ? 1 : 0;
        Name = theme.Name; _preview = true;
        OnPropertyChanged(nameof(AppearanceIndex)); OnPropertyChanged(nameof(PreviewEnabled)); OnPropertyChanged(nameof(HasDraft));
        RefreshColor(); NotifyPalette();
    }
    public void ApplyColor()
    {
        if (_draft is null) throw new InvalidOperationException("Create or edit a palette first.");
        var literal = ColorInput.Trim(); _ = ThemeColor.Parse(literal);
        var colors = new Dictionary<string, string>(_draft.ForAppearance(ModeName)!); colors[SelectedRole] = literal;
        if (_draft.Appearance == ModeName) _draft = _draft with { Colors = colors, Managed = false };
        else { var variants = new Dictionary<string, Dictionary<string, string>>(_draft.Variants!) { [ModeName] = colors }; _draft = _draft with { Variants = variants, Managed = false }; }
        PreviewEnabled = true; NotifyPalette();
        var canvas = ThemeColor.Parse(colors["canvas"]); var text = ThemeColor.Parse(colors["text"]).Over(canvas);
        Status = $"Preview updated: {SelectedRole}. Base text/canvas contrast {text.Contrast(canvas):0.0}:1.";
    }
    public void ResetColor() { ColorInput = ThemeFiles.Defaults(ModeName)[SelectedRole]; ApplyColor(); }
    public void Save()
    {
        if (_draft is null) throw new InvalidOperationException("Create or edit a palette first.");
        // Name and all colors are validated before storage or the active preference changes.
        var saved = _library.Install(_draft with { Name = Name.Trim() }, replace: _editingId is not null);
        _layout.ThemePreference = ModeName == "light" ? AppThemePreference.Light : AppThemePreference.Dark;
        Cancel(); RefreshLibrary(); SelectedTheme = Themes.First(theme => theme.Id == saved.Id); Status = "Saved and applied " + saved.Name + ".";
    }
    public void ApplySelected()
    {
        if (SelectedTheme is not { } theme) throw new InvalidOperationException("Select a saved theme first.");
        _library.Select(theme.Id);
        if (theme.Variants is not { Count: > 0 }) _layout.ThemePreference = theme.Appearance == "light" ? AppThemePreference.Light : AppThemePreference.Dark;
        Cancel(); Status = "Applied " + theme.Name + ".";
    }
    public void UseDefaults() { _library.Select(null); Cancel(); Status = "Native palette restored. Saved themes are retained."; }
    public void DeleteSelected()
    {
        if (SelectedTheme is not { } theme) throw new InvalidOperationException("Select a saved theme first.");
        _library.Delete(theme.Id); Cancel(); RefreshLibrary(); Status = "Deleted " + theme.Name + ". The previous library is available in the backup.";
    }
    public void Cancel()
    {
        _draft = null; _editingId = null; _preview = false;
        OnPropertyChanged(nameof(HasDraft)); OnPropertyChanged(nameof(PreviewEnabled)); NotifyPalette(); Status = "Preview canceled. Saved theme restored.";
    }
    public void Import()
    {
        var theme = ThemeFiles.Import(Json, ImportFormat switch { 1 => "t3", 2 => "vscode", 3 => "pi", _ => "auto" });
        StartDraft(theme, null);
        Status = ImportFormat == 3 ? "Pi colors converted to a native preview. Terminal-default colors use native defaults; syntax and terminal layout settings are not imported. Save to install."
            : "Theme imported as a preview. Save to install; unmapped T3 colors are retained for export. VS Code syntax rules are not imported.";
    }
    public string Export()
    {
        var theme = _draft is not null ? _draft with { Name = Name.Trim() } : SelectedTheme ?? _library.Active ?? throw new InvalidOperationException("Select or create a theme first.");
        Json = ThemeFiles.Export(ThemeFiles.Copy(theme)); Status = "T3 theme JSON is ready to copy or save."; return Json;
    }
    public void Recover() { _library.RecoverBackup(); Cancel(); RefreshLibrary(); OnPropertyChanged(nameof(IsLibraryAvailable)); Status = "Restored the previous theme library."; }
    public void ReloadLibrary()
    {
        _library.Reload(); RefreshLibrary(); OnPropertyChanged(nameof(IsLibraryAvailable)); NotifyPalette();
        Status = _library.Error ?? "Library reloaded. Any open preview is retained.";
    }
    public void ResetDamagedLibrary()
    {
        _library.ResetDamagedLibrary(); Cancel(); RefreshLibrary(); OnPropertyChanged(nameof(IsLibraryAvailable));
        Status = "Started an empty library. A copy of the damaged file was retained.";
    }
    public void InspectRole(string role)
    {
        if (!Roles.Contains(role)) return;
        if (_draft is null) { if (_library.Active is { } active) StartDraft(ThemeFiles.Copy(active), active.Id); else New(); }
        SelectedRole = role; Status = "Inspected " + role + ". Edit its color below.";
    }
    public void Run(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException or ArgumentException) { Status = error.Message; }
    }
    private void RefreshColor() => ColorInput = (_draft?.ForAppearance(ModeName) ?? ThemeFiles.Defaults(ModeName)).GetValueOrDefault(SelectedRole) ?? "";
    private void RefreshLibrary()
    {
        Themes.Clear(); foreach (var theme in _library.Themes) Themes.Add(theme);
        SelectedTheme = Themes.FirstOrDefault(theme => theme.Id == _library.ActiveId) ?? Themes.FirstOrDefault();
        OnPropertyChanged(nameof(ActiveSummary));
    }
    private void NotifyPalette() { OnPropertyChanged(nameof(IsPreviewing)); OnPropertyChanged(nameof(ActiveSummary)); PaletteChanged?.Invoke(this, EventArgs.Empty); }
}
