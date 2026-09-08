using Microsoft.AspNetCore.SignalR;
using PiStation.Protocol.Models;

namespace PiStation.Host.Security;

/// <summary>SSH grants operate access as the signed-in Windows user, but never desktop-only RPCs.</summary>
public sealed class SshAuthorizationFilter : IHubFilter
{
    private readonly RemoteAuthorizationFilter _remote = new();
    private static bool IsDevice(HubCallerContext context) =>
        context.GetHttpContext()?.Items[RemoteAuthorizationFilter.AuthorizationItem] is RemoteAuthorization;

    public Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next) =>
        IsDevice(context.Context) ? _remote.OnConnectedAsync(context, next) : next(context);

    public Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next) =>
        IsDevice(context.Context) ? _remote.OnDisconnectedAsync(context, exception, next) : next(context, exception);

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (IsDevice(invocationContext.Context)) return await _remote.InvokeMethodAsync(invocationContext, next).ConfigureAwait(false);
        var context = invocationContext;
        if (!RemoteAuthorizationFilter.MethodAccess.ContainsKey(context.HubMethodName))
            throw new HubException("This operation is not available over SSH.");
        var result = await next(context).ConfigureAwait(false);
        return result is EnvironmentDescriptor descriptor
            ? descriptor with { Capabilities = descriptor.Capabilities.Where(c => c != "editor.open").Append("remote.access").ToArray() }
            : result;
    }
}
