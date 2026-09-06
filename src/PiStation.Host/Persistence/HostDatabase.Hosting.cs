using System.Text.Json;
using PiStation.Host.Errors;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Persistence;

public sealed partial class HostDatabase
{
    public async Task<(HostingOperation Operation, bool Created)> AcquireHostingOperationAsync(CommandId id, string action, string hash, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var operation = new HostingOperation(id, action, CommandReceiptState.Dispatching, null, now, now);
        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT OR IGNORE INTO HostingOperations (OperationId, RequestHash, OperationJson) VALUES ($id,$hash,$json);";
        insert.Parameters.AddWithValue("$id", id.Value);
        insert.Parameters.AddWithValue("$hash", hash);
        insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(operation, ProtocolJsonContext.Default.HostingOperation));
        var created = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT RequestHash, OperationJson FROM HostingOperations WHERE OperationId=$id;";
        read.Parameters.AddWithValue("$id", id.Value);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("The hosting operation receipt could not be read.");
        if (reader.GetString(0) != hash) throw new HostOperationException(ProtocolErrorCodes.CommandConflict, "This operation identity was already used for another request.");
        return (JsonSerializer.Deserialize(reader.GetString(1), ProtocolJsonContext.Default.HostingOperation)!, created);
    }

    public async Task CompleteHostingOperationAsync(HostingOperation operation, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE HostingOperations SET OperationJson=$json WHERE OperationId=$id;";
        command.Parameters.AddWithValue("$id", operation.OperationId.Value);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(operation, ProtocolJsonContext.Default.HostingOperation));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HostingOperation>> ListHostingOperationsAsync(CancellationToken cancellationToken = default)
        => await ReadHostingOperationsAsync(false, cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<HostingOperation>> ReadHostingOperationsAsync(bool recovering, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = recovering
            ? "SELECT OperationJson FROM HostingOperations WHERE json_extract(OperationJson, '$.state') = $state;"
            : "SELECT OperationJson FROM HostingOperations ORDER BY rowid DESC LIMIT 100;";
        if (recovering) command.Parameters.AddWithValue("$state", (int)CommandReceiptState.Dispatching);
        var result = new List<HostingOperation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(JsonSerializer.Deserialize(reader.GetString(0), ProtocolJsonContext.Default.HostingOperation)!);
        return result;
    }

    private async Task RecoverHostingOperationsAsync(CancellationToken cancellationToken)
    {
        foreach (var operation in await ReadHostingOperationsAsync(true, cancellationToken).ConfigureAwait(false))
        {
            if (operation.State != CommandReceiptState.Dispatching) continue;
            await CompleteHostingOperationAsync(operation with
            {
                State = CommandReceiptState.DispatchUncertain,
                Result = new SourceControlOperationResult(false, "The application stopped before this write was confirmed. Inspect the provider; this operation will not be resent.",
                    OperationId: operation.OperationId, State: CommandReceiptState.DispatchUncertain),
                UpdatedUtc = DateTimeOffset.UtcNow,
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
