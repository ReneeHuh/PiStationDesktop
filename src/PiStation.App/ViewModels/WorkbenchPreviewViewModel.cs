using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public enum PreviewFailureKind
{
    None,
    InvalidAddress,
    Navigation,
    BrowserProcess,
    Initialization,
}

public enum PreviewViewportPreset
{
    Responsive,
    Desktop,
    Tablet,
    Phone,
    Freeform,
}

public sealed class WorkbenchPreviewViewModel : ObservableObject
{
    public const int MaximumTabs = 12;

    private string _blankAddressText = string.Empty;
    private string _discoveryStatus = "Select a workspace to discover local servers";
    private bool _hasProject;
    private bool _isDiscovering;
    private WorkbenchPreviewTabViewModel? _activeTab;
    private string _defaultProfileId = "default";
    private BrowserDefaults _defaults = new();
    private PreviewAutomationAccess _automationPermission;

    private IReadOnlyList<DiscoveredPreviewServer> _allServers = [];
    private PiStation.Protocol.Identifiers.ThreadId? _discoveryThreadId;
    private bool _discoveryTruncated;
    private int _discoveryScopeIndex;
    public int DiscoveryScopeIndex
    {
        get => _discoveryScopeIndex;
        set { if (SetProperty(ref _discoveryScopeIndex, value == 1 ? 1 : 0)) UpdateDiscoveredServers(); }
    }
    public ObservableCollection<DiscoveredPreviewServerRow> DiscoveredServers { get; } = [];

    public ObservableCollection<WorkbenchPreviewTabViewModel> Tabs { get; } = [];

    public ObservableCollection<string> RecentUrls { get; } = [];

    public ObservableCollection<BrowserProfilePreference> BrowserProfiles { get; } = [];

    public WorkbenchPreviewTabViewModel? ActiveTab
    {
        get => _activeTab;
        internal set
        {
            if (ReferenceEquals(_activeTab, value))
            {
                return;
            }

            if (_activeTab is not null)
            {
                _activeTab.PropertyChanged -= OnActiveTabPropertyChanged;
                _activeTab.IsActive = false;
            }

            if (!SetProperty(ref _activeTab, value))
            {
                return;
            }

            if (_activeTab is not null)
            {
                _activeTab.IsActive = true;
                _activeTab.PropertyChanged += OnActiveTabPropertyChanged;
                _blankAddressText = _activeTab.AddressText;
            }

            RaiseActiveTabProperties();
        }
    }

    public string AddressText
    {
        get => ActiveTab?.AddressText ?? _blankAddressText;
        internal set
        {
            var normalized = value ?? string.Empty;
            if (ActiveTab is { } tab)
            {
                tab.AddressText = normalized;
            }
            else if (!string.Equals(_blankAddressText, normalized, StringComparison.Ordinal))
            {
                _blankAddressText = normalized;
                OnPropertyChanged();
            }
        }
    }

    public string CurrentUrl => ActiveTab?.CurrentUrl ?? string.Empty;

    public string DocumentTitle => ActiveTab?.DocumentTitle ?? "Preview";

    public string DiscoveryStatus
    {
        get => _discoveryStatus;
        private set
        {
            if (SetProperty(ref _discoveryStatus, value))
            {
                OnPropertyChanged(nameof(DiscoveryStatusVisibility));
            }
        }
    }

    public Visibility DiscoveryStatusVisibility => string.IsNullOrWhiteSpace(DiscoveryStatus)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public bool HasProject
    {
        get => _hasProject;
        private set
        {
            if (SetProperty(ref _hasProject, value))
            {
                OnPropertyChanged(nameof(CanDiscover));
                OnPropertyChanged(nameof(CanNavigate));
                OnPropertyChanged(nameof(TabStripVisibility));
            }
        }
    }

    public bool IsDiscovering
    {
        get => _isDiscovering;
        internal set
        {
            if (SetProperty(ref _isDiscovering, value))
            {
                OnPropertyChanged(nameof(CanDiscover));
                OnPropertyChanged(nameof(DiscoveryProgressVisibility));
            }
        }
    }

    public Visibility DiscoveryProgressVisibility => IsDiscovering
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool IsLoading => ActiveTab?.IsLoading == true;

    public Visibility LoadingProgressVisibility => IsLoading
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool CanGoBack => ActiveTab?.CanGoBack == true;

    public bool CanGoForward => ActiveTab?.CanGoForward == true;

    public bool CanDiscover => HasProject && !IsDiscovering;

    public bool CanNavigate => HasProject;

    public bool CanReloadOrStop => !string.IsNullOrWhiteSpace(CurrentUrl);

