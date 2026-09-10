using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed record UsageLimitWindowRow(string Label, double UsedPercent, string Detail, double ElapsedWidth, double MarkerOpacity);
public sealed record UsageLimitAccountRow(string Label, string Status, IReadOnlyList<UsageLimitWindowRow> Windows);
public sealed record UsageLimitSourceRow(string Label, string Status, IReadOnlyList<UsageLimitAccountRow> Accounts);

public sealed class UsageLimitsViewModel : ObservableObject
{
    private UsageLimitsDashboard? _snapshot;
    private string _status = "No quota sources loaded.", _actionStatus = "", _background = "", _label = "", _url = "", _keyHint = "Enter the hub management key.";
    private bool _busy, _enabled = true;
    private string? _editingId;
    private long _editorRevision;
    public ObservableCollection<UsageLimitSourceRow> Sources { get; } = [];
    public ObservableCollection<UsageLimitSourceConfiguration> Configurations { get; } = [];
    public string Status { get => _status; internal set => SetProperty(ref _status, value); }
    public string ActionStatus { get => _actionStatus; internal set => SetProperty(ref _actionStatus, value); }
    public string Background { get => _background; private set => SetProperty(ref _background, value); }
    public string Label { get => _label; set => SetProperty(ref _label, value); }
    public string BaseUrl { get => _url; set => SetProperty(ref _url, value); }
    public string KeyHint { get => _keyHint; private set => SetProperty(ref _keyHint, value); }
    // One-way reset signal. Typed credentials remain only in the PasswordBox.
    public string PasswordReset { get; } = "";
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public bool IsBusy { get => _busy; internal set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(IsIdle)); } }
    public bool IsIdle => !IsBusy;
    public bool CanRemove => _editingId is not null;
    public void Edit(UsageLimitSourceConfiguration? source)
    {
        _editingId = source?.Id; _editorRevision = _snapshot?.Settings.Revision ?? 0;
        Label = source?.Label ?? ""; BaseUrl = source?.BaseUrl ?? ""; Enabled = source?.Enabled ?? true;
        KeyHint = source?.HasKey == true ? "A key is stored on this host. Leave blank to keep it on the same address." : "Enter the hub management key.";
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(PasswordReset));
        ActionStatus = "";
    }
    public SaveUsageLimitSourceRequest SaveRequest(string key) => new(_editorRevision, _editingId, Label, BaseUrl.Trim(), Enabled,
        string.IsNullOrEmpty(key) ? null : key);
    public RemoveUsageLimitSourceRequest RemoveRequest() => new(_editorRevision, _editingId ?? throw new InvalidOperationException("Select a hub to remove."));
    public void Clear()
    {
        _snapshot = null; Sources.Clear(); Configurations.Clear(); Edit(null); Background = ""; Status = "No quota sources loaded.";
    }
    public void Apply(UsageLimitsDashboard snapshot, DateTimeOffset? now = null)
    {
        var first = _snapshot is null;
        if (first || _snapshot!.Settings.Revision != snapshot.Settings.Revision)
        {
            Configurations.Clear(); foreach (var source in snapshot.Settings.Sources) Configurations.Add(source);
        }
        _snapshot = snapshot;
        if (first) Edit(null);
        Background = snapshot.BackgroundStatus + ". Quota hubs refresh about once a minute while background activity is allowed.";
        Status = snapshot.Settings.Error ?? $"{snapshot.Sources.Count} quota sources · last read {snapshot.CapturedAt.ToLocalTime():g}";
        Tick(now ?? DateTimeOffset.UtcNow);
    }
    public void Tick(DateTimeOffset now)
    {
        if (_snapshot is null) return;
        var rows = new List<UsageLimitSourceRow>();
        foreach (var source in _snapshot.Sources)
        {
            var stale = source.Stale || source.CheckedAt is { } at && now - at > TimeSpan.FromMinutes(5);
            var status = source.Error ?? (source.Accounts.Count == 0 ? "No limits reported." : $"{source.Accounts.Count} accounts");
            if (stale) status = "Stale readings · " + status;
            if (source.CheckedAt is { } checkedAt) status += $" · checked {checkedAt.ToLocalTime():g}";
            rows.Add(new(source.Label, status, source.Accounts.Select(account =>
            {
                var accountStale = stale || now - account.CheckedAt > TimeSpan.FromMinutes(5) || account.Unavailable == "probeFailed";
                var notice = account.Unavailable switch { "unsupported" => "Subscription quotas unsupported for this account.",
                    "probeFailed" => "Quota probe failed; retained last successful readings.", _ => account.Windows.Count == 0 ? "No limits reported." : "" };
                if (accountStale) notice = notice.Length == 0 ? "Quota reading is stale." : "Stale · " + notice;
                return new UsageLimitAccountRow($"{account.Provider} · {account.Label}" + (account.Plan is { Length: > 0 } plan ? " · " + plan : ""), notice,
                    account.Windows.Select(window => new UsageLimitWindowRow(window.Label, window.UsedPercent,
                        $"{window.UsedPercent:0.#}% used · {UsageLimitPacing.Reset(window, now)} · " +
                        (accountStale ? "Pace unavailable while stale" : UsageLimitPacing.Pace(window, now)),
                        Math.Round((UsageLimitPacing.ElapsedPercent(window, now) ?? 0) * 2),
                        UsageLimitPacing.ElapsedPercent(window, now) is null ? 0 : 1)).ToArray());
            }).ToArray()));
        }
        // Preserve the native item tree and scroll anchor when polling does not change its content.
        if (Sources.Count == rows.Count && Sources.Zip(rows).All(pair => pair.First.Label == pair.Second.Label && pair.First.Status == pair.Second.Status &&
            pair.First.Accounts.Count == pair.Second.Accounts.Count && pair.First.Accounts.Zip(pair.Second.Accounts).All(accounts =>
                accounts.First.Label == accounts.Second.Label && accounts.First.Status == accounts.Second.Status && accounts.First.Windows.SequenceEqual(accounts.Second.Windows)))) return;
        Sources.Clear(); foreach (var row in rows) Sources.Add(row);
    }
}
