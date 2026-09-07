using PiStation.Protocol.Models;

namespace PiStation.Host.Persistence;

public sealed partial class HostDatabase
{
    internal SemaphoreSlim PiAutomationGate { get; } = new(1, 1);

    public async Task<PiAutomationSettings> GetPiAutomationSettingsAsync(CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT AutoCompaction,AutoRetry,Revision FROM PiAutomationSettings WHERE Id=1;";
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        await reader.ReadAsync(token).ConfigureAwait(false);
        return new(reader.IsDBNull(0) ? null : reader.GetBoolean(0), reader.IsDBNull(1) ? null : reader.GetBoolean(1), reader.GetInt64(2));
    }

    public async Task<PiAutomationSettings> SavePiAutomationSettingsAsync(PiAutomationSettings settings, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(settings.Revision);
        await PiAutomationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE PiAutomationSettings SET AutoCompaction=$compaction,AutoRetry=$retry,Revision=Revision+1 WHERE Id=1 AND Revision=$revision;";
            command.Parameters.AddWithValue("$compaction", (object?)settings.AutoCompaction ?? DBNull.Value);
            command.Parameters.AddWithValue("$retry", (object?)settings.AutoRetry ?? DBNull.Value);
            command.Parameters.AddWithValue("$revision", settings.Revision);
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Pi automation settings changed. Reload before saving.");
            return settings with { Revision = settings.Revision + 1 };
        }
        finally { PiAutomationGate.Release(); }
    }
}
