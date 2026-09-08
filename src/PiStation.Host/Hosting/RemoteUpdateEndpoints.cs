using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PiStation.Host.Preview;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Hosting;

internal static class RemoteUpdateEndpoints
{
    private const string Prefix = "/updates/v1";

    public static void Map(WebApplication app, EnvironmentService environment)
    {
        // All listeners authenticate /updates and require operate access before dispatch.
        // Descriptor discovery binds the identity; every subsequent request must repeat it.
        app.MapGet(Prefix + "/descriptor", (HttpContext context) => HandleAsync(context, environment, false, async () =>
            await JsonSerializer.SerializeAsync(context.Response.Body,
                new RemoteUpdateMaintenanceDescriptor(RemoteUpdateMaintenanceDescriptor.CurrentApiVersion, environment.EnvironmentId, environment.Updates.Descriptor),
                ProtocolJsonContext.Default.RemoteUpdateMaintenanceDescriptor, context.RequestAborted).ConfigureAwait(false)));
        app.MapGet(Prefix + "/history", (HttpContext context) => HandleAsync(context, environment, true, async () =>
            await JsonSerializer.SerializeAsync(context.Response.Body, environment.Updates.GetHistory(PreviewLeaseRegistry.Principal(context)),
                ProtocolJsonContext.Default.RemoteUpdateReceiptArray, context.RequestAborted).ConfigureAwait(false)));
        app.MapGet(Prefix + "/{request:guid}", (HttpContext context) => HandleAsync(context, environment, true, async () =>
        {
            var receipt = environment.Updates.GetReceipt(Id(context), PreviewLeaseRegistry.Principal(context));
            if (receipt is null) { context.Response.StatusCode = 404; return; }
            await WriteAsync(context, receipt).ConfigureAwait(false);
        }));
        app.MapPost(Prefix + "/prepare", (HttpContext context) => HandleAsync(context, environment, true, async () =>
        {
            var request = await ReadAsync(context, ProtocolJsonContext.Default.PrepareRemoteUpdateRequest).ConfigureAwait(false);
            using var admission = environment.Updates.EnterOperation(allowDuringDrain: false);
            await WriteAsync(context, environment.Updates.Prepare(request, PreviewLeaseRegistry.Principal(context))).ConfigureAwait(false);
        }));
        app.MapPost(Prefix + "/{request:guid}/commit", (HttpContext context) => HandleAsync(context, environment, true, async () =>
        {
            var request = await ReadAsync(context, ProtocolJsonContext.Default.CommitRemoteUpdateRequest).ConfigureAwait(false);
            if (request.RequestId != Id(context)) throw new ArgumentException("The update request identity does not match.");
            await WriteAsync(context, environment.Updates.Commit(request, PreviewLeaseRegistry.Principal(context))).ConfigureAwait(false);
        }));
        app.MapPost(Prefix + "/{request:guid}/cancel", (HttpContext context) => HandleAsync(context, environment, true, () =>
            WriteAsync(context, environment.Updates.Cancel(Id(context), PreviewLeaseRegistry.Principal(context)))));
        app.MapPost(Prefix + "/{request:guid}/package", (HttpContext context) => HandleAsync(context, environment, true, async () =>
        {
            using var admission = environment.Updates.EnterOperation(allowDuringDrain: false);
            await environment.Updates.UploadAsync(context).ConfigureAwait(false);
        }));
    }

    private static Guid Id(HttpContext context) => Guid.Parse((string)context.Request.RouteValues["request"]!);
    private static Task WriteAsync(HttpContext context, RemoteUpdateReceipt receipt) =>
        JsonSerializer.SerializeAsync(context.Response.Body, receipt, ProtocolJsonContext.Default.RemoteUpdateReceipt, context.RequestAborted);

    private static async Task<T> ReadAsync<T>(HttpContext context, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        if (context.Request.ContentLength is not (> 0 and <= 4096)) throw new ArgumentException("Update metadata must declare a size of at most 4 KiB.");
        var bytes = new byte[checked((int)context.Request.ContentLength.Value)];
        await context.Request.Body.ReadExactlyAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, type) ?? throw new ArgumentException("Update metadata is empty.");
    }

    private static async Task HandleAsync(HttpContext context, EnvironmentService environment, bool requireIdentity, Func<Task> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.ContentType = "application/json";
        if (requireIdentity && context.Request.Headers["X-PiStation-Environment-Id"] != environment.EnvironmentId.Value)
        { context.Response.StatusCode = 409; return; }
        try { await action().ConfigureAwait(false); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException or JsonException)
        {
            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.StatusCode = error is UnauthorizedAccessException ? 403 : error is ArgumentException or JsonException ? 400 : 409;
            var message = error is ArgumentException or InvalidOperationException ? error.Message : "The host update request could not complete. Check its status before retrying.";
            await JsonSerializer.SerializeAsync(context.Response.Body, new ProtocolError("update.request_failed", message.Length > 512 ? message[..512] : message),
                ProtocolJsonContext.Default.ProtocolError, context.RequestAborted).ConfigureAwait(false);
        }
    }
}
