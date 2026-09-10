using Microsoft.AspNetCore.SignalR;

namespace PiStation.Host.Diagnostics;

internal sealed class OperationDiagnosticsFilter(RuntimeHealthService health) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        using var operation = health.Begin(context.HubMethodName);
        try { return await next(context).ConfigureAwait(false); }
        catch (OperationCanceledException) { operation.Outcome = "canceled"; throw; }
        catch { operation.Outcome = "error"; throw; }
    }
    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        health.Activity.Remove(context.Context.ConnectionId);
        await next(context, exception).ConfigureAwait(false);
    }
}