    public bool CanOpenExternal => TryNormalizeAddress(CurrentUrl, out _, out _);

    public bool CanCapture => ActiveTab is { CanCapture: true, IsPickingElement: false };

    public bool CanAnnotate => ActiveTab is { CanCapture: true };

    public bool CanAddTab => HasProject && Tabs.Count < MaximumTabs;

    public bool CanCloseTab => ActiveTab is not null;

    public string ReloadStopGlyph => IsLoading ? "\uE71A" : "\uE72C";

    public string ReloadStopLabel => IsLoading ? "Stop loading preview" : "Reload preview";

    public string AnnotationLabel => ActiveTab?.IsPickingElement == true
        ? "Cancel element annotation"
        : "Annotate preview element";

    public string CaptureStatus => ActiveTab?.CaptureStatus ?? string.Empty;

    public string? LastCapturePath => ActiveTab?.LastCapturePath;

    public bool CanRevealCapture => !string.IsNullOrWhiteSpace(LastCapturePath);

    public Visibility CaptureStatusVisibility => string.IsNullOrWhiteSpace(CaptureStatus)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public int ViewportPresetIndex => ActiveTab?.ViewportPresetIndex ?? 0;

    public string ViewportDescription => ActiveTab?.ViewportDescription ?? "Responsive";

    public Visibility ViewportControlsVisibility => ActiveTab is null
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility TabStripVisibility => HasProject ? Visibility.Visible : Visibility.Collapsed;

    public PreviewFailureKind FailureKind => ActiveTab?.FailureKind ?? PreviewFailureKind.None;

    public string FailureMessage => ActiveTab?.FailureMessage ?? string.Empty;

    public Visibility FailureVisibility => FailureKind == PreviewFailureKind.None
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility EmptyStateVisibility => string.IsNullOrWhiteSpace(CurrentUrl)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility BrowserVisibility => string.IsNullOrWhiteSpace(CurrentUrl)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public double ZoomFactor => ActiveTab?.ZoomFactor ?? 1;

    public string ZoomDescription => $"{Math.Round(ZoomFactor * 100)}%";

    public int ColorSchemeIndex => (int)(ActiveTab?.ColorScheme ?? PreviewColorScheme.System);

    public BrowserProfilePreference? SelectedProfile => BrowserProfiles.FirstOrDefault(profile =>
        string.Equals(profile.Id, ActiveTab?.ProfileId, StringComparison.Ordinal));

    public bool CanChangeProfile => ActiveTab is { CurrentUrl.Length: 0, HasNavigated: false };
    public bool CanMakeDefaultProfile => SelectedProfile is { Id: not "incognito" };

    public bool IsRecording => ActiveTab?.IsRecording == true;

    public string RecordingLabel => IsRecording ? "Stop recording" : "Start recording";

    public int AutomationPermissionIndex => (int)_automationPermission;

    public PreviewAutomationAccess AutomationPermission => _automationPermission;

    public string AutomationDescription => _automationPermission switch
    {
        PreviewAutomationAccess.Inspect => "Agent browser access: inspect only",
        PreviewAutomationAccess.Interact => "Agent browser access: inspect and interact",
        _ => "Agent browser access: off",
    };

