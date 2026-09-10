using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

public sealed partial class EnvironmentClient
{
    public Task<UsageLimitsDashboard> GetUsageLimitsAsync(CancellationToken token = default) => InvokeAsync<UsageLimitsDashboard>("GetUsageLimits", token);
    public Task<UsageLimitsDashboard> RefreshUsageLimitsAsync(CancellationToken token = default) => InvokeAsync<UsageLimitsDashboard>("RefreshUsageLimits", token);
    public Task<UsageLimitsDashboard> SaveUsageLimitSourceAsync(SaveUsageLimitSourceRequest request, CancellationToken token = default) => InvokeAsync<UsageLimitsDashboard>("SaveUsageLimitSource", request, token);
    public Task<UsageLimitsDashboard> RemoveUsageLimitSourceAsync(RemoveUsageLimitSourceRequest request, CancellationToken token = default) => InvokeAsync<UsageLimitsDashboard>("RemoveUsageLimitSource", request, token);
    public Task<UsageDashboard> GetUsageDashboardAsync(UsageQuery query, CancellationToken token = default) => InvokeAsync<UsageDashboard>("GetUsageDashboard", query, token);
    public Task<UsageDashboard> RefreshUsageDashboardAsync(RefreshUsageRequest request, CancellationToken token = default) => InvokeAsync<UsageDashboard>("RefreshUsageDashboard", request, token);
}
