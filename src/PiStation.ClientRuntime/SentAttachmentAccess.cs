using System.Security.Cryptography;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

public static class SentAttachmentAccess
{
    public static async Task VerifyAsync(DraftAttachment attachment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (!Path.IsPathFullyQualified(attachment.ServerPath)) throw new IOException("The saved attachment path is invalid.");
        await using var stream = new FileStream(attachment.ServerPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != attachment.ByteLength || !string.Equals(
                Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)),
                attachment.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The saved attachment has changed since it was sent. It was not opened or copied.");
    }
}
