using System.Net;
using PiStation.Protocol.Errors;

namespace PiStation.ClientRuntime;

public sealed class AttachmentUploadException(
    HttpStatusCode statusCode,
    ProtocolError error) : Exception(error.Message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public ProtocolError Error { get; } = error;
}
