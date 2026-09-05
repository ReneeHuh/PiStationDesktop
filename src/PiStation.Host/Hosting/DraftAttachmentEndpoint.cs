using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using PiStation.Host.Errors;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Hosting;

internal static class DraftAttachmentEndpoint
{
    public const string Route = "/threads/{threadId}/draft-attachments";

    public static async Task HandleAsync(HttpContext context, EnvironmentService environment)
    {
        try
        {
            var request = ParseRequest(context.Request);
            var result = await environment.UploadDraftAttachmentAsync(
                request,
                context.Request.Body,
                context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status200OK;
            await WriteJsonAsync(
                context.Response,
                result,
                ProtocolJsonContext.Default.DraftAttachmentUploadResult,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            context.Response.StatusCode = exception.Code switch
            {
                ProtocolErrorCodes.AttachmentTooLarge => StatusCodes.Status413PayloadTooLarge,
                ProtocolErrorCodes.DraftNotFound or
                ProtocolErrorCodes.ThreadNotFound or
                ProtocolErrorCodes.AttachmentNotFound => StatusCodes.Status404NotFound,
                ProtocolErrorCodes.CommandConflict or
                ProtocolErrorCodes.DraftConflict or
                ProtocolErrorCodes.AttachmentConflict or
                ProtocolErrorCodes.AttachmentLimitExceeded => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            };
            await WriteJsonAsync(
                context.Response,
                new ProtocolError(exception.Code, exception.Message),
                ProtocolJsonContext.Default.ProtocolError,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await WriteJsonAsync(
                context.Response,
                new ProtocolError("AttachmentUploadFailed", exception.Message, true),
                ProtocolJsonContext.Default.ProtocolError,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static UploadDraftAttachmentRequest ParseRequest(HttpRequest request)
    {
        var threadId = RequiredRoute(request, "threadId");
        var fileName = request.Query["fileName"].ToString();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw Invalid("The upload query must include a fileName.");
        }

        var contentLength = request.ContentLength ??
            throw Invalid("The upload must declare its content length.");
        return new UploadDraftAttachmentRequest(
            ParseInt(RequiredHeader(request, "X-PiStation-Protocol-Version"), "protocol version"),
            EnvironmentId.Parse(RequiredHeader(request, "X-PiStation-Environment-Id")),
            ClientId.Parse(RequiredHeader(request, "X-PiStation-Client-Id")),
            CommandId.Parse(RequiredHeader(request, "X-PiStation-Command-Id")),
            ThreadId.Parse(threadId),
            DraftId.Parse(RequiredHeader(request, "X-PiStation-Draft-Id")),
            AttachmentId.Parse(RequiredHeader(request, "X-PiStation-Attachment-Id")),
            ParseLong(RequiredHeader(request, "X-PiStation-Draft-Revision"), "draft revision"),
            fileName,
            request.ContentType,
            contentLength);
    }

    private static string RequiredRoute(HttpRequest request, string name) =>
        request.RouteValues.TryGetValue(name, out var value) && value is not null &&
        !string.IsNullOrWhiteSpace(value.ToString())
            ? value.ToString()!
            : throw Invalid($"The upload route is missing {name}.");

    private static string RequiredHeader(HttpRequest request, string name)
    {
        var value = request.Headers[name].ToString();
        return string.IsNullOrWhiteSpace(value)
            ? throw Invalid($"The upload is missing the {name} header.")
            : value;
    }

    private static int ParseInt(string value, string name) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw Invalid($"The upload {name} is invalid.");

    private static long ParseLong(string value, string name) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw Invalid($"The upload {name} is invalid.");

    private static HostOperationException Invalid(string message) =>
        new(ProtocolErrorCodes.AttachmentInvalid, message);

    private static Task WriteJsonAsync<T>(
        HttpResponse response,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(response.Body, value, typeInfo, cancellationToken);
    }
}
