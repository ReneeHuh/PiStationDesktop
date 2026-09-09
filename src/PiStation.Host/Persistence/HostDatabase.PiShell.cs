using System.Text.Json;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Persistence;

public sealed partial class HostDatabase
{
    public async Task<PiShellExecution?> GetPiShellAsync(ThreadId threadId, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ExecutionJson FROM PiShellExecutions WHERE ThreadId=$thread;";
        command.Parameters.AddWithValue("$thread", threadId.Value);
        var json = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
        return json is null ? null : JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.PiShellExecution);
    }

    public async Task SavePiShellAsync(ThreadId threadId, PiShellExecution execution, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PiShellExecutions(ThreadId,ExecutionJson) VALUES($thread,$json)
            ON CONFLICT(ThreadId) DO UPDATE SET ExecutionJson=excluded.ExecutionJson;
            """;
        command.Parameters.AddWithValue("$thread", threadId.Value);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(execution, ProtocolJsonContext.Default.PiShellExecution));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
