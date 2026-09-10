using CommunityToolkit.Mvvm.ComponentModel;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed class PiRuntimePreferencesViewModel : ObservableObject
{
    private int _cache, _telemetry, _offline, _version;
    public int CacheIndex { get => _cache; set => SetProperty(ref _cache, value); }
    public int TelemetryIndex { get => _telemetry; set => SetProperty(ref _telemetry, value); }
    public int OfflineIndex { get => _offline; set => SetProperty(ref _offline, value); }
    public int VersionIndex { get => _version; set => SetProperty(ref _version, value); }
    private static bool? Choice(int index) => index switch { 1 => true, 2 => false, _ => null };
    private static int Index(bool? value) => value switch { true => 1, false => 2, _ => 0 };
    public PiRuntimePreferences Create() => new(Choice(CacheIndex), Choice(TelemetryIndex), Choice(OfflineIndex), Choice(VersionIndex));
    public void Apply(PiRuntimePreferences? preferences)
    {
        preferences ??= new();
        CacheIndex = Index(preferences.LongCacheRetention); TelemetryIndex = Index(preferences.Telemetry);
        OfflineIndex = Index(preferences.Offline); VersionIndex = Index(preferences.SkipVersionCheck);
    }
}
