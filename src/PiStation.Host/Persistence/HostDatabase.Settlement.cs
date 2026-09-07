using Microsoft.Data.Sqlite;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Persistence;

public sealed record SettlementActivity(DateTimeOffset? LastActivity, DateTimeOffset? LastUserActivity, bool Protected);

public sealed partial class HostDatabase
{
    private static async Task InitializeSettlementAsync(SqliteConnection connection, CancellationToken token)
    {
        await EnsureColumnAsync(connection, "ThreadInboxMetadata", "SettlementActivityUtc", "TEXT NULL", token);
        await EnsureColumnAsync(connection, "ThreadInboxMetadata", "SettlementUserUtc", "TEXT NULL", token);
        await EnsureColumnAsync(connection, "ThreadInboxMetadata", "SettlementProtected", "INTEGER NOT NULL DEFAULT 0", token);
        await EnsureColumnAsync(connection, "ThreadInboxMetadata", "SettlementPlanPending", "INTEGER NOT NULL DEFAULT 0", token);
        await EnsureColumnAsync(connection, "ThreadInboxMetadata", "SettlementPlanRevision", "INTEGER NOT NULL DEFAULT -1", token);
        await EnsureColumnAsync(connection, "ThreadInboxMetadata", "SettlementPlanSession", "TEXT NOT NULL DEFAULT ''", token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SettlementSettings (Id INTEGER PRIMARY KEY CHECK(Id=1), InactiveDays INTEGER NULL, OnMerge INTEGER NOT NULL, OnClose INTEGER NOT NULL);
            INSERT OR IGNORE INTO SettlementSettings VALUES(1,3,1,1);
            """;
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<SettlementSettings> GetSettlementSettingsAsync(CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT InactiveDays,OnMerge,OnClose FROM SettlementSettings WHERE Id=1;";
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        return new(reader.IsDBNull(0) ? null : reader.GetInt32(0), reader.GetBoolean(1), reader.GetBoolean(2));
    }

    public async Task SaveSettlementSettingsAsync(SettlementSettings settings, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.InactiveDays is < 1 or > 365) throw new ArgumentOutOfRangeException(nameof(settings), "Inactivity must be 1–365 days, or disabled.");
        await using var connection = await OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE SettlementSettings SET InactiveDays=$days,OnMerge=$merge,OnClose=$close WHERE Id=1;";
        command.Parameters.AddWithValue("$days", (object?)settings.InactiveDays ?? DBNull.Value);
        command.Parameters.AddWithValue("$merge", settings.OnMerge);
        command.Parameters.AddWithValue("$close", settings.OnClose);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<SettlementActivity> GetSettlementActivityAsync(ThreadId id, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SettlementActivityUtc,SettlementUserUtc,(SettlementProtected=1 OR SettlementPlanPending=1) FROM ThreadInboxMetadata WHERE ThreadId=$id;";
        command.Parameters.AddWithValue("$id", id.Value);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new KeyNotFoundException("Thread not found.");
        return new(reader.IsDBNull(0) ? null : DateTimeOffset.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture), reader.GetBoolean(2));
    }

    public async Task RecordSettlementActivityAsync(ThreadId id, bool userActivity, DateTimeOffset now, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ThreadInboxMetadata SET SettlementActivityUtc=$now,
                SettlementUserUtc=CASE WHEN $user=1 THEN $now ELSE SettlementUserUtc END,
                SettlementProtected=CASE WHEN $user=1 THEN 0 ELSE SettlementProtected END,
                IsSettled=CASE WHEN $user=1 THEN 0 ELSE IsSettled END
            WHERE ThreadId=$id;
            UPDATE Threads SET Revision=Revision+1 WHERE ThreadId=$id;
            """;
        command.Parameters.AddWithValue("$id", id.Value);
        command.Parameters.AddWithValue("$now", FormatDate(now));
        command.Parameters.AddWithValue("$user", userActivity);
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task<bool> TryAutoSettleAsync(ThreadId id, long expectedRevision, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Threads SET Revision=Revision+1,IsPinned=0
            WHERE ThreadId=$id AND Revision=$revision AND IsArchived=0
              AND EXISTS(SELECT 1 FROM ThreadInboxMetadata WHERE ThreadId=$id AND IsSettled=0 AND SettlementProtected=0 AND SettlementPlanPending=0)
              AND NOT EXISTS(SELECT 1 FROM CommandReceipts WHERE ThreadId=$id AND State IN ('Received','Dispatching','Accepted','DispatchUncertain'))
              AND NOT EXISTS(SELECT 1 FROM BackgroundTaskSubmissions WHERE
                (SourceThreadId=$id OR json_extract(ResultJson,'$.taskThreadId')=$id)
                AND json_extract(ResultJson,'$.state') IN (0,1,4));
            """;
        command.Parameters.AddWithValue("$id", id.Value);
        command.Parameters.AddWithValue("$revision", expectedRevision);
        if (await command.ExecuteNonQueryAsync(token) != 1) return false;
        command.CommandText = "UPDATE ThreadInboxMetadata SET IsSettled=1,PinnedOrder=NULL WHERE ThreadId=$id;";
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }

    public async Task RecordSettlementPlanAsync(ThreadId id, PiPlanState plan, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ThreadInboxMetadata SET SettlementPlanPending=$pending,SettlementPlanRevision=$revision,SettlementPlanSession=$session
            WHERE ThreadId=$id AND (SettlementPlanSession<>$session OR SettlementPlanRevision<$revision);
            """;
        command.Parameters.AddWithValue("$id", id.Value);
        command.Parameters.AddWithValue("$pending", plan.Mode is "ready" or "executing" or "paused");
        command.Parameters.AddWithValue("$revision", plan.Revision);
        command.Parameters.AddWithValue("$session", plan.SessionId);
        if (await command.ExecuteNonQueryAsync(token) > 0)
        {
            command.CommandText = "UPDATE Threads SET Revision=Revision+1 WHERE ThreadId=$id;";
            await command.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }
}
