using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime;

public sealed class PiSessionImportRecoveryStore(string root)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<PiSessionImportFile?> LoadAsync(EnvironmentId environmentId, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadAsync(FilePath(environmentId), cancellationToken).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    public async Task SaveAsync(EnvironmentId environmentId, PiSessionImportFile import, CancellationToken cancellationToken = default)
    {
        import.Request.Validate();
        var path = FilePath(environmentId);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var record = new PiSessionImportRecoveryEntry(import.FilePath,
                JsonSerializer.SerializeToElement(import.Request, ProtocolJsonContext.Default.PiSessionImportRequest));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, PiSessionImportJsonContext.Default.PiSessionImportRecoveryEntry);
            if (bytes.Length > 64 * 1024) throw new InvalidDataException("The import recovery record is too large.");
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            finally { Gate.Release(); }
        }
    }

    public async Task ClearAsync(EnvironmentId environmentId, Guid operationId, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = FilePath(environmentId);
            if ((await ReadAsync(path, cancellationToken).ConfigureAwait(false))?.Request.OperationId == operationId) File.Delete(path);
        }
        finally { Gate.Release(); }
    }

    private string FilePath(EnvironmentId environmentId) => Path.Combine(Path.GetFullPath(root),
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(environmentId.Value))) + ".json");

    private static async Task<PiSessionImportFile?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (file.Length > 64 * 1024) throw new InvalidDataException("The saved import recovery record is too large.");
        var record = await JsonSerializer.DeserializeAsync(file, PiSessionImportJsonContext.Default.PiSessionImportRecoveryEntry, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The saved import recovery record is empty.");
        var request = record.Request.Deserialize(ProtocolJsonContext.Default.PiSessionImportRequest)
            ?? throw new InvalidDataException("The saved import request is empty.");
        var import = new PiSessionImportFile(record.FilePath, request);
        import.Request.Validate();
        if (!Path.IsPathFullyQualified(import.FilePath)) throw new InvalidDataException("The saved import file path is invalid.");
        return import;
    }
}

internal sealed record PiSessionImportRecoveryEntry(string FilePath, JsonElement Request);

[JsonSerializable(typeof(PiSessionImportRecoveryEntry))]
internal sealed partial class PiSessionImportJsonContext : JsonSerializerContext;
