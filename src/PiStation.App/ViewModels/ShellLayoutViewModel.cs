using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public enum WorkbenchPanelKind
{
    Changes,
    Files,
    Terminal,
    Preview,
    Agents,
}

public enum AppThemePreference
{
    Dark,
    System,
    Light,
}

public enum PreviewColorScheme
{
    System,
    Light,
    Dark,
}

public enum PreviewDevToolsPolicy
{
    Disabled,
    UserInitiated,
}

public enum PreviewAutomationAccess
{
    Off,
    Inspect,
    Interact,
}

public sealed class ShellLayoutViewModel : ObservableObject
{
    public const double DefaultRightPanelWidth = 420;
    public const double MinimumRightPanelWidth = 360;
    public const double MaximumRightPanelWidth = 720;
    public const string DefaultTerminalFontFamily = "Cascadia Mono, Consolas";
    public const double DefaultTerminalFontSize = 12;
    public const double MinimumTerminalFontSize = 6;
    public const double MaximumTerminalFontSize = 32;

    private static readonly string[] SupportedTerminalFontFamilies =
    [
        DefaultTerminalFontFamily,
        "Consolas",
        "Cascadia Code",
        "CaskaydiaCove Nerd Font",
        "JetBrains Mono",
        "Fira Code",
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string? _settingsPath;
    private bool _isSidebarCollapsed;
    private bool _isRightPanelOpen;
    private double _rightPanelWidth = DefaultRightPanelWidth;
    private WorkbenchPanelKind _selectedPanel = WorkbenchPanelKind.Changes;
    private AppThemePreference _themePreference = AppThemePreference.Dark;
    private string _terminalFontFamily = DefaultTerminalFontFamily;
    private double _terminalFontSize = DefaultTerminalFontSize;
    private readonly Dictionary<string, TerminalPaneLayoutPreference> _terminalPaneLayouts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _previewUrls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PreviewWorkspacePreference> _previewWorkspaces =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PreviewAutomationAccess> _previewAutomationPermissions =
        new(StringComparer.Ordinal);
    private readonly List<BrowserProfilePreference> _browserProfiles =
        [new BrowserProfilePreference("default", "Default")];
    private string _defaultBrowserProfileId = "default";
    private PreviewDevToolsPolicy _previewDevToolsPolicy;
    private readonly List<CommandKeybindingPreference> _commandKeybindings = [];

    public ShellLayoutViewModel(string? settingsPath = null)
    {
        _settingsPath = string.IsNullOrWhiteSpace(settingsPath) ? null : settingsPath;
        Load();
    }

    public bool IsSidebarCollapsed
    {
        get => _isSidebarCollapsed;
        set
        {
            if (SetProperty(ref _isSidebarCollapsed, value))
            {
                OnPropertyChanged(nameof(LayoutSummary));
                Save();
            }
        }
    }

    public bool IsRightPanelOpen
    {
        get => _isRightPanelOpen;
        set
        {
            if (SetProperty(ref _isRightPanelOpen, value))
            {
                OnPropertyChanged(nameof(RightPanelVisibility));
                OnPropertyChanged(nameof(WorkbenchToggleLabel));
                OnPropertyChanged(nameof(LayoutSummary));
                Save();
            }
        }
    }

    public Visibility RightPanelVisibility => IsRightPanelOpen
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string WorkbenchToggleLabel => IsRightPanelOpen
        ? "Close workbench"
        : "Open workbench";

    public double RightPanelWidth
    {
        get => _rightPanelWidth;
        private set => SetProperty(ref _rightPanelWidth, ClampWidth(value));
    }

    public WorkbenchPanelKind SelectedPanel
    {
        get => _selectedPanel;
        set
        {
            if (SetProperty(ref _selectedPanel, value))
            {
                OnPropertyChanged(nameof(SelectedPanelTitle));
                OnPropertyChanged(nameof(SelectedPanelDescription));
                OnPropertyChanged(nameof(WorkbenchEmptyStateVisibility));
                OnPropertyChanged(nameof(ChangesPanelVisibility));
                OnPropertyChanged(nameof(FilesPanelVisibility));
                OnPropertyChanged(nameof(TerminalPanelVisibility));
                OnPropertyChanged(nameof(PreviewPanelVisibility));
                OnPropertyChanged(nameof(AgentsPanelVisibility));
                OnPropertyChanged(nameof(LayoutSummary));
                Save();
            }
        }
    }

    public AppThemePreference ThemePreference
    {
        get => _themePreference;
        set
        {
            if (SetProperty(ref _themePreference, value))
            {
                OnPropertyChanged(nameof(ThemeSummary));
                Save();
            }
        }
    }

    public string ThemeSummary => ThemePreference switch
    {
        AppThemePreference.Dark => "Dark theme",
        AppThemePreference.System => "Follow Windows",
        AppThemePreference.Light => "Light theme",
        _ => "Dark theme",
    };

    public string TerminalFontFamily
    {
        get => _terminalFontFamily;
        set
        {
            if (SetProperty(ref _terminalFontFamily, NormalizeTerminalFontFamily(value)))
            {
                OnPropertyChanged(nameof(TerminalAppearanceSummary));
                Save();
            }
        }
    }

    public double TerminalFontSize
    {
        get => _terminalFontSize;
        set
        {
            if (SetProperty(ref _terminalFontSize, NormalizeTerminalFontSize(value)))
            {
                OnPropertyChanged(nameof(TerminalAppearanceSummary));
                Save();
            }
        }
    }

    public string TerminalAppearanceSummary =>
        $"{TerminalFontFamily.Split(',')[0].Trim()} • {Math.Round(TerminalFontSize)} px";

    public string SelectedPanelTitle => SelectedPanel switch
    {
        WorkbenchPanelKind.Changes => "Changes",
        WorkbenchPanelKind.Files => "Files",
        WorkbenchPanelKind.Terminal => "Terminal",
        WorkbenchPanelKind.Preview => "Preview",
        WorkbenchPanelKind.Agents => "Agents",
        _ => "Workbench",
    };

    public string SelectedPanelDescription => SelectedPanel switch
    {
        WorkbenchPanelKind.Changes =>
            "Inspect the active project's Git branch, file states, and diffs.",
        WorkbenchPanelKind.Files =>
            "Browse, search, edit, and preview files in the active thread workspace.",
        WorkbenchPanelKind.Terminal =>
            "Run project commands in host-owned terminal sessions.",
        WorkbenchPanelKind.Preview =>
            "Browse local development servers in persistent WebView2 tabs.",
        WorkbenchPanelKind.Agents =>
            "Inspect live and completed agents and workflows for this thread.",
        _ => "This workbench view is not available.",
    };

    public Visibility WorkbenchEmptyStateVisibility => SelectedPanel is WorkbenchPanelKind.Changes or WorkbenchPanelKind.Files or WorkbenchPanelKind.Terminal or WorkbenchPanelKind.Preview or WorkbenchPanelKind.Agents
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility ChangesPanelVisibility => SelectedPanel == WorkbenchPanelKind.Changes
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility FilesPanelVisibility => SelectedPanel == WorkbenchPanelKind.Files
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility TerminalPanelVisibility => SelectedPanel == WorkbenchPanelKind.Terminal
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility PreviewPanelVisibility => SelectedPanel == WorkbenchPanelKind.Preview
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility AgentsPanelVisibility => SelectedPanel == WorkbenchPanelKind.Agents
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string LayoutSummary =>
        $"Sidebar {(IsSidebarCollapsed ? "collapsed" : "expanded")} • " +
        $"Workbench {(IsRightPanelOpen ? SelectedPanelTitle : "closed")} • " +
        $"{Math.Round(RightPanelWidth)} px";

    public IReadOnlyList<CommandKeybindingPreference> CommandKeybindings => _commandKeybindings.ToArray();

    public IReadOnlyList<BrowserProfilePreference> BrowserProfiles => _browserProfiles.ToArray();

    public string DefaultBrowserProfileId => _defaultBrowserProfileId;

    public PreviewDevToolsPolicy PreviewDevToolsPolicy
    {
        get => _previewDevToolsPolicy;
        set
        {
            var normalized = Enum.IsDefined(value) ? value : PreviewDevToolsPolicy.Disabled;
            if (SetProperty(ref _previewDevToolsPolicy, normalized))
            {
                Save();
            }
        }
    }

    public void ToggleRightPanel() => IsRightPanelOpen = !IsRightPanelOpen;

    public void ResizeRightPanel(double width) => RightPanelWidth = width;

    public void CommitRightPanelWidth()
    {
        OnPropertyChanged(nameof(LayoutSummary));
        Save();
    }

    public void Reset()
    {
        _terminalPaneLayouts.Clear();
        _previewUrls.Clear();
        _previewWorkspaces.Clear();
        _previewAutomationPermissions.Clear();
        _browserProfiles.Clear();
        _browserProfiles.Add(new BrowserProfilePreference("default", "Default"));
        _defaultBrowserProfileId = "default";
        _previewDevToolsPolicy = PreviewDevToolsPolicy.Disabled;
        IsSidebarCollapsed = false;
        IsRightPanelOpen = false;
        SelectedPanel = WorkbenchPanelKind.Changes;
        RightPanelWidth = DefaultRightPanelWidth;
        OnPropertyChanged(nameof(LayoutSummary));
        Save();
    }

    public BrowserProfilePreference AddBrowserProfile(string name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 48)
        {
            throw new ArgumentException("Browser profile names contain between 1 and 48 characters.", nameof(name));
        }

        if (_browserProfiles.Count >= 12)
        {
            throw new InvalidOperationException("At most 12 browser profiles may be retained.");
        }

        var profile = new BrowserProfilePreference(Guid.NewGuid().ToString("N"), normalized);
        _browserProfiles.Add(profile);
        OnPropertyChanged(nameof(BrowserProfiles));
        Save();
        return profile;
    }

