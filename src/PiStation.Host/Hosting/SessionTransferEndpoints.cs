using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using PiStation.Host.Errors;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Hosting;

internal static class SessionTransferEndpoints
{
    private const string ImportRoute = "/session-transfers/import/{operationId}";

    internal static void Map(WebApplication app, EnvironmentService environment)
    {
        app.MapGet(ImportRoute, (HttpContext context) => HandleAsync(context, environment, async () =>
        {
            var result = await environment.GetSessionImportResultAsync(ParseImport(context.Request), context.RequestAborted).ConfigureAwait(false);
            if (result is null) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body, result, ProtocolJsonContext.Default.ThreadDescriptor, context.RequestAborted).ConfigureAwait(false);
        }));
        app.MapPut(ImportRoute, (HttpContext context) => HandleAsync(context, environment, async () =>
        {
            var request = ParseImport(context.Request);
            if (context.Request.ContentLength != request.ByteLength) throw new ArgumentException("The session upload must declare the expected content length.");
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = request.ByteLength;
            var result = await environment.ImportPiSessionFileAsync(request, context.Request.Body, context.RequestAborted).ConfigureAwait(false);
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body, result, ProtocolJsonContext.Default.ThreadDescriptor, context.RequestAborted).ConfigureAwait(false);
        }));
        app.MapGet("/session-transfers/export/{threadId}", (HttpContext context) => HandleAsync(context, environment, async () =>
        {
            var threadId = ThreadId.Parse((string)context.Request.RouteValues["threadId"]!);
            if (!Enum.TryParse<PiSessionExportFormat>(context.Request.Query["format"], out var format) || !Enum.IsDefined(format))
                throw new ArgumentException("Select JSONL, HTML, or a ZIP bundle.");
            await environment.DownloadPiSessionAsync(threadId, format, async (file, sha256) =>
            {
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength = file.Length;
                context.Response.Headers.ETag = $"\"{sha256}\"";
                context.Response.Headers.ContentDisposition = $"attachment; filename=session{PiSessionTransferDefaults.Extension(format)}";
                await file.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            }, context.RequestAborted).ConfigureAwait(false);
        }));
    }

    private static PiSessionImportRequest ParseImport(HttpRequest request)
    {
        if (!Guid.TryParseExact((string?)request.RouteValues["operationId"], "N", out var operationId) ||
            !long.TryParse(request.Query["length"], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            throw new ArgumentException("The session import metadata is invalid.");
        var title = request.Query["title"].ToString();
        var result = new PiSessionImportRequest(operationId, ProjectId.Parse(request.Query["projectId"].ToString()),
            request.Query["extension"].ToString(), length, request.Query["sha256"].ToString(), string.IsNullOrEmpty(title) ? null : title);
        result.Validate();
        return result;
    }

    private static async Task HandleAsync(HttpContext context, EnvironmentService environment, Func<Task> action)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (context.Request.Headers["X-PiStation-Environment-Id"] != environment.EnvironmentId.Value)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }
        try { await action().ConfigureAwait(false); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or IOException or JsonException or UnauthorizedAccessException or HostOperationException or InvalidOperationException)
        {
            if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested) { context.Abort(); return; }
            context.Response.ContentLength = null;
            context.Response.StatusCode = error switch
            {
                ArgumentException => StatusCodes.Status400BadRequest,
                FileNotFoundException or DirectoryNotFoundException => StatusCodes.Status404NotFound,
                UnauthorizedAccessException => StatusCodes.Status403Forbidden,
                HostOperationException { Code: ProtocolErrorCodes.ThreadNotFound or ProtocolErrorCodes.ProjectNotFound } => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status409Conflict,
            };
            var message = error is InvalidDataException or ArgumentException or HostOperationException
                ? error.Message : "The session transfer could not finish. Check the host connection and retry.";
            if (message.Length > 512) message = message[..512];
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body, new ProtocolError("session.transfer_failed", message),
                ProtocolJsonContext.Default.ProtocolError, context.RequestAborted).ConfigureAwait(false);
        }
    }
}
