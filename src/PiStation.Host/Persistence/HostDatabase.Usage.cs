using System.Globalization;
using System.Text.Json;
using PiStation.Host.Usage;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Persistence;

public sealed partial class HostDatabase
{
    internal async Task SaveUsageRecordAsync(ThreadId threadId, UsageRecord record, CancellationToken token)
    {
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO UsageRecords (RecordKey, ThreadId, RecordJson) VALUES ($key, $thread, $json) ON CONFLICT(RecordKey) DO UPDATE SET RecordJson = excluded.RecordJson;";
        command.Parameters.AddWithValue("$key", record.Key); command.Parameters.AddWithValue("$thread", threadId.Value);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(record, UsageJsonContext.Default.UsageRecord));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    internal async Task<(List<UsageRecord> Records, List<(string Thread, AgentActivityProjection Activity)> Children, int Suppressed)> ReadUsageRecordsAsync(
        IReadOnlySet<string> scannedSessionIds, IReadOnlySet<string> scannedPaths, CancellationToken token)
    {
        var records = new List<UsageRecord>();
        var canonicalThreads = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT ThreadId, RecordJson FROM UsageRecords;";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (JsonSerializer.Deserialize(reader.GetString(1), UsageJsonContext.Default.UsageRecord) is { } record)
                { records.Add(record); canonicalThreads.Add(reader.GetString(0)); }
            }
        }
        var suppressed = 0;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT u.UsageEventId, u.ThreadId, u.Provider, u.Model, u.InputTokens, u.OutputTokens, u.CacheTokens, u.TotalTokens, u.EstimatedCost, u.CostKnown, u.CreatedUtc, u.OriginKind, t.PiSessionId, t.PiSessionFile FROM UsageEvents u LEFT JOIN Threads t ON t.ThreadId=u.ThreadId WHERE u.OriginKind <> 'chat';";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (reader.GetString(11) == "legacy" && (canonicalThreads.Contains(reader.GetString(1)) || !reader.IsDBNull(12) && scannedSessionIds.Contains(reader.GetString(12)) ||
                    !reader.IsDBNull(13) && scannedPaths.Contains(reader.GetString(13))))
                { suppressed++; continue; }
                records.Add(new("legacy:" + reader.GetInt64(0), reader.IsDBNull(12) ? reader.GetString(1) : reader.GetString(12),
                    ParseDate(reader.GetString(10)), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5),
                    reader.GetInt64(6), 0, 0, reader.GetInt64(7), reader.GetInt64(9) == 0 ? null : decimal.Parse(reader.GetString(8), CultureInfo.InvariantCulture), Legacy: true));
            }
        }
        var children = new Dictionary<(string, string), AgentActivityProjection>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT ThreadId, EventJson FROM ThreadAgentEvents ORDER BY EventId;";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var activity = JsonSerializer.Deserialize(reader.GetString(1), ProtocolJsonContext.Default.AgentActivityChangedEvent)?.Activity;
                if (activity is { Kind: AgentActivityKind.Agent, Usage: not null }) children[(reader.GetString(0), activity.ActivityId)] = activity;
            }
        }
        return (records, children.Select(pair => (pair.Key.Item1, pair.Value)).ToList(), suppressed);
    }
}