    public void SetDefaultBrowserProfile(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (_browserProfiles.Any(profile => profile.Id == profileId) &&
            !string.Equals(_defaultBrowserProfileId, profileId, StringComparison.Ordinal))
        {
            _defaultBrowserProfileId = profileId;
            OnPropertyChanged(nameof(DefaultBrowserProfileId));
            Save();
        }
    }

    public PreviewAutomationAccess GetPreviewAutomationPermission(string contextKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextKey);
        return _previewAutomationPermissions.GetValueOrDefault(contextKey, PreviewAutomationAccess.Off);
    }

    public void SavePreviewAutomationPermission(string contextKey, PreviewAutomationAccess permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextKey);
        var normalized = Enum.IsDefined(permission) ? permission : PreviewAutomationAccess.Off;
        if (normalized == PreviewAutomationAccess.Off)
        {
            _previewAutomationPermissions.Remove(contextKey);
        }
        else
        {
            _previewAutomationPermissions[contextKey] = normalized;
        }

        Save();
    }

    public void ResetTerminalAppearance()
    {
        TerminalFontFamily = DefaultTerminalFontFamily;
        TerminalFontSize = DefaultTerminalFontSize;
    }

    public TerminalPaneLayoutPreference? GetTerminalPaneLayout(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return _terminalPaneLayouts.TryGetValue(projectId, out var preference)
            ? preference
            : null;
    }

    public void SaveTerminalPaneLayout(string projectId, TerminalPaneLayoutPreference preference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(preference);
        _terminalPaneLayouts[projectId] = NormalizeTerminalPaneLayout(preference);
        Save();
    }

    public string? GetPreviewUrl(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return _previewUrls.GetValueOrDefault(projectId);
    }

    public void SavePreviewUrl(string projectId, string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        _previewUrls[projectId] = url;
        Save();
    }

    public void ClearPreviewUrl(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (_previewUrls.Remove(projectId))
        {
            Save();
        }
    }

    public PreviewWorkspacePreference? GetPreviewWorkspace(string contextKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextKey);
        return _previewWorkspaces.GetValueOrDefault(contextKey);
    }

    public void SavePreviewWorkspace(string contextKey, PreviewWorkspacePreference preference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextKey);
        ArgumentNullException.ThrowIfNull(preference);
        _previewWorkspaces[contextKey] = NormalizePreviewWorkspace(preference);
        Save();
    }

    public void ClearPreviewWorkspace(string contextKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextKey);
        if (_previewWorkspaces.Remove(contextKey))
        {
            Save();
        }
    }

    public void SaveCommandKeybinding(CommandKeybindingPreference preference)
    {
        ArgumentNullException.ThrowIfNull(preference);
        var commandId = preference.CommandId?.Trim() ?? string.Empty;
        var gesture = preference.Gesture?.Trim() ?? string.Empty;
        var when = string.IsNullOrWhiteSpace(preference.When) ? null : preference.When.Trim();
        if (commandId.Length is 0 or > 128 || gesture.Length is 0 or > 64 || when?.Length > 256)
        {
            return;
        }

        _commandKeybindings.RemoveAll(binding =>
            string.Equals(binding.CommandId, commandId, StringComparison.Ordinal));
        _commandKeybindings.Add(new CommandKeybindingPreference(commandId, gesture, when));
        OnPropertyChanged(nameof(CommandKeybindings));
        Save();
    }

    public void RemoveCommandKeybinding(string commandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        if (_commandKeybindings.RemoveAll(binding =>
                string.Equals(binding.CommandId, commandId, StringComparison.Ordinal)) > 0)
        {
            OnPropertyChanged(nameof(CommandKeybindings));
            Save();
        }
    }

    public void ResetCommandKeybindings()
    {
        if (_commandKeybindings.Count == 0)
        {
            return;
        }

        _commandKeybindings.Clear();
        OnPropertyChanged(nameof(CommandKeybindings));
        Save();
    }

    private static double ClampWidth(double width) =>
        Math.Clamp(width, MinimumRightPanelWidth, MaximumRightPanelWidth);

    private static string NormalizeTerminalFontFamily(string? value) =>
        SupportedTerminalFontFamilies.FirstOrDefault(
            family => string.Equals(family, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ??
        DefaultTerminalFontFamily;

    private static double NormalizeTerminalFontSize(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(Math.Round(value), MinimumTerminalFontSize, MaximumTerminalFontSize)
            : DefaultTerminalFontSize;

    private static TerminalPaneLayoutPreference NormalizeTerminalPaneLayout(
        TerminalPaneLayoutPreference preference)
    {
        var root = NormalizeTerminalPaneNode(preference.Root, depth: 0);
        var paneCount = root is null
            ? preference.IsSplit ? 2 : 1
            : CountTerminalPaneLeaves(root);
        return new TerminalPaneLayoutPreference(
            preference.IsSplit || paneCount > 1,
            Enum.IsDefined(preference.SplitOrientation)
                ? preference.SplitOrientation
                : TerminalSplitOrientation.Right,
            WorkbenchTerminalViewModel.NormalizeSplitRatio(preference.SplitRatio),
            Math.Clamp(preference.ActivePaneIndex, 0, Math.Max(0, paneCount - 1)),
            NormalizeSessionId(preference.PrimarySessionId),
            NormalizeSessionId(preference.SecondarySessionId),
            root);
    }

    private static TerminalPaneLayoutNodePreference? NormalizeTerminalPaneNode(
        TerminalPaneLayoutNodePreference? node,
        int depth)
    {
        if (node is null || depth >= WorkbenchTerminalViewModel.MaximumPaneCount)
        {
            return null;
        }

        var first = NormalizeTerminalPaneNode(node.First, depth + 1);
        var second = NormalizeTerminalPaneNode(node.Second, depth + 1);
        TerminalSplitOrientation? orientation = node.SplitOrientation is { } value && Enum.IsDefined(value)
            ? value
            : null;
        var isSplit = orientation is not null && first is not null && second is not null;
        return new TerminalPaneLayoutNodePreference(
            NormalizeNodeId(node.NodeId),
            isSplit ? null : NormalizeNodeId(node.PaneId),
            isSplit ? null : NormalizeSessionId(node.SessionId),
            isSplit ? orientation : null,
            WorkbenchTerminalViewModel.NormalizeSplitRatio(node.SplitRatio),
            isSplit ? first : null,
            isSplit ? second : null);
    }

    private static int CountTerminalPaneLeaves(TerminalPaneLayoutNodePreference node) =>
        node.First is null || node.Second is null
            ? 1
            : CountTerminalPaneLeaves(node.First) + CountTerminalPaneLeaves(node.Second);

    private static string? NormalizeNodeId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeSessionId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PreviewWorkspacePreference NormalizePreviewWorkspace(
        PreviewWorkspacePreference preference)
    {
        var tabs = (preference.Tabs ?? Array.Empty<PreviewTabPreference>())
            .Where(static tab => tab is not null && !string.IsNullOrWhiteSpace(tab.TabId))
            .Take(WorkbenchPreviewViewModel.MaximumTabs)
            .Select(static tab =>
            {
                var url = WorkbenchPreviewViewModel.TryNormalizeAddress(tab.Url, out var uri, out _)
                    ? uri.AbsoluteUri
                    : string.Empty;
                var preset = Enum.IsDefined(tab.ViewportPreset)
                    ? tab.ViewportPreset
                    : PreviewViewportPreset.Responsive;
                var width = tab.ViewportWidth is >= 240 and <= 3840 ? tab.ViewportWidth : 0;
                var height = tab.ViewportHeight is >= 240 and <= 3840 ? tab.ViewportHeight : 0;
                if (width * (long)height > 3840L * 2160L)
                {
                    width = 0;
                    height = 0;
                }

                return new PreviewTabPreference(
                    tab.TabId.Trim(),
                    url,
                    string.IsNullOrWhiteSpace(tab.Title) ? "New preview" : tab.Title.Trim(),
                    preset,
                    width,
                    height,
                    double.IsFinite(tab.ZoomFactor) ? Math.Clamp(tab.ZoomFactor, 0.25, 3) : 1,
                    Enum.IsDefined(tab.ColorScheme) ? tab.ColorScheme : PreviewColorScheme.System,
                    string.IsNullOrWhiteSpace(tab.ProfileId) ? "default" : tab.ProfileId.Trim());
            })
            .ToArray();
        var activeTabId = tabs.Any(tab =>
            string.Equals(tab.TabId, preference.ActiveTabId, StringComparison.Ordinal))
            ? preference.ActiveTabId
            : tabs.LastOrDefault()?.TabId;
        var recentUrls = (preference.RecentUrls ?? [])
            .Where(url => WorkbenchPreviewViewModel.TryNormalizeAddress(url, out _, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        return new PreviewWorkspacePreference(activeTabId, tabs, recentUrls);
    }

    private void Load()
    {
        if (_settingsPath is null || !File.Exists(_settingsPath))
        {
            return;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<LayoutSettings>(
                File.ReadAllText(_settingsPath),
                SerializerOptions);
            if (snapshot is null)
            {
                return;
            }

            _isSidebarCollapsed = snapshot.IsSidebarCollapsed;
            _isRightPanelOpen = snapshot.IsRightPanelOpen;
            _rightPanelWidth = ClampWidth(snapshot.RightPanelWidth);
            _selectedPanel = Enum.IsDefined(snapshot.SelectedPanel)
                ? snapshot.SelectedPanel
                : WorkbenchPanelKind.Changes;
            _themePreference = Enum.IsDefined(snapshot.ThemePreference)
                ? snapshot.ThemePreference
                : AppThemePreference.Dark;
            _terminalFontFamily = NormalizeTerminalFontFamily(snapshot.TerminalFontFamily);
            _terminalFontSize = NormalizeTerminalFontSize(
                snapshot.TerminalFontSize ?? DefaultTerminalFontSize);
            _terminalPaneLayouts.Clear();
            if (snapshot.TerminalPaneLayouts is not null)
            {
                foreach (var (projectId, preference) in snapshot.TerminalPaneLayouts)
                {
                    if (!string.IsNullOrWhiteSpace(projectId) && preference is not null)
                    {
                        _terminalPaneLayouts[projectId] = NormalizeTerminalPaneLayout(preference);
                    }
                }
            }

            _previewUrls.Clear();
            if (snapshot.PreviewUrls is not null)
            {
                foreach (var (projectId, url) in snapshot.PreviewUrls)
                {
                    if (!string.IsNullOrWhiteSpace(projectId) &&
                        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                        uri.Scheme is "http" or "https" &&
                        string.IsNullOrEmpty(uri.UserInfo) &&
                        uri.AbsoluteUri.Length <= PreviewDiscoveryDefaults.MaximumUrlLength)
                    {
                        _previewUrls[projectId] = uri.AbsoluteUri;
                    }
                }
            }

            _previewWorkspaces.Clear();
            if (snapshot.PreviewWorkspaces is not null)
            {
                foreach (var (contextKey, preference) in snapshot.PreviewWorkspaces)
                {
                    if (!string.IsNullOrWhiteSpace(contextKey) && preference is not null)
                    {
                        _previewWorkspaces[contextKey] = NormalizePreviewWorkspace(preference);
                    }
                }
            }

            _previewAutomationPermissions.Clear();
            if (snapshot.PreviewAutomationPermissions is not null)
            {
                foreach (var (contextKey, permission) in snapshot.PreviewAutomationPermissions)
                {
                    if (!string.IsNullOrWhiteSpace(contextKey) && Enum.IsDefined(permission) &&
                        permission != PreviewAutomationAccess.Off)
                    {
                        _previewAutomationPermissions[contextKey] = permission;
                    }
                }
            }

            _browserProfiles.Clear();
            _browserProfiles.Add(new BrowserProfilePreference("default", "Default"));
            foreach (var profile in snapshot.BrowserProfiles ?? [])
            {
                var id = profile?.Id?.Trim() ?? string.Empty;
                var name = profile?.Name?.Trim() ?? string.Empty;
                if (id is { Length: > 0 and <= 64 } && name is { Length: > 0 and <= 48 } &&
                    !string.Equals(id, "default", StringComparison.Ordinal) &&
                    !_browserProfiles.Any(candidate => candidate.Id == id) &&
                    _browserProfiles.Count < 12)
                {
                    _browserProfiles.Add(new BrowserProfilePreference(id, name));
                }
            }

            _defaultBrowserProfileId = _browserProfiles.Any(profile => profile.Id == snapshot.DefaultBrowserProfileId)
                ? snapshot.DefaultBrowserProfileId!
                : "default";
            _previewDevToolsPolicy = snapshot.PreviewDevToolsPolicy is { } devToolsPolicy && Enum.IsDefined(devToolsPolicy)
                ? devToolsPolicy
                : PreviewDevToolsPolicy.Disabled;

            _commandKeybindings.Clear();
            foreach (var binding in snapshot.CommandKeybindings ?? [])
            {
                var commandId = binding?.CommandId?.Trim() ?? string.Empty;
                var gesture = binding?.Gesture?.Trim() ?? string.Empty;
                var when = string.IsNullOrWhiteSpace(binding?.When) ? null : binding.When.Trim();
                if (commandId.Length is > 0 and <= 128 &&
                    gesture.Length is > 0 and <= 64 &&
                    (when is null || when.Length <= 256) &&
                    !_commandKeybindings.Any(candidate =>
                        string.Equals(candidate.CommandId, commandId, StringComparison.Ordinal)))
                {
                    _commandKeybindings.Add(new CommandKeybindingPreference(commandId, gesture, when));
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (JsonException)
        {
        }
    }

    private void Save()
    {
        if (_settingsPath is null)
        {
            return;
        }

        var temporaryPath = _settingsPath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var snapshot = new LayoutSettings(
                IsSidebarCollapsed,
                IsRightPanelOpen,
                RightPanelWidth,
                SelectedPanel,
                ThemePreference,
                TerminalFontFamily,
                TerminalFontSize,
                _terminalPaneLayouts,
                _previewUrls,
                _previewWorkspaces,
                _commandKeybindings,
                _previewAutomationPermissions,
                _browserProfiles,
                _defaultBrowserProfileId,
                _previewDevToolsPolicy);
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(snapshot, SerializerOptions));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        catch (IOException)
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record LayoutSettings(
        bool IsSidebarCollapsed,
        bool IsRightPanelOpen,
        double RightPanelWidth,
        WorkbenchPanelKind SelectedPanel,
        AppThemePreference ThemePreference,
        string? TerminalFontFamily = null,
        double? TerminalFontSize = null,
        IReadOnlyDictionary<string, TerminalPaneLayoutPreference>? TerminalPaneLayouts = null,
        IReadOnlyDictionary<string, string>? PreviewUrls = null,
        IReadOnlyDictionary<string, PreviewWorkspacePreference>? PreviewWorkspaces = null,
        IReadOnlyList<CommandKeybindingPreference>? CommandKeybindings = null,
        IReadOnlyDictionary<string, PreviewAutomationAccess>? PreviewAutomationPermissions = null,
        IReadOnlyList<BrowserProfilePreference>? BrowserProfiles = null,
        string? DefaultBrowserProfileId = null,
        PreviewDevToolsPolicy? PreviewDevToolsPolicy = null);
}

public sealed record CommandKeybindingPreference(
    string CommandId,
    string Gesture,
    string? When = null);

public sealed record PreviewWorkspacePreference(
    string? ActiveTabId,
    IReadOnlyList<PreviewTabPreference> Tabs,
    IReadOnlyList<string>? RecentUrls = null);

public sealed record PreviewTabPreference(
    string TabId,
    string Url,
    string Title,
    PreviewViewportPreset ViewportPreset,
    int ViewportWidth,
    int ViewportHeight,
    double ZoomFactor = 1,
    PreviewColorScheme ColorScheme = PreviewColorScheme.System,
    string ProfileId = "default");

public sealed record BrowserProfilePreference(string Id, string Name);

public sealed record TerminalPaneLayoutPreference(
    bool IsSplit,
    TerminalSplitOrientation SplitOrientation,
    double SplitRatio,
    int ActivePaneIndex,
    string? PrimarySessionId,
    string? SecondarySessionId,
    TerminalPaneLayoutNodePreference? Root = null);

public sealed record TerminalPaneLayoutNodePreference(
    string? NodeId,
    string? PaneId,
    string? SessionId,
    TerminalSplitOrientation? SplitOrientation,
    double SplitRatio,
    TerminalPaneLayoutNodePreference? First,
    TerminalPaneLayoutNodePreference? Second);
