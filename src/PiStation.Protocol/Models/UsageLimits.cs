namespace PiStation.Protocol.Models;

// Quota sources describe accounts; they never route model requests or start another agent runtime.
public sealed record UsageLimitWindow(string Id, string Kind, string Label, double UsedPercent,
    DateTimeOffset? ResetsAt = null, double? WindowDurationMins = null);
public sealed record UsageLimitAccount(string Id, string Provider, string Label, DateTimeOffset CheckedAt,
    IReadOnlyList<UsageLimitWindow> Windows, string? Plan = null, string? Unavailable = null);
public sealed record UsageLimitSource(string Id, string Kind, string Label, DateTimeOffset? CheckedAt,
    IReadOnlyList<UsageLimitAccount> Accounts, string? Error = null, bool Stale = false);
public sealed record UsageLimitSourceConfiguration(string Id, string Label, string BaseUrl, bool Enabled, bool HasKey);
public sealed record UsageLimitSettings(long Revision, IReadOnlyList<UsageLimitSourceConfiguration> Sources, string? Error = null);
public sealed record UsageLimitsDashboard(UsageLimitSettings Settings, IReadOnlyList<UsageLimitSource> Sources,
    DateTimeOffset CapturedAt, string BackgroundStatus);
// Null key preserves an existing key on the same origin. Keys are never returned in snapshots.
public sealed record SaveUsageLimitSourceRequest(long Revision, string? Id, string Label, string BaseUrl,
    bool Enabled, string? ManagementKey = null)
{
    public override string ToString() => "SaveUsageLimitSourceRequest { credentials redacted }";
}
public sealed record RemoveUsageLimitSourceRequest(long Revision, string Id);

public static class UsageLimitPacing
{
    public static double? ElapsedPercent(UsageLimitWindow window, DateTimeOffset now) =>
        window.ResetsAt is { } reset && window.WindowDurationMins is > 0 and <= 527040 &&
        double.IsFinite(window.WindowDurationMins.Value)
            ? Math.Clamp(100 * (1 - (reset - now).TotalMinutes / window.WindowDurationMins.Value), 0, 100) : null;

    public static string Pace(UsageLimitWindow window, DateTimeOffset now)
    {
        if (window.ResetsAt <= now) return "Awaiting reset update";
        if (ElapsedPercent(window, now) is not { } elapsed) return "Pace unknown";
        var gap = window.UsedPercent - elapsed;
        return gap > 5 ? "Ahead of pace" : gap < -5 ? "Under pace" : "On pace";
    }
    public static string Reset(UsageLimitWindow window, DateTimeOffset now)
    {
        if (window.ResetsAt is not { } reset) return "Reset time unknown";
        if (reset <= now) return "Reset due; awaiting fresh quota";
        var remaining = reset - now;
        return "Resets in " + (remaining.TotalDays >= 1 ? $"{(int)remaining.TotalDays}d {remaining.Hours}h" :
            remaining.TotalHours >= 1 ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m" : $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m");
    }
}