    internal void Reset(
        bool hasProject,
        PreviewWorkspacePreference? savedWorkspace = null,
        string? legacySavedUrl = null,
        IReadOnlyList<BrowserProfilePreference>? browserProfiles = null,
        string defaultProfileId = "default",
        PreviewAutomationAccess automationPermission = PreviewAutomationAccess.Off,
        BrowserDefaults? defaults = null)
    {
        _defaults = (defaults ?? new()).Normalize();
        ActiveTab = null;
        Tabs.Clear();
        HasProject = hasProject;
        IsDiscovering = false;
        DiscoveredServers.Clear();
        _allServers = [];
        _discoveryThreadId = null;
        RecentUrls.Clear();
        BrowserProfiles.Clear();
        foreach (var profile in browserProfiles ?? [new BrowserProfilePreference("default", "Default")])
        {
            if (!BrowserProfiles.Any(candidate => candidate.Id == profile.Id))
            {
                BrowserProfiles.Add(profile);
            }
        }

        if (BrowserProfiles.Count == 0)
        {
            BrowserProfiles.Add(new BrowserProfilePreference("default", "Default"));
        }

        BrowserProfiles.Add(new("incognito", "Incognito"));
        _defaultProfileId = BrowserProfiles.Any(profile => profile.Id == defaultProfileId && profile.Id != "incognito")
            ? defaultProfileId
            : BrowserProfiles[0].Id;
        _automationPermission = Enum.IsDefined(automationPermission)
            ? automationPermission
            : PreviewAutomationAccess.Off;
        _blankAddressText = string.Empty;

        if (hasProject && savedWorkspace is not null)
        {
            foreach (var preference in savedWorkspace.Tabs.Take(MaximumTabs))
            {
                var tab = WorkbenchPreviewTabViewModel.FromPreference(
                    preference,
                    BrowserProfiles,
                    _defaultProfileId);
                if (tab is not null)
                {
                    Tabs.Add(tab);
                }
            }

            ActiveTab = Tabs.FirstOrDefault(tab =>
                string.Equals(tab.TabId, savedWorkspace.ActiveTabId, StringComparison.Ordinal)) ??
                Tabs.LastOrDefault();
            foreach (var recentUrl in savedWorkspace.RecentUrls ?? [])
            {
                AddRecentUrl(recentUrl);
            }
        }

        if (hasProject && Tabs.Count == 0 && TryNormalizeAddress(legacySavedUrl, out var legacyUri, out _))
        {
            var migrated = new WorkbenchPreviewTabViewModel(Guid.NewGuid().ToString("N"), _defaultProfileId);
            migrated.Restore(legacyUri.AbsoluteUri, legacyUri.Host, PreviewViewportPreset.Responsive, 0, 0);
            Tabs.Add(migrated);
            ActiveTab = migrated;
        }

        DiscoveryStatus = hasProject
            ? Tabs.Count > 0
                ? "Restored preview tabs for this thread"
                : "Looking for local development servers…"
            : "Select a workspace to discover local servers";
        RaiseTabCollectionProperties();
        OnPropertyChanged(nameof(BrowserProfiles));
        OnPropertyChanged(nameof(RecentUrls));
        OnPropertyChanged(nameof(AutomationPermissionIndex));
        OnPropertyChanged(nameof(AutomationPermission));
        OnPropertyChanged(nameof(AutomationDescription));
    }

    internal WorkbenchPreviewTabViewModel AddTab(string? initialUrl = null)
    {
        if (Tabs.Count >= MaximumTabs)
        {
            throw new InvalidOperationException("The preview tab limit was reached.");
        }

        var tab = new WorkbenchPreviewTabViewModel(Guid.NewGuid().ToString("N"), _defaultProfileId);
        tab.Restore(initialUrl, null, _defaults.Viewport, 0, 0, _defaults.ZoomFactor, _defaults.Appearance);

        Tabs.Add(tab);
        ActiveTab = tab;
        RaiseTabCollectionProperties();
        return tab;
    }

