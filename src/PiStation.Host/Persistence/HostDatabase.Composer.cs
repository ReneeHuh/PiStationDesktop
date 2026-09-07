using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Persistence;

public sealed partial class HostDatabase
{
    public async Task<IReadOnlyList<PromptStash>> ListPromptStashesAsync(ProjectId projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT StashId, ProjectId, ThreadId, Title, StashText, CreatedUtc, UpdatedUtc, ContextJson, AttachmentsJson FROM PromptStashes WHERE ProjectId = $id ORDER BY UpdatedUtc DESC, StashId LIMIT 100;";
        command.Parameters.AddWithValue("$id", projectId.Value);
        var result = new List<PromptStash>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(ReadStash(reader));
        return result;
    }

    public async Task<PromptStash?> GetPromptStashAsync(string stashId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadStashAsync(connection, null, stashId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PromptStash?> ReadStashAsync(SqliteConnection connection, SqliteTransaction? transaction, string stashId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT StashId, ProjectId, ThreadId, Title, StashText, CreatedUtc, UpdatedUtc, ContextJson, AttachmentsJson FROM PromptStashes WHERE StashId = $id;";
        command.Parameters.AddWithValue("$id", stashId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadStash(reader) : null;
    }

    private static PromptStash ReadStash(SqliteDataReader reader) => new(
        reader.GetString(0), ProjectId.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : ThreadId.Parse(reader.GetString(2)),
        reader.GetString(3), reader.GetString(4), ParseDate(reader.GetString(5)), ParseDate(reader.GetString(6)),
        JsonSerializer.Deserialize(reader.GetString(7), ProtocolJsonContext.Default.ComposerContextArray) ?? [],
        JsonSerializer.Deserialize(reader.GetString(8), ProtocolJsonContext.Default.DraftAttachmentArray) ?? []);

    public async Task<PromptStash> SavePromptStashAsync(SavePromptStashRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var stashId = string.IsNullOrWhiteSpace(request.StashId) ? Guid.NewGuid().ToString("N") : request.StashId;
        var existing = await ReadStashAsync(connection, transaction, stashId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.ProjectId != request.ProjectId || existing.ThreadId != request.ThreadId || existing.Text != request.Text)
                throw new InvalidOperationException("This stash identity already belongs to a different prompt.");
            return existing;
        }
        IReadOnlyList<ComposerContext> context = [];
        IReadOnlyList<DraftAttachment> attachments = [];
        var text = request.Text;
        if (request.DraftId is { } draftId && request.ThreadId is { } threadId)
        {
            var header = await ReadDraftHeaderAsync(connection, transaction, threadId, cancellationToken).ConfigureAwait(false);
            if (header is null || header.Value.DraftId != draftId || header.Value.Revision != request.ExpectedRevision)
                throw new InvalidOperationException("The draft changed before it could be stashed. Reload it and try again.");
            await using var owner = connection.CreateCommand();
            owner.Transaction = transaction;
            owner.CommandText = "SELECT ProjectId FROM Threads WHERE ThreadId = $id;";
            owner.Parameters.AddWithValue("$id", threadId.Value);
            if (!Equals(await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), request.ProjectId.Value))
                throw new InvalidOperationException("The draft belongs to another project.");
            text = header.Value.Text;
            context = JsonSerializer.Deserialize(header.Value.ContextJson, ProtocolJsonContext.Default.ComposerContextArray) ?? [];
            attachments = await ReadDraftAttachmentsAsync(connection, transaction, threadId, draftId, cancellationToken).ConfigureAwait(false);
        }
        var now = DateTimeOffset.UtcNow;
        var stash = new PromptStash(stashId, request.ProjectId, request.ThreadId,
            string.IsNullOrWhiteSpace(request.Title) ? CreatePromptStashTitle(text) : request.Title.Trim(), text, now, now, context, attachments);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO PromptStashes (StashId, ProjectId, ThreadId, Title, StashText, CreatedUtc, UpdatedUtc, ContextJson, AttachmentsJson) VALUES ($id,$project,$thread,$title,$text,$now,$now,$context,$attachments);";
        insert.Parameters.AddWithValue("$id", stash.StashId);
        insert.Parameters.AddWithValue("$project", stash.ProjectId.Value);
        insert.Parameters.AddWithValue("$thread", (object?)stash.ThreadId?.Value ?? DBNull.Value);
        insert.Parameters.AddWithValue("$title", stash.Title);
        insert.Parameters.AddWithValue("$text", stash.Text);
        insert.Parameters.AddWithValue("$now", FormatDate(now));
        insert.Parameters.AddWithValue("$context", JsonSerializer.Serialize(context.ToArray(), ProtocolJsonContext.Default.ComposerContextArray));
        insert.Parameters.AddWithValue("$attachments", JsonSerializer.Serialize(attachments.ToArray(), ProtocolJsonContext.Default.DraftAttachmentArray));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stash;
    }

    public async Task RestorePromptStashAsync(ThreadId threadId, DraftId draftId, long expectedRevision, string stashId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var stash = await ReadStashAsync(connection, transaction, stashId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The prompt stash is no longer available.");
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE ThreadDrafts SET DraftText=$text, ContextJson=$context, Revision=Revision+1, UpdatedUtc=$now
            WHERE ThreadId=$thread AND DraftId=$draft AND Revision=$revision AND length(trim(DraftText))=0
              AND ContextJson='[]' AND NOT EXISTS(SELECT 1 FROM DraftAttachments WHERE DraftId=$draft)
              AND EXISTS(SELECT 1 FROM Threads WHERE ThreadId=$thread AND ProjectId=$project);
            """;
        update.Parameters.AddWithValue("$text", stash.Text);
        update.Parameters.AddWithValue("$context", JsonSerializer.Serialize((stash.Context ?? []).ToArray(), ProtocolJsonContext.Default.ComposerContextArray));
        update.Parameters.AddWithValue("$now", FormatDate(DateTimeOffset.UtcNow));
        update.Parameters.AddWithValue("$thread", threadId.Value);
        update.Parameters.AddWithValue("$draft", draftId.Value);
        update.Parameters.AddWithValue("$revision", expectedRevision);
        update.Parameters.AddWithValue("$project", stash.ProjectId.Value);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Restore into an empty draft in the original project. Stash your current draft first.");
        var ordinal = 0;
        foreach (var attachment in stash.Attachments ?? [])
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO DraftAttachments (AttachmentId,DraftId,ThreadId,Ordinal,FileName,MediaType,ByteLength,Sha256,ServerPath,CreatedUtc) VALUES ($id,$draft,$thread,$ordinal,$name,$media,$bytes,$hash,$path,$now);";
            insert.Parameters.AddWithValue("$id", AttachmentId.New().Value);
            insert.Parameters.AddWithValue("$draft", draftId.Value);
            insert.Parameters.AddWithValue("$thread", threadId.Value);
            insert.Parameters.AddWithValue("$ordinal", ordinal++);
            insert.Parameters.AddWithValue("$name", attachment.FileName);
            insert.Parameters.AddWithValue("$media", attachment.MediaType);
            insert.Parameters.AddWithValue("$bytes", attachment.ByteLength);
            insert.Parameters.AddWithValue("$hash", attachment.Sha256);
            insert.Parameters.AddWithValue("$path", attachment.ServerPath);
            insert.Parameters.AddWithValue("$now", FormatDate(attachment.CreatedUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsAttachmentReferencedAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM DraftAttachments WHERE ServerPath=$path) OR EXISTS(SELECT 1 FROM PromptStashes, json_each(AttachmentsJson) WHERE json_extract(value,'$.serverPath')=$path) OR EXISTS(SELECT 1 FROM SentMessageContents, json_each(ContentJson, '$.attachments') WHERE json_extract(value,'$.serverPath')=$path);";
        command.Parameters.AddWithValue("$path", path);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }

    public async Task DeletePromptStashAsync(string stashId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PromptStashes WHERE StashId=$id;";
        command.Parameters.AddWithValue("$id", stashId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) throw new KeyNotFoundException("The stash no longer exists.");
    }
}
