using System.Security.Cryptography;
using System.Text.Json;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host;

public sealed partial class EnvironmentService
{
    // Limit staging disk usage and serialize retries of the same import identity.
    private readonly SemaphoreSlim _sessionTransferGate = new(1, 1);
    private bool _sessionTransferStorageInitialized;
    private string SessionTransferRoot => Path.Combine(_options.CanonicalDataRoot, "session-transfers");

    private CopyPiSessionRequest CreateUploadedSessionCopy(PiSessionImportRequest request)
    {
        request.Validate();
        // The content hash is part of the durable copy request, so an operation ID
        // cannot silently be reused with a different file. No client path is accepted.
        var path = Path.Combine(SessionTransferRoot, $"transfer-{request.OperationId:N}-{request.Sha256.ToUpperInvariant()}-{request.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture)}{request.Extension}");
        return new(request.OperationId, request.ProjectId, path, Title: request.Title);
    }

    internal async Task<ThreadDescriptor?> GetSessionImportResultAsync(PiSessionImportRequest request, CancellationToken cancellationToken)
    {
        var copy = CreateUploadedSessionCopy(request);
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(copy, ProtocolJsonContext.Default.CopyPiSessionRequest)));
        var record = await _database.FindSessionCopyAsync(request.OperationId, hash, cancellationToken).ConfigureAwait(false);
        return record is null ? null : await _database.EnrichThreadDescriptorAsync(record, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ThreadDescriptor> ImportPiSessionFileAsync(PiSessionImportRequest request, Stream source, CancellationToken cancellationToken)
    {
        var copy = CreateUploadedSessionCopy(request);
        using var operation = Updates.EnterOperation(allowDuringDrain: false);
        await _sessionTransferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = copy.SourcePath + $".{Guid.NewGuid():N}.partial";
        try
        {
            InitializeSessionTransferStorage();
            // Validate the complete body even on a concurrent replay. The receipt
            // endpoint lets ordinary retries recover without sending the file again.
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long received = 0;
                while (true)
                {
                    var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > request.ByteLength) throw new InvalidDataException("The session upload exceeds its declared length.");
                    hash.AppendData(buffer.AsSpan(0, count));
                    await file.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }
                if (received != request.ByteLength || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), request.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The session upload was incomplete or failed its integrity check. Retry the import.");
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (await GetSessionImportResultAsync(request, cancellationToken).ConfigureAwait(false) is { } previous) return previous;
            File.Move(temporary, copy.SourcePath!, overwrite: true);
            return await CopyPiSessionAsync(copy, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteSessionTransferFile(temporary);
            DeleteSessionTransferFile(copy.SourcePath!);
            _sessionTransferGate.Release();
        }
    }

    internal async Task DownloadPiSessionAsync(ThreadId threadId, PiSessionExportFormat format,
        Func<FileStream, string, Task> write, CancellationToken cancellationToken)
    {
        var extension = PiSessionTransferDefaults.Extension(format);
        using var operation = Updates.EnterOperation(allowDuringDrain: false);
        await _sessionTransferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var path = Path.Combine(SessionTransferRoot, $"transfer-{Guid.NewGuid():N}{extension}");
        try
        {
            InitializeSessionTransferStorage();
            await WritePiSessionExportAsync(threadId, path, format, cancellationToken).ConfigureAwait(false);
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            if (file.Length > PiSessionTransferDefaults.MaximumTransferBytes) throw new InvalidDataException("The session export exceeds the 1 GiB transfer limit.");
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
            file.Position = 0;
            await write(file, hash).ConfigureAwait(false);
        }
        finally
        {
            DeleteSessionTransferFile(path);
            _sessionTransferGate.Release();
        }
    }

    private void InitializeSessionTransferStorage()
    {
        if (_sessionTransferStorageInitialized) return;
        Directory.CreateDirectory(SessionTransferRoot);
        if ((File.GetAttributes(SessionTransferRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Session transfer storage cannot be a symbolic link.");
        // Only this service writes transfer-* files in this dedicated directory.
        // No transfer is active yet; clean files left by a previous host crash.
        foreach (var file in Directory.EnumerateFiles(SessionTransferRoot, "transfer-*", SearchOption.TopDirectoryOnly))
            DeleteSessionTransferFile(file);
        _sessionTransferStorageInitialized = true;
    }

    private static void DeleteSessionTransferFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
