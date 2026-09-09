namespace PiStation.App.ViewModels;

public enum BrowserLinkTarget { System, App }

public sealed record BrowserDefaults(
    PreviewViewportPreset Viewport = PreviewViewportPreset.Responsive,
    double ZoomFactor = 1,
    PreviewColorScheme Appearance = PreviewColorScheme.System)
{
    public BrowserDefaults Normalize() => new(
        Enum.IsDefined(Viewport) ? Viewport : PreviewViewportPreset.Responsive,
        double.IsFinite(ZoomFactor) ? Math.Clamp(Math.Round(ZoomFactor, 2), 0.25, 3) : 1,
        Enum.IsDefined(Appearance) ? Appearance : PreviewColorScheme.System);
}

public sealed partial class ShellLayoutViewModel
{
    private BrowserDefaults _browserDefaults = new();
    private BrowserLinkTarget _browserLinkTarget;
    private BrowserSettingsStore? _browserSettingsStore;
    internal void ReleaseBrowserSettings() { if (_browserSettingsStore is not null) _browserSettingsStore.Changed -= RefreshSharedBrowserSettings; }

    public void UseSharedBrowserSettings(string path)
    {
        if (_browserSettingsStore is not null) _browserSettingsStore.Changed -= RefreshSharedBrowserSettings;
        _browserSettingsStore = BrowserSettingsStore.Open(path, CreateBrowserSettingsSnapshot());
        _browserSettingsStore.Changed += RefreshSharedBrowserSettings;
        RefreshSharedBrowserSettings();
    }

    private BrowserSettingsSnapshot CreateBrowserSettingsSnapshot() =>
        new(_browserDefaults, _browserLinkTarget, BrowserProfiles, _defaultBrowserProfileId);
    private void SaveBrowserSettings() => _browserSettingsStore?.Update(CreateBrowserSettingsSnapshot());
    private void RefreshSharedBrowserSettings()
    {
        var snapshot = _browserSettingsStore!.Snapshot;
        _browserDefaults = (snapshot.Defaults ?? new()).Normalize();
        _browserLinkTarget = Enum.IsDefined(snapshot.LinkTarget) ? snapshot.LinkTarget : BrowserLinkTarget.System;
        _browserProfiles.Clear();
        _browserProfiles.Add(new("default", "Default"));
        foreach (var profile in snapshot.Profiles ?? [])
            if (profile is { Id.Length: > 0 and <= 64, Name.Length: > 0 and <= 48 } &&
                profile.Id != "incognito" && !profile.Id.Any(char.IsControl) && !_browserProfiles.Any(existing => existing.Id == profile.Id) &&
                _browserProfiles.Count < BrowserSettingsStore.MaximumSavedProfiles)
                _browserProfiles.Add(profile);
        _defaultBrowserProfileId = _browserProfiles.Any(profile => profile.Id == snapshot.DefaultProfileId) ? snapshot.DefaultProfileId : "default";
        OnPropertyChanged(nameof(BrowserDefaults));
        OnPropertyChanged(nameof(BrowserDefaultViewportIndex));
        OnPropertyChanged(nameof(BrowserDefaultZoomPercent));
        OnPropertyChanged(nameof(BrowserDefaultAppearanceIndex));
        OnPropertyChanged(nameof(BrowserLinkTargetIndex));
        OnPropertyChanged(nameof(BrowserLinkTarget));
        OnPropertyChanged(nameof(BrowserProfiles));
        OnPropertyChanged(nameof(DefaultBrowserProfileId));
    }

    public BrowserDefaults BrowserDefaults => _browserDefaults;
    public BrowserLinkTarget BrowserLinkTarget => _browserLinkTarget;
    public int BrowserLinkTargetIndex
    {
        get => (int)_browserLinkTarget;
        set
        {
            var target = value == 1 ? BrowserLinkTarget.App : BrowserLinkTarget.System;
            if (SetProperty(ref _browserLinkTarget, target)) { OnPropertyChanged(nameof(BrowserLinkTarget)); SaveBrowserSettings(); Save(); }
        }
    }
    public int BrowserDefaultViewportIndex
    {
        get => (int)_browserDefaults.Viewport;
        set => SetBrowserDefaults(_browserDefaults with { Viewport = (PreviewViewportPreset)value });
    }
    public double BrowserDefaultZoomPercent
    {
        get => _browserDefaults.ZoomFactor * 100;
        set => SetBrowserDefaults(_browserDefaults with { ZoomFactor = value / 100 });
    }
    public int BrowserDefaultAppearanceIndex
    {
        get => (int)_browserDefaults.Appearance;
        set => SetBrowserDefaults(_browserDefaults with { Appearance = (PreviewColorScheme)value });
    }
    public void SetBrowserDefaults(BrowserDefaults value)
    {
        var normalized = value.Normalize();
        if (_browserDefaults == normalized) return;
        _browserDefaults = normalized;
        OnPropertyChanged(nameof(BrowserDefaults));
        OnPropertyChanged(nameof(BrowserDefaultViewportIndex));
        OnPropertyChanged(nameof(BrowserDefaultZoomPercent));
        OnPropertyChanged(nameof(BrowserDefaultAppearanceIndex));
        SaveBrowserSettings();
        Save();
    }

    public void RenameBrowserProfile(string profileId, string name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 48) throw new ArgumentException("Enter a name of 1–48 characters.", nameof(name));
        var index = _browserProfiles.FindIndex(profile => profile.Id == profileId && profile.Id != "default");
        if (index < 0) throw new InvalidOperationException("Only a custom browser profile can be renamed.");
        _browserProfiles[index] = _browserProfiles[index] with { Name = normalized };
        OnPropertyChanged(nameof(BrowserProfiles));
        SaveBrowserSettings();
        Save();
    }

    // The UI awaits successful browser data clearing before removing the record.
    public async Task RemoveBrowserProfileAsync(string profileId, Func<string, Task> clearData)
    {
        if (profileId == "default" || !_browserProfiles.Any(profile => profile.Id == profileId))
            throw new InvalidOperationException("Only a custom browser profile can be removed.");
        await clearData(profileId);
        _browserProfiles.RemoveAll(profile => profile.Id == profileId);
        if (_defaultBrowserProfileId == profileId) _defaultBrowserProfileId = "default";
        OnPropertyChanged(nameof(BrowserProfiles));
        OnPropertyChanged(nameof(DefaultBrowserProfileId));
        SaveBrowserSettings();
        Save();
    }
}