    internal void CloseTab(WorkbenchPreviewTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        Tabs.RemoveAt(index);
        if (ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = Tabs.Count == 0
                ? null
                : Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        RaiseTabCollectionProperties();
    }

    internal PreviewWorkspacePreference CreatePreference() => new(
        ActiveTab?.ProfileId == "incognito" ? null : ActiveTab?.TabId,
        Tabs.Where(static tab => tab.ProfileId != "incognito").Select(static tab => tab.CreatePreference()).ToArray(),
        RecentUrls.ToArray());

    internal void BeginDiscovery()
    {
        IsDiscovering = true;
        DiscoveryStatus = "Looking for local development servers…";
    }

    internal void ApplyDiscovery(DiscoverProjectPreviewServersResult result,
        PiStation.Protocol.Identifiers.ThreadId? threadId = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        _allServers = result.Servers;
        _discoveryThreadId = threadId;
        _discoveryTruncated = result.IsTruncated;
        IsDiscovering = false;
        UpdateDiscoveredServers();
    }

    private void UpdateDiscoveredServers()
    {
        var rows = _allServers.Where(server => DiscoveryScopeIndex == 0 ||
            _discoveryThreadId is not null && server.Terminal?.ThreadId == _discoveryThreadId)
            .Select(server => new DiscoveredPreviewServerRow(server, _discoveryThreadId)).ToArray();
        if (!DiscoveredServers.SequenceEqual(rows))
        {
            DiscoveredServers.Clear();
            foreach (var row in rows) DiscoveredServers.Add(row);
        }
        DiscoveryStatus = DiscoveredServers.Count == 0
            ? DiscoveryScopeIndex == 1 ? "No servers owned by terminals in this thread." : "No local web servers found. Start your development server, then refresh."
            : $"{DiscoveredServers.Count} local server(s)" + (_discoveryTruncated ? " • results limited" : string.Empty);
    }

    internal void FailDiscovery(string message)
    {
        IsDiscovering = false;
        DiscoveryStatus = $"Server discovery unavailable: {message}";
    }

    internal bool PrepareNavigation(string? address, out WorkbenchPreviewTabViewModel? tab, out Uri? uri)
    {
        if (!TryNormalizeAddress(address, out var normalized, out var error))
        {
            tab = ActiveTab;
            uri = null;
            if (tab is null)
            {
                _blankAddressText = address ?? string.Empty;
                OnPropertyChanged(nameof(AddressText));
            }
            else
            {
                tab.ReportFailure(PreviewFailureKind.InvalidAddress, error);
            }

            RaiseActiveTabProperties();
            return false;
        }

        tab = ActiveTab ?? AddTab();
        tab.PrepareNavigation(normalized);
        if (tab.ProfileId != "incognito") AddRecentUrl(normalized.AbsoluteUri);
        uri = normalized;
        return true;
    }

    internal void ReportNavigationStarted(string? tabId, Uri uri) =>
        FindTab(tabId)?.ReportNavigationStarted(uri);

    internal void ReportBrowserState(
        string? tabId,
        string? source,
        string? title,
        bool canGoBack,
        bool canGoForward) =>
        FindTab(tabId)?.ReportBrowserState(source, title, canGoBack, canGoForward);

    internal void ReportNavigationCompleted(string? tabId, bool succeeded, string? message) =>
        FindTab(tabId)?.ReportNavigationCompleted(succeeded, message);

    internal void ReportBrowserFailure(string? tabId, PreviewFailureKind kind, string message) =>
        FindTab(tabId)?.ReportFailure(kind, message);

    internal void ReturnToServers()
    {
        ActiveTab?.ReturnToServers();
        DiscoveryStatus = "Looking for local development servers…";
    }

    internal void RestoreAddressDraft()
    {
        if (ActiveTab is { } tab)
        {
            tab.RestoreAddressDraft();
        }
        else
        {
            _blankAddressText = string.Empty;
            OnPropertyChanged(nameof(AddressText));
        }
    }

    internal void ApplyViewportPreset(int index) => ActiveTab?.ApplyViewportPreset(index);

    internal void RotateViewport() => ActiveTab?.RotateViewport();

    internal void AdjustZoom(double delta) => ActiveTab?.SetZoom(ZoomFactor + delta);

    internal void ResetZoom() => ActiveTab?.SetZoom(1);

    internal void SetColorScheme(int index)
    {
        if (ActiveTab is { } tab)
        {
            tab.SetColorScheme(Enum.IsDefined(typeof(PreviewColorScheme), index)
                ? (PreviewColorScheme)index
                : PreviewColorScheme.System);
        }
    }

    internal void SelectProfile(BrowserProfilePreference profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (CanChangeProfile && BrowserProfiles.Contains(profile))
        {
            ActiveTab?.SetProfile(profile.Id);
        }
    }

    internal void ReplaceProfiles(IReadOnlyList<BrowserProfilePreference> profiles, string defaultProfileId)
    {
        BrowserProfiles.Clear();
        foreach (var profile in profiles)
        {
            BrowserProfiles.Add(profile);
        }

        BrowserProfiles.Add(new("incognito", "Incognito"));
        _defaultProfileId = BrowserProfiles.Any(profile => profile.Id == defaultProfileId && profile.Id != "incognito")
            ? defaultProfileId
            : BrowserProfiles.FirstOrDefault()?.Id ?? "default";
        OnPropertyChanged(nameof(BrowserProfiles));
        RaiseActiveTabProperties();
    }

    internal void SetDefaults(BrowserDefaults defaults) => _defaults = defaults.Normalize();

    internal void SetAutomationPermission(PreviewAutomationAccess permission)
    {
        var normalized = Enum.IsDefined(permission) ? permission : PreviewAutomationAccess.Off;
        if (_automationPermission == normalized)
        {
            return;
        }

        _automationPermission = normalized;
        OnPropertyChanged(nameof(AutomationPermissionIndex));
        OnPropertyChanged(nameof(AutomationPermission));
        OnPropertyChanged(nameof(AutomationDescription));
    }

    internal void SetRecording(bool recording)
    {
        if (ActiveTab is { } tab)
        {
            tab.IsRecording = recording;
        }
    }

    internal void UpdateResponsiveViewport(double width, double height)
    {
        foreach (var tab in Tabs)
        {
            tab.UpdateResponsiveViewport(width, height);
        }
    }

    internal void BeginElementPick()
    {
        if (ActiveTab is { } tab)
        {
            tab.IsPickingElement = true;
            tab.CaptureStatus = "Select an element in the preview, or press Escape to cancel";
        }
    }

    internal void CompleteElementPick(string status)
    {
        if (ActiveTab is { } tab)
        {
            tab.IsPickingElement = false;
            tab.CaptureStatus = status;
        }
    }

    internal void SetCaptureStatus(string? tabId, string status, string? capturePath = null)
    {
        if (FindTab(tabId) is { } tab)
        {
            tab.CaptureStatus = status;
            if (!string.IsNullOrWhiteSpace(capturePath))
            {
                tab.LastCapturePath = capturePath;
            }
        }
    }

    internal WorkbenchPreviewTabViewModel? FindTab(string? tabId) =>
        string.IsNullOrWhiteSpace(tabId)
            ? null
            : Tabs.FirstOrDefault(tab => string.Equals(tab.TabId, tabId, StringComparison.Ordinal));

    public static bool TryNormalizeAddress(string? value, out Uri uri, out string error)
    {
        uri = null!;
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
        {
            error = "Enter an HTTP or HTTPS address.";
            return false;
        }

        if (candidate.Length > PreviewDiscoveryDefaults.MaximumUrlLength)
        {
            error = $"The address exceeds {PreviewDiscoveryDefaults.MaximumUrlLength} characters.";
            return false;
        }

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"http://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) ||
            !(string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            parsed.AbsoluteUri.Length > PreviewDiscoveryDefaults.MaximumUrlLength)
        {
            error = "Only HTTP and HTTPS addresses without embedded credentials can be previewed.";
            return false;
        }

        uri = parsed;
        error = string.Empty;
        return true;
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        RaiseActiveTabProperties();

    private void AddRecentUrl(string? url)
    {
        if (!TryNormalizeAddress(url, out var uri, out _))
        {
            return;
        }

        var existing = RecentUrls.FirstOrDefault(candidate =>
            string.Equals(candidate, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            RecentUrls.Remove(existing);
        }

        RecentUrls.Insert(0, uri.AbsoluteUri);
        while (RecentUrls.Count > 20)
        {
            RecentUrls.RemoveAt(RecentUrls.Count - 1);
        }

        OnPropertyChanged(nameof(RecentUrls));
    }

    private void RaiseTabCollectionProperties()
    {
        OnPropertyChanged(nameof(Tabs));
        OnPropertyChanged(nameof(CanAddTab));
        OnPropertyChanged(nameof(CanCloseTab));
        OnPropertyChanged(nameof(TabStripVisibility));
        RaiseActiveTabProperties();
    }

    private void RaiseActiveTabProperties()
    {
        OnPropertyChanged(nameof(AddressText));
        OnPropertyChanged(nameof(CurrentUrl));
        OnPropertyChanged(nameof(DocumentTitle));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(LoadingProgressVisibility));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanReloadOrStop));
        OnPropertyChanged(nameof(CanOpenExternal));
        OnPropertyChanged(nameof(CanCapture));
        OnPropertyChanged(nameof(CanAnnotate));
        OnPropertyChanged(nameof(CanCloseTab));
        OnPropertyChanged(nameof(ReloadStopGlyph));
        OnPropertyChanged(nameof(ReloadStopLabel));
        OnPropertyChanged(nameof(AnnotationLabel));
        OnPropertyChanged(nameof(CaptureStatus));
        OnPropertyChanged(nameof(CaptureStatusVisibility));
        OnPropertyChanged(nameof(LastCapturePath));
        OnPropertyChanged(nameof(CanRevealCapture));
        OnPropertyChanged(nameof(ViewportPresetIndex));
        OnPropertyChanged(nameof(ViewportDescription));
        OnPropertyChanged(nameof(ViewportControlsVisibility));
        OnPropertyChanged(nameof(FailureKind));
        OnPropertyChanged(nameof(FailureMessage));
        OnPropertyChanged(nameof(FailureVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(BrowserVisibility));
        OnPropertyChanged(nameof(ZoomFactor));
        OnPropertyChanged(nameof(ZoomDescription));
        OnPropertyChanged(nameof(ColorSchemeIndex));
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(CanChangeProfile));
        OnPropertyChanged(nameof(CanMakeDefaultProfile));
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(RecordingLabel));
    }
}

