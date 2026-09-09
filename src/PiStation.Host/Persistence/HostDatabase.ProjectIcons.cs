using Microsoft.Data.Sqlite;
using PiStation.Protocol.Identifiers;

namespace PiStation.Host.Persistence;

public sealed partial class HostDatabase
{
    private static async Task InitializeProjectIconsAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ProjectIconOverrides (
                ProjectId TEXT PRIMARY KEY REFERENCES Projects(ProjectId) ON DELETE CASCADE,
                Icon TEXT NULL);
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    public async Task<(bool IsSet, string? Icon)> GetProjectIconOverrideAsync(ProjectId id, CancellationToken token)
    {
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Icon FROM ProjectIconOverrides WHERE ProjectId = $id";
        command.Parameters.AddWithValue("$id", id.Value);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false)
            ? (true, reader.IsDBNull(0) ? null : reader.GetString(0)) : (false, null);
    }

    // The group update is one host transaction. Catalog triggers publish each changed
    // project; no checkout's scripts, trust, or model defaults are read-modify-written.
    public async Task SetProjectIconsAsync(IReadOnlyList<ProjectId> ids, string? icon, CancellationToken token)
    {
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        foreach (var id in ids)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ProjectIconOverrides(ProjectId, Icon) VALUES ($id, $icon)
                ON CONFLICT(ProjectId) DO UPDATE SET Icon = excluded.Icon;
                UPDATE Projects SET Icon = $icon WHERE ProjectId = $id;
                """;
            command.Parameters.AddWithValue("$id", id.Value);
            command.Parameters.AddWithValue("$icon", (object?)icon ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }
}
