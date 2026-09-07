using System.Text.Json;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Persistence;

public sealed partial class HostDatabase
{
    public async Task RetainSentMessageAsync(ThreadId threadId, DraftId draftId, long revision,
        SentMessageContent content, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // The reference and draft revision check are one atomic statement. A concurrent
        // draft clear must not produce metadata pointing at already-released files.
        command.CommandText = """
            INSERT INTO SentMessageContents (Id, ThreadId, ContentJson)
            SELECT $id, $thread, $json FROM ThreadDrafts
            WHERE ThreadId=$thread AND DraftId=$draft AND Revision=$revision;
            """;
        command.Parameters.AddWithValue("$id", content.Id);
        command.Parameters.AddWithValue("$thread", threadId.Value);
        command.Parameters.AddWithValue("$draft", draftId.Value);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(content, ProtocolJsonContext.Default.SentMessageContent));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The draft changed before its sent content could be retained. Reload it and retry.");
    }

    public async Task<IReadOnlyDictionary<string, SentMessageContent>> ListSentMessagesAsync(
        ThreadId threadId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ContentJson FROM SentMessageContents WHERE ThreadId=$thread;";
        command.Parameters.AddWithValue("$thread", threadId.Value);
        var result = new Dictionary<string, SentMessageContent>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var content = JsonSerializer.Deserialize(reader.GetString(0), ProtocolJsonContext.Default.SentMessageContent)!;
            result.Add(content.Id, content);
        }
        return result;
    }
}
