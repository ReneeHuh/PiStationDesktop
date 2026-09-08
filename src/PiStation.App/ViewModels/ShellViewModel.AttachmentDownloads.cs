using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    internal Task<string> GetAttachmentFileAsync(DraftAttachment attachment, CancellationToken cancellationToken = default) =>
        RequireClient().GetAttachmentFileAsync(attachment, _attachmentCacheRoot, cancellationToken);
}
