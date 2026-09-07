using System.Text.Json;
using Microsoft.Data.Sqlite;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Persistence;

public sealed partial class HostDatabase
{
    public async Task<BackgroundTaskResult?> GetBackgroundTaskAsync(SubmitBackgroundTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RequestJson,ResultJson FROM BackgroundTaskSubmissions WHERE DraftId=$draft AND DraftRevision=$revision;";
        command.Parameters.AddWithValue("$draft", request.DraftId.Value);
        command.Parameters.AddWithValue("$revision", request.DraftRevision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        if (reader.GetString(0) != JsonSerializer.Serialize(request, ProtocolJsonContext.Default.SubmitBackgroundTaskRequest))
            throw new InvalidOperationException("This draft revision already has a background submission with different options. Inspect it before submitting another task.");
        return JsonSerializer.Deserialize(reader.GetString(1), ProtocolJsonContext.Default.BackgroundTaskResult);
    }

    public async Task SaveBackgroundTaskAsync(SubmitBackgroundTaskRequest request, BackgroundTaskResult result,
        bool create = false, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = create
            ? "INSERT INTO BackgroundTaskSubmissions(SourceThreadId,DraftId,DraftRevision,RequestJson,ResultJson) VALUES($source,$draft,$revision,$request,$result);"
            : "UPDATE BackgroundTaskSubmissions SET ResultJson=$result WHERE DraftId=$draft AND DraftRevision=$revision AND RequestJson=$request;";
        command.Parameters.AddWithValue("$source", request.SourceThreadId.Value);
        command.Parameters.AddWithValue("$draft", request.DraftId.Value);
        command.Parameters.AddWithValue("$revision", request.DraftRevision);
        command.Parameters.AddWithValue("$request", JsonSerializer.Serialize(request, ProtocolJsonContext.Default.SubmitBackgroundTaskRequest));
        command.Parameters.AddWithValue("$result", JsonSerializer.Serialize(result, ProtocolJsonContext.Default.BackgroundTaskResult));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The background submission could not be recorded.");
    }

    public async Task<ThreadDraft> CopyBackgroundDraftAsync(SubmitBackgroundTaskRequest request, ThreadId targetThreadId,
        CancellationToken cancellationToken = default)
    {
        var target = await GetOrCreateThreadDraftAsync(targetThreadId, cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var source = await ReadDraftHeaderAsync(connection, transaction, request.SourceThreadId, cancellationToken).ConfigureAwait(false);
        if (source is null || source.Value.DraftId != request.DraftId || source.Value.Revision != request.DraftRevision)
            throw new InvalidOperationException("The source draft changed before submission. Its newer content was preserved.");
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE ThreadDrafts SET DraftText=$text,ContextJson=$context,Revision=Revision+1,UpdatedUtc=$now WHERE DraftId=$target AND Revision=0 AND DraftText='' AND ContextJson='[]';";
        update.Parameters.AddWithValue("$now", FormatDate(DateTimeOffset.UtcNow));
        update.Parameters.AddWithValue("$text", source.Value.Text);
        update.Parameters.AddWithValue("$context", source.Value.ContextJson);
        update.Parameters.AddWithValue("$target", target.DraftId.Value);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The new task already has a draft; it was not overwritten.");
        var attachments = await ReadDraftAttachmentsAsync(connection, transaction, request.SourceThreadId, request.DraftId, cancellationToken).ConfigureAwait(false);
        var ordinal = 0;
        foreach (var attachment in attachments)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO DraftAttachments(AttachmentId,DraftId,ThreadId,Ordinal,FileName,MediaType,ByteLength,Sha256,ServerPath,CreatedUtc) VALUES($id,$draft,$thread,$ordinal,$name,$media,$bytes,$hash,$path,$now);";
            insert.Parameters.AddWithValue("$id", AttachmentId.New().Value);
            insert.Parameters.AddWithValue("$draft", target.DraftId.Value);
            insert.Parameters.AddWithValue("$thread", targetThreadId.Value);
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
        return await GetOrCreateThreadDraftAsync(targetThreadId, cancellationToken).ConfigureAwait(false);
    }
}
