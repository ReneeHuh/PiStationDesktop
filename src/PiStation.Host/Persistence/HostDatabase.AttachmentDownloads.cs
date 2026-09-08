using System.Text.Json;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Persistence;

public sealed partial class HostDatabase
{
    public async Task<DraftAttachment?> FindDownloadableAttachmentAsync(
        ThreadId threadId, AttachmentId attachmentId, CancellationToken cancellationToken = default)
    {
        var draft = await GetThreadDraftAsync(threadId, cancellationToken).ConfigureAwait(false);
        if (draft?.Attachments.FirstOrDefault(item => item.AttachmentId == attachmentId) is { } attachment)
            return attachment;

        // Sent attachments outlive their draft. Select only the requested attachment,
        // including references retained by copied and imported sessions.
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT attachment.value
            FROM SentMessageContents AS message, json_each(message.ContentJson, '$.attachments') AS attachment
            WHERE message.ThreadId = $thread AND json_extract(attachment.value, '$.attachmentId') = $attachment
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$thread", threadId.Value);
        command.Parameters.AddWithValue("$attachment", attachmentId.Value);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return json is null ? null : JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.DraftAttachment);
    }
}
