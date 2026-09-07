using Microsoft.Data.Sqlite;
using System.Globalization;
using PiStation.Protocol.Identifiers;

namespace PiStation.Host.Persistence;

public sealed partial class HostDatabase
{
    public async Task<long> GetCompletionSequenceAsync(ThreadId threadId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CompletionSequence FROM ThreadInboxMetadata WHERE ThreadId=$id;";
        command.Parameters.AddWithValue("$id", threadId.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public Task<long> RecordCompletionAsync(ThreadId threadId, CancellationToken cancellationToken = default) =>
        ChangeReadStateAsync(threadId, null, false, cancellationToken);

    public Task<long> SetReadStateAsync(ThreadId threadId, long observedCompletionSequence, bool isUnread,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(observedCompletionSequence);
        return ChangeReadStateAsync(threadId, observedCompletionSequence, isUnread, cancellationToken);
    }

    private async Task<long> ChangeReadStateAsync(ThreadId threadId, long? observed, bool isUnread, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = observed is null
            ? "UPDATE ThreadInboxMetadata SET CompletionSequence=CompletionSequence+1,SettlementActivityUtc=$now WHERE ThreadId=$id;"
            : isUnread
                ? "UPDATE ThreadInboxMetadata SET ReadCompletionSequence=MIN(ReadCompletionSequence,$observed-1) WHERE ThreadId=$id AND $observed>0 AND $observed<=CompletionSequence AND ReadCompletionSequence>=$observed;"
                : "UPDATE ThreadInboxMetadata SET ReadCompletionSequence=$observed WHERE ThreadId=$id AND $observed<=CompletionSequence AND ReadCompletionSequence<$observed;";
        update.Parameters.AddWithValue("$id", threadId.Value);
        update.Parameters.AddWithValue("$now", FormatDate(DateTimeOffset.UtcNow));
        if (observed is not null) update.Parameters.AddWithValue("$observed", observed.Value);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            // Advance metadata ordering without changing activity time or settlement.
            await using var revision = connection.CreateCommand();
            revision.Transaction = transaction;
            revision.CommandText = "UPDATE Threads SET Revision=Revision+1 WHERE ThreadId=$id;";
            revision.Parameters.AddWithValue("$id", threadId.Value);
            await revision.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT CompletionSequence FROM ThreadInboxMetadata WHERE ThreadId=$id;";
        read.Parameters.AddWithValue("$id", threadId.Value);
        var sequence = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The thread no longer exists.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(sequence, CultureInfo.InvariantCulture);
    }
}