public sealed class WorkbenchPreviewTabViewModel : ObservableObject
{
    private string _addressText = string.Empty;
    private bool _canGoBack;
    private bool _canGoForward;
    private string _captureStatus = string.Empty;
    private string _currentUrl = string.Empty;
    private string _documentTitle = "New preview";
    private PreviewFailureKind _failureKind;
    private string _failureMessage = string.Empty;
    private bool _isActive;
    private bool _isLoading;
    private bool _isPickingElement;
    private string? _lastCapturePath;
    private PreviewViewportPreset _viewportPreset;
    private int _viewportWidth;
    private int _viewportHeight;
    private double _responsiveWidth = 240;
    private double _responsiveHeight = 240;
    private double _zoomFactor = 1;
    private PreviewColorScheme _colorScheme;
    private string _profileId;
    private bool _isRecording;

    public WorkbenchPreviewTabViewModel(string tabId, string profileId = "default")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        TabId = tabId;
        _profileId = string.IsNullOrWhiteSpace(profileId) ? "default" : profileId.Trim();
    }

    public string TabId { get; }
    internal bool HasNavigated { get; private set; }
    internal void MarkBrowserStarted()
    {
        if (HasNavigated) return;
        HasNavigated = true;
        OnPropertyChanged(nameof(HasNavigated));
    }

    public string AddressText
    {
        get => _addressText;
        internal set => SetProperty(ref _addressText, value ?? string.Empty);
    }

    public string CurrentUrl
    {
        get => _currentUrl;
        private set
        {
            if (SetProperty(ref _currentUrl, value))
            {
                OnPropertyChanged(nameof(CanCapture));
                OnPropertyChanged(nameof(SurfaceVisibility));
            }
        }
    }

    public string DocumentTitle
    {
        get => _documentTitle;
        private set
        {
            if (SetProperty(ref _documentTitle, value))
            {
                OnPropertyChanged(nameof(TabTitle));
            }
        }
    }

    public string TabTitle => IsLoading ? $"{DocumentTitle} …" : DocumentTitle;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(TabTitle));
                OnPropertyChanged(nameof(CanCapture));
            }
        }
    }

    public bool CanGoBack
    {
        get => _canGoBack;
        private set => SetProperty(ref _canGoBack, value);
    }

    public bool CanGoForward
    {
        get => _canGoForward;
        private set => SetProperty(ref _canGoForward, value);
    }

    public bool CanCapture => !string.IsNullOrWhiteSpace(CurrentUrl) && !IsLoading;

    public bool IsPickingElement
    {
        get => _isPickingElement;
        internal set => SetProperty(ref _isPickingElement, value);
    }

    public string CaptureStatus
    {
        get => _captureStatus;
        internal set => SetProperty(ref _captureStatus, value ?? string.Empty);
    }

    public string? LastCapturePath
    {
        get => _lastCapturePath;
        internal set => SetProperty(ref _lastCapturePath, value);
    }

    public PreviewFailureKind FailureKind
    {
        get => _failureKind;
        private set => SetProperty(ref _failureKind, value);
    }

    public string FailureMessage
    {
        get => _failureMessage;
        private set => SetProperty(ref _failureMessage, value ?? string.Empty);
    }

    public bool IsActive
    {
        get => _isActive;
        internal set
        {
            if (SetProperty(ref _isActive, value))
            {
                OnPropertyChanged(nameof(SurfaceVisibility));
            }
        }
    }

    public Visibility SurfaceVisibility => IsActive && !string.IsNullOrWhiteSpace(CurrentUrl)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public int ViewportPresetIndex => (int)_viewportPreset;

    public double SurfaceWidth => _viewportPreset == PreviewViewportPreset.Responsive
        ? _responsiveWidth
        : _viewportWidth;

    public double SurfaceHeight => _viewportPreset == PreviewViewportPreset.Responsive
        ? _responsiveHeight
        : _viewportHeight;

    public string ViewportDescription => _viewportPreset == PreviewViewportPreset.Responsive
        ? $"Responsive • {Math.Round(_responsiveWidth)} × {Math.Round(_responsiveHeight)}"
        : $"{_viewportPreset} • {_viewportWidth} × {_viewportHeight}";

    public double ZoomFactor => _zoomFactor;

    public PreviewColorScheme ColorScheme => _colorScheme;

    public string ProfileId => _profileId;

    public bool IsRecording
    {
        get => _isRecording;
        internal set => SetProperty(ref _isRecording, value);
    }

    internal static WorkbenchPreviewTabViewModel? FromPreference(
        PreviewTabPreference preference,
        IReadOnlyList<BrowserProfilePreference> profiles,
        string defaultProfileId)
    {
        if (preference is null || string.IsNullOrWhiteSpace(preference.TabId))
        {
            return null;
        }

        var profileId = profiles.Any(profile => profile.Id == preference.ProfileId)
            ? preference.ProfileId
            : defaultProfileId;
        var tab = new WorkbenchPreviewTabViewModel(preference.TabId.Trim(), profileId);
        tab.Restore(
            preference.Url,
            preference.Title,
            Enum.IsDefined(preference.ViewportPreset)
                ? preference.ViewportPreset
                : PreviewViewportPreset.Responsive,
            preference.ViewportWidth,
            preference.ViewportHeight,
            preference.ZoomFactor,
            preference.ColorScheme);
        return tab;
    }

    internal PreviewTabPreference CreatePreference() => new(
        TabId,
        CurrentUrl,
        DocumentTitle,
        _viewportPreset,
        _viewportWidth,
        _viewportHeight,
        _zoomFactor,
        _colorScheme,
        _profileId);

    internal void Restore(
        string? url,
        string? title,
        PreviewViewportPreset preset,
        int width,
        int height,
        double zoomFactor = 1,
        PreviewColorScheme colorScheme = PreviewColorScheme.System)
    {
        if (WorkbenchPreviewViewModel.TryNormalizeAddress(url, out var uri, out _))
        {
            CurrentUrl = uri.AbsoluteUri;
            HasNavigated = true;
            AddressText = uri.AbsoluteUri;
            DocumentTitle = string.IsNullOrWhiteSpace(title) ? uri.Host : title.Trim();
        }
        else
        {
            CurrentUrl = string.Empty;
            AddressText = string.Empty;
            DocumentTitle = "New preview";
        }

        ApplyViewport(preset, width, height);
        SetZoom(zoomFactor);
        SetColorScheme(colorScheme);
        FailureKind = PreviewFailureKind.None;
        FailureMessage = string.Empty;
        IsLoading = false;
    }

    internal void PrepareNavigation(Uri uri)
    {
        HasNavigated = true;
        CurrentUrl = uri.AbsoluteUri;
        AddressText = uri.AbsoluteUri;
        DocumentTitle = uri.Host;
        FailureKind = PreviewFailureKind.None;
        FailureMessage = string.Empty;
        CaptureStatus = string.Empty;
        LastCapturePath = null;
        IsLoading = true;
    }

    internal void ReportNavigationStarted(Uri uri)
    {
        CurrentUrl = uri.AbsoluteUri;
        AddressText = uri.AbsoluteUri;
        FailureKind = PreviewFailureKind.None;
        FailureMessage = string.Empty;
        IsLoading = true;
    }

    internal void ReportBrowserState(string? source, string? title, bool canGoBack, bool canGoForward)
    {
        if (WorkbenchPreviewViewModel.TryNormalizeAddress(source, out var uri, out _))
        {
            CurrentUrl = uri.AbsoluteUri;
            AddressText = uri.AbsoluteUri;
        }

        DocumentTitle = string.IsNullOrWhiteSpace(title)
            ? WorkbenchPreviewViewModel.TryNormalizeAddress(CurrentUrl, out var current, out _)
                ? current.Host
                : "New preview"
            : title.Trim();
        CanGoBack = canGoBack;
        CanGoForward = canGoForward;
    }

    internal void ReportNavigationCompleted(bool succeeded, string? message)
    {
        IsLoading = false;
        if (succeeded)
        {
            FailureKind = PreviewFailureKind.None;
            FailureMessage = string.Empty;
            return;
        }

        FailureKind = PreviewFailureKind.Navigation;
        FailureMessage = string.IsNullOrWhiteSpace(message)
            ? "The page could not be loaded. Check that the server is running, then reload."
            : message;
    }

    internal void ReportFailure(PreviewFailureKind kind, string message)
    {
        IsLoading = false;
        FailureKind = kind;
        FailureMessage = message;
    }

    internal void ReturnToServers()
    {
        CurrentUrl = string.Empty;
        AddressText = string.Empty;
        DocumentTitle = "New preview";
        FailureKind = PreviewFailureKind.None;
        FailureMessage = string.Empty;
        CaptureStatus = string.Empty;
        IsLoading = false;
        CanGoBack = false;
        CanGoForward = false;
    }

    internal void RestoreAddressDraft() => AddressText = CurrentUrl;

    internal void ApplyViewportPreset(int index)
    {
        var preset = Enum.IsDefined(typeof(PreviewViewportPreset), index)
            ? (PreviewViewportPreset)index
            : PreviewViewportPreset.Responsive;
        ApplyViewport(preset, 0, 0);
    }

    internal PiStation.ClientRuntime.BrowserViewportSetting ViewportSetting => _viewportPreset switch
    {
        PreviewViewportPreset.Responsive => new("fill"),
        PreviewViewportPreset.Freeform => new("freeform", _viewportWidth, _viewportHeight),
        _ => new("preset", _viewportWidth, _viewportHeight, _viewportPreset.ToString().ToLowerInvariant()),
    };
    internal long ViewportRevision { get; private set; }
    internal long AppearanceRevision { get; private set; }
    internal void ApplyAutomationViewport(PiStation.ClientRuntime.BrowserViewportSetting setting) =>
        ApplyViewport(setting.Mode == "fill" ? PreviewViewportPreset.Responsive : setting.Mode == "freeform" ? PreviewViewportPreset.Freeform :
            Enum.Parse<PreviewViewportPreset>(setting.Preset!, true), setting.Width, setting.Height);

    internal void RotateViewport()
    {
        if (_viewportPreset == PreviewViewportPreset.Responsive)
        {
            return;
        }

        (_viewportWidth, _viewportHeight) = (_viewportHeight, _viewportWidth);
        ViewportRevision++;
        RaiseViewportProperties();
    }

    internal void SetZoom(double value)
    {
        var normalized = double.IsFinite(value) ? Math.Clamp(Math.Round(value, 2), 0.25, 3) : 1;
        if (SetProperty(ref _zoomFactor, normalized, nameof(ZoomFactor)))
        {
            OnPropertyChanged(nameof(ViewportDescription));
        }
    }

    internal void SetColorScheme(PreviewColorScheme value)
    {
        if (SetProperty(
            ref _colorScheme,
            Enum.IsDefined(value) ? value : PreviewColorScheme.System,
            nameof(ColorScheme))) AppearanceRevision++;
    }

    internal void SetProfile(string profileId)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            SetProperty(ref _profileId, profileId.Trim(), nameof(ProfileId));
        }
    }

    internal void UpdateResponsiveViewport(double width, double height)
    {
        var normalizedWidth = Math.Max(240, Math.Floor(width));
        var normalizedHeight = Math.Max(240, Math.Floor(height));
        if (Math.Abs(_responsiveWidth - normalizedWidth) < 0.5 &&
            Math.Abs(_responsiveHeight - normalizedHeight) < 0.5)
        {
            return;
        }

        _responsiveWidth = normalizedWidth;
        _responsiveHeight = normalizedHeight;
        if (_viewportPreset == PreviewViewportPreset.Responsive)
        {
            RaiseViewportProperties();
        }
    }

    private void ApplyViewport(PreviewViewportPreset preset, int width, int height)
    {
        ViewportRevision++;
        _viewportPreset = preset;
        (_viewportWidth, _viewportHeight) = preset switch
        {
            PreviewViewportPreset.Desktop => NormalizeFixedSize(width, height, 1440, 900),
            PreviewViewportPreset.Tablet => NormalizeFixedSize(width, height, 768, 1024),
            PreviewViewportPreset.Phone => NormalizeFixedSize(width, height, 390, 844),
            PreviewViewportPreset.Freeform => NormalizeFixedSize(width, height, 1024, 768),
            _ => (0, 0),
        };
        RaiseViewportProperties();
    }

    private static (int Width, int Height) NormalizeFixedSize(
        int width,
        int height,
        int defaultWidth,
        int defaultHeight) =>
        width is >= 240 and <= 3840 && height is >= 240 and <= 3840 && width * (long)height <= 3840L * 2160L
            ? (width, height)
            : (defaultWidth, defaultHeight);

    private void RaiseViewportProperties()
    {
        OnPropertyChanged(nameof(ViewportPresetIndex));
        OnPropertyChanged(nameof(SurfaceWidth));
        OnPropertyChanged(nameof(SurfaceHeight));
        OnPropertyChanged(nameof(ViewportDescription));
    }
}

public sealed record DiscoveredPreviewServerRow(DiscoveredPreviewServer Server,
    PiStation.Protocol.Identifiers.ThreadId? CurrentThreadId)
{
    public string Url => Server.Url;
    public string Ownership => Server.Terminal is { } owner
        ? $"{owner.TerminalName} • {(CurrentThreadId is not null && owner.ThreadId == CurrentThreadId ? "This thread" : owner.ThreadId is null ? "Project terminal" : "Another thread")} • {Server.ProcessName ?? "Process"}"
        : $"{Server.ProcessName ?? "Server"} • No terminal owner";
}

public sealed record PreviewElementAnnotation(
    string PageUrl,
    string PageTitle,
    string ElementLabel,
    string Selector,
    string Text,
    string OuterHtml,
    double X,
    double Y,
    double Width,
    double Height,
    byte[] Screenshot);
