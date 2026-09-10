namespace PiStation.Protocol.Models;

// Null preserves Pi's inherited behavior. Explicit false removes presence-based environment flags.
public sealed record PiRuntimePreferences(bool? LongCacheRetention = null, bool? Telemetry = null,
    bool? Offline = null, bool? SkipVersionCheck = null);

public static class PiRuntimePreferenceRules
{
    public static Dictionary<string, string?> Environment(PiLaunchConfiguration launch)
    {
        var result = new Dictionary<string, string?>(launch.EnvironmentVariables ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase);
        var preferences = launch.Preferences ?? new();
        void Apply(string name, bool? choice, string yes, string? no)
        {
            if (choice is null) return;
            if (result.ContainsKey(name)) throw new ArgumentException($"Remove {name} from the raw environment or choose Inherit in its dedicated preference.");
            result[name] = choice.Value ? yes : no;
        }
        Apply("PI_CACHE_RETENTION", preferences.LongCacheRetention, "long", null);
        Apply("PI_TELEMETRY", preferences.Telemetry, "1", "0");
        Apply("PI_OFFLINE", preferences.Offline, "1", null);
        Apply("PI_SKIP_VERSION_CHECK", preferences.SkipVersionCheck, "1", null);
        if (preferences.Offline.HasValue && (launch.Arguments ?? []).Any(value => value.Split('=')[0] == "--offline"))
            throw new ArgumentException("Remove --offline from raw arguments or choose Inherit for Offline.");
        if (preferences.Offline == true && preferences.SkipVersionCheck == false)
            throw new ArgumentException("Pi offline mode also skips version checks. Choose Inherit or Skip for version checks.");
        return result;
    }
}

public sealed record PiNativePreferences(string SavedTransport, string TransportRevision, string? ProjectTransport,
    string? StartupTransport, string? CacheRetention, string? Telemetry, bool Offline, bool SkipVersionCheck);
