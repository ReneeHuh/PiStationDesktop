using PiStation.ClientRuntime;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    public UsageLimitsViewModel Limits { get; } = new();
    private EnvironmentId? _limitsEnvironment;
    private bool _limitsVisible;
    internal void SetLimitsVisible(bool visible)
    {
        _limitsVisible = visible;
        if (visible) _ = RefreshLimitsAsync(refresh: CanOperate);
    }
    internal async Task RefreshLimitsAsync(bool refresh = false)
    {
        if (!IsConnected || _client is not { } client || Limits.IsBusy || refresh && !CanOperate) return;
        var environment = client.Descriptor?.EnvironmentId;
        if (_limitsEnvironment != environment) { Limits.Clear(); _limitsEnvironment = environment; }
        Limits.IsBusy = true;
        if (refresh) Limits.Status = "Refreshing quotas…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var snapshot = refresh ? await client.RefreshUsageLimitsAsync(timeout.Token) : await client.GetUsageLimitsAsync(timeout.Token);
            if (IsCurrentLimitsClient(client, environment)) Limits.Apply(snapshot);
        }
        catch (Exception error) { if (IsCurrentLimitsClient(client, environment)) Limits.Status = "Quota refresh failed: " + error.Message; }
        finally { Limits.IsBusy = false; }
    }
    internal async Task SaveLimitSourceAsync(string key, bool remove = false)
    {
        if (!CanOperate || _client is not { } client || Limits.IsBusy) return;
        var environment = client.Descriptor?.EnvironmentId;
        if (_limitsEnvironment != environment) { await RefreshLimitsAsync(); Limits.ActionStatus = "Environment changed. Review the hub settings before saving."; return; }
        Limits.IsBusy = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var snapshot = remove ? await client.RemoveUsageLimitSourceAsync(Limits.RemoveRequest(), timeout.Token) :
                await client.SaveUsageLimitSourceAsync(Limits.SaveRequest(key), timeout.Token);
            if (IsCurrentLimitsClient(client, environment)) { Limits.Apply(snapshot); Limits.Edit(null); Limits.ActionStatus = remove ? "Quota hub removed from this host." : "Quota hub saved securely on this host. Refresh to check its accounts."; }
        }
        catch (Exception error) { if (IsCurrentLimitsClient(client, environment)) Limits.ActionStatus = error.Message; }
        finally { Limits.IsBusy = false; }
    }
    private bool IsCurrentLimitsClient(IEnvironmentClient client, EnvironmentId? environment) => ReferenceEquals(_client, client) && client.Descriptor?.EnvironmentId == environment;
}
