using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Serialization;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Terminals;

internal sealed class TerminalHistoryStore(string dataRoot)
{
    private readonly string _root = Path.Combine(dataRoot, "terminal-history");
    private string FilePath(TerminalSessionId id) => Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.Value))) + ".json");

    public async Task SaveAsync(TerminalSnapshotEnvelope snapshot, CancellationToken token = default)
    {
        Directory.CreateDirectory(_root);
        var target = FilePath(snapshot.TerminalSessionId);
        var temporary = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, ProtocolJsonContext.Default.TerminalSnapshotEnvelope, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public IEnumerable<string> Files() => Directory.Exists(_root) ? Directory.EnumerateFiles(_root, "*.json") : [];
    public async Task<TerminalSnapshotEnvelope?> ReadAsync(string file, CancellationToken token)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 32768, FileOptions.Asynchronous);
        if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("Saved terminal history exceeds its limit.");
        TerminalSnapshotEnvelope? snapshot;
        try { snapshot = await JsonSerializer.DeserializeAsync(stream, ProtocolJsonContext.Default.TerminalSnapshotEnvelope, token).ConfigureAwait(false); }
        catch (NullReferenceException error)
        {
            // The envelope constructor reads its descriptor to initialize the base
            // record; missing/null metadata in a damaged file cannot be constructed.
            throw new InvalidDataException("Saved terminal metadata is missing.", error);
        }
        if (snapshot?.Descriptor is null || string.IsNullOrWhiteSpace(snapshot.TerminalSessionId.Value) || snapshot.BufferedOutput is null ||
            !string.Equals(file, FilePath(snapshot.TerminalSessionId), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid saved terminal identity.");
        return snapshot;
    }
    public void Delete(TerminalSessionId id) => File.Delete(FilePath(id));
}
