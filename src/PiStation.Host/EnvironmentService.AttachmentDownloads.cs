using PiStation.Host.Errors;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host;

public sealed partial class EnvironmentService
{
    internal async Task<(DraftAttachment Attachment, FileStream Content)> OpenAttachmentDownloadAsync(
        ThreadId threadId, AttachmentId attachmentId, CancellationToken cancellationToken)
    {
        var attachment = await _database.FindDownloadableAttachmentAsync(threadId, attachmentId, cancellationToken)
            .ConfigureAwait(false);
        if (attachment is null || attachment.EnvironmentId != EnvironmentId || attachment.ThreadId != threadId)
            throw new HostOperationException(ProtocolErrorCodes.AttachmentNotFound, "The attachment is no longer available.");
        var content = await _attachmentStorage.OpenVerifiedReadAsync(attachment, cancellationToken).ConfigureAwait(false);
        return (attachment, content);
    }
}
