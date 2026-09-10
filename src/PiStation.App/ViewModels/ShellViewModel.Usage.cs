using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    public UsageDashboardViewModel Usage { get; } = new();
    private EnvironmentId? _usageEnvironment;
    public async Task RefreshUsageAsync(bool rescan = false, bool pricing = false)
    {
        if (!IsConnected || _client is not { } client || Usage.IsBusy || (rescan || pricing) && !CanOperate) return;
        var environment = client.Descriptor?.EnvironmentId;
        if (_usageEnvironment != environment) { Usage.Clear(); _usageEnvironment = environment; }
        Usage.IsBusy = true; Usage.Status = pricing ? "Refreshing public pricing and scanning history…" : "Scanning usage history…";
        try
        {
            var query = Usage.CreateQuery();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(125));
            var report = rescan || pricing ? await client.RefreshUsageDashboardAsync(new(query, rescan, pricing), timeout.Token) :
                await client.GetUsageDashboardAsync(query, timeout.Token);
            if (ReferenceEquals(_client, client) && client.Descriptor?.EnvironmentId == environment)
                Usage.Apply(report, client.Descriptor?.EnvironmentName ?? "Current environment");
        }
        catch (Exception error) { if (ReferenceEquals(_client, client) && client.Descriptor?.EnvironmentId == environment) Usage.Status = "Usage refresh failed: " + error.Message; }
        finally { Usage.IsBusy = false; }
    }
}
