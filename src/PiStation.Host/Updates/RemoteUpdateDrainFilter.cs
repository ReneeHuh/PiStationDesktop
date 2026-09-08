using Microsoft.AspNetCore.SignalR;
using PiStation.Host.Hubs;
using PiStation.Host.Security;
using PiStation.Protocol.Models;

namespace PiStation.Host.Updates;

public sealed class RemoteUpdateDrainFilter(EnvironmentService environment) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var context = invocationContext;
        var allowed = RemoteAuthorizationFilter.MethodAccess.TryGetValue(context.HubMethodName, out var access) && access == RemoteAccessLevel.ReadOnly ||
            context.HubMethodName is nameof(EnvironmentHub.CommitRemoteUpdate) or nameof(EnvironmentHub.CancelRemoteUpdate) or nameof(EnvironmentHub.ClosePreview);
        RemoteUpdateCoordinator.OperationLease? lease;
        try { lease = environment.Updates.EnterOperation(allowed); }
        catch (InvalidOperationException)
        {
            throw new HubException("The host is restarting for an update. New operations are paused.");
        }
        using (lease) return await next(context).ConfigureAwait(false);
    }
}
