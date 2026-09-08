using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PiStation.Host.Security;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Hosting;

internal static class RemotePairingEndpoints
{
    public static void Map(WebApplication app, EnvironmentService environment, RemoteAccessStore access)
    {
        app.MapPost("/remote/pair", async (HttpContext context) =>
        {
            try
            {
                LimitPairingBody(context);
                var request = await context.Request.ReadFromJsonAsync(ProtocolJsonContext.Default.PairingRequest, context.RequestAborted).ConfigureAwait(false)
                    ?? throw new ArgumentException("Invalid pairing request.");
                var pending = access.BeginPairing(request);
                var descriptor = environment.GetDescriptor();
                await context.Response.WriteAsJsonAsync(new PairingStatus(pending.RequestId, "pending", descriptor.EnvironmentId, descriptor.EnvironmentName),
                    ProtocolJsonContext.Default.PairingStatus, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; }
            catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException or BadHttpRequestException)
            { context.Response.StatusCode = StatusCodes.Status400BadRequest; }
            catch (InvalidOperationException) { context.Response.StatusCode = StatusCodes.Status409Conflict; }
        });
        app.MapPost("/remote/pair/status", async (HttpContext context) =>
        {
            try
            {
                LimitPairingBody(context);
                var request = await context.Request.ReadFromJsonAsync(ProtocolJsonContext.Default.PairingPollRequest, context.RequestAborted).ConfigureAwait(false)
                    ?? throw new ArgumentException("Invalid pairing request.");
                var state = access.GetPairingState(request);
                var descriptor = environment.GetDescriptor();
                await context.Response.WriteAsJsonAsync(new PairingStatus(request.RequestId, state, descriptor.EnvironmentId, descriptor.EnvironmentName),
                    ProtocolJsonContext.Default.PairingStatus, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; }
            catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException or BadHttpRequestException)
            { context.Response.StatusCode = StatusCodes.Status400BadRequest; }
        });
    }

    private static void LimitPairingBody(HttpContext context)
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = 8192;
        if (context.Request.ContentLength is > 8192) throw new ArgumentException("Pairing request is too large.");
    }
}
