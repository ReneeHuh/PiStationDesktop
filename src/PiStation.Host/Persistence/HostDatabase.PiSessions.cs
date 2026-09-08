using Microsoft.Data.Sqlite;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Persistence;

public sealed partial class HostDatabase
{
    public async Task<HostThreadRecord?> FindSessionCopyAsync(Guid operationId, string requestHash, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RequestHash, ThreadId FROM PiSessionCopies WHERE OperationId = $id";
        command.Parameters.AddWithValue("$id", operationId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        if (reader.GetString(0) != requestHash) throw new InvalidDataException("This session operation was already used for a different request.");
        return await GetThreadAsync(ThreadId.Parse(reader.GetString(1)), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The thread created by this operation was deleted. Start a new import or fork.");
    }

    public async Task<HostThreadRecord> CreateSessionCopyAsync(Guid operationId, string requestHash,
        ProjectId projectId, string title, string sessionPath, PiModelSelection? model, PiThinkingLevel? thinking,
        IReadOnlyList<SentMessageContent> messages,
        CancellationToken cancellationToken)
    {
        var id = operationId.ToString("N");
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Threads (ThreadId, ProjectId, PiSessionId, PiSessionFile, Title, Revision,
                IsArchived, IsPinned, CreatedUtc, UpdatedUtc, WorkspaceMode, BranchName, WorktreePath,
                WorkspaceGeneration, SetupScriptState, SetupScriptMessage)
            VALUES ($id, $project, $id, $path, $title, 0, 0, 0, $now, $now, 'Local', NULL, NULL, 0, 'None', NULL);
            INSERT INTO ThreadInboxMetadata (ThreadId, IsSettled, SnoozedUntilUtc, PinnedOrder, TitleKind, PullRequestJson)
            VALUES ($id, 0, NULL, NULL, 'Manual', NULL);
            INSERT INTO ThreadPiConfigurations (ThreadId, ModelProvider, ModelId, ThinkingLevel, RuntimeModeId, Revision, UpdatedUtc)
            VALUES ($id, $provider, $model, $thinking, NULL, 0, $now);
            INSERT INTO PiSessionCopies (OperationId, RequestHash, ThreadId) VALUES ($id, $hash, $id);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$project", projectId.Value);
        command.Parameters.AddWithValue("$path", sessionPath);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$now", FormatDate(now));
        command.Parameters.AddWithValue("$provider", (object?)model?.ProviderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)model?.ModelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$thinking", (object?)thinking?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", requestHash);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        foreach (var message in messages)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO SentMessageContents(Id,ThreadId,ContentJson) VALUES($id,$thread,$json)";
            insert.Parameters.AddWithValue("$id", message.Id);
            insert.Parameters.AddWithValue("$thread", id);
            insert.Parameters.AddWithValue("$json", System.Text.Json.JsonSerializer.Serialize(message, PiStation.Protocol.Serialization.ProtocolJsonContext.Default.SentMessageContent));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(ThreadId.Parse(id), projectId, id, sessionPath, title, 0, false, false, now, now);
    }
}
