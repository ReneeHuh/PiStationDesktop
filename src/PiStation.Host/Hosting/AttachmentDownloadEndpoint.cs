using Microsoft.AspNetCore.Http;
using PiStation.Host.Errors;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;

namespace PiStation.Host.Hosting;

internal static class AttachmentDownloadEndpoint
{
    public const string Route = "/attachments/{threadId}/{attachmentId}";

    public static async Task HandleAsync(HttpContext context, EnvironmentService environment)
    {
        // Authentication (including read-only grants and revocation cancellation) is
        // enforced by each listener before this endpoint. Never accept a filesystem path.
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (context.Request.Headers["X-PiStation-Environment-Id"] != environment.EnvironmentId.Value)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        try
        {
            var thread = ThreadId.Parse((string)context.Request.RouteValues["threadId"]!);
            var id = AttachmentId.Parse((string)context.Request.RouteValues["attachmentId"]!);
            var download = await environment.OpenAttachmentDownloadAsync(thread, id, context.RequestAborted).ConfigureAwait(false);
            await using var content = download.Content;
            var etag = $"\"{download.Attachment.Sha256.ToUpperInvariant()}\"";
            context.Response.Headers.ETag = etag;
            // Even a cache hit must still authorize access and validate the host file.
            if (context.Request.Headers.IfNoneMatch == etag)
            {
                context.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers.ContentDisposition = "attachment";
            context.Response.ContentLength = content.Length;
            await content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception error) when (error is HostOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.ContentLength = null;
            context.Response.StatusCode = error switch
            {
                FileNotFoundException or DirectoryNotFoundException => StatusCodes.Status404NotFound,
                HostOperationException { Code: ProtocolErrorCodes.AttachmentNotFound } => StatusCodes.Status404NotFound,
                ArgumentException => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status409Conflict,
            };
        }
    }
}
