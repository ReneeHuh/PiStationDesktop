using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using PiStation.Host.Errors;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Attachments;

public sealed record StoredDraftAttachment(DraftAttachment Attachment, bool CreatedFile);

public sealed class DraftAttachmentStorage
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".gif", ".jpeg", ".jpg", ".png", ".webp",
    };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly HostOptions _options;
    private readonly string _attachmentRootPrefix;

    public DraftAttachmentStorage(HostOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _attachmentRootPrefix = Path.GetFullPath(options.AttachmentRoot) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(options.AttachmentRoot);
        Directory.CreateDirectory(options.AttachmentStagingRoot);
    }

    public async Task<StoredDraftAttachment> StoreAsync(
        UploadDraftAttachmentRequest request,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);
        var fileName = ValidateFileName(request.FileName);
        var mediaType = NormalizeMediaType(request.MediaType);
        var maximumBytes = IsImage(fileName, mediaType)
            ? _options.MaximumImageAttachmentBytes
            : _options.MaximumFileAttachmentBytes;
        if (request.ByteLength < 0)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.AttachmentInvalid,
                "An attachment byte length cannot be negative.");
        }

        if (request.ByteLength > maximumBytes)
        {
            throw TooLarge(fileName, maximumBytes);
        }

        var stagingPath = Path.Combine(_options.AttachmentStagingRoot, $"{Guid.NewGuid():N}.upload");
        try
        {
            string sha256;
            long written = 0;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var destination = new FileStream(
                             stagingPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    written = checked(written + read);
                    if (written > maximumBytes)
                    {
                        throw TooLarge(fileName, maximumBytes);
                    }

                    if (written > request.ByteLength)
                    {
                        throw new HostOperationException(
                            ProtocolErrorCodes.AttachmentInvalid,
                            "The attachment body is larger than its declared byte length.");
                    }

                    hash.AppendData(buffer.AsSpan(0, read));
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                if (written != request.ByteLength)
                {
                    throw new HostOperationException(
                        ProtocolErrorCodes.AttachmentInvalid,
                        $"The attachment body contained {written} bytes; {request.ByteLength} were declared.");
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                sha256 = Convert.ToHexString(hash.GetHashAndReset());
            }

            var attachmentDirectory = Path.Combine(_options.AttachmentRoot, StableDirectoryName(request.AttachmentId));
            Directory.CreateDirectory(attachmentDirectory);
            var serverPath = Path.GetFullPath(Path.Combine(attachmentDirectory, fileName));
            EnsureOwnedPath(serverPath);
            var createdFile = false;
            try
            {
                File.Move(stagingPath, serverPath, overwrite: false);
                createdFile = true;
            }
            catch (IOException) when (File.Exists(serverPath))
            {
                string existingHash;
                await using (var existing = File.OpenRead(serverPath))
                {
                    existingHash = Convert.ToHexString(await SHA256.HashDataAsync(
                        existing,
                        cancellationToken).ConfigureAwait(false));
                }

                if (!string.Equals(existingHash, sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new HostOperationException(
                        ProtocolErrorCodes.AttachmentConflict,
                        "The attachment ID already identifies different file content.");
                }
            }

            return new StoredDraftAttachment(
                new DraftAttachment(
                    request.EnvironmentId,
                    request.ThreadId,
                    request.DraftId,
                    request.AttachmentId,
                    fileName,
                    mediaType,
                    written,
                    sha256,
                    serverPath,
                    DateTimeOffset.UtcNow),
                createdFile);
        }
        finally
        {
            TryDeleteFile(stagingPath);
        }
    }

    public void Delete(DraftAttachment attachment) => Delete(attachment.ServerPath);

    public async Task ValidateForPromptAsync(
        DraftAttachment attachment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var fullPath = Path.GetFullPath(attachment.ServerPath);
        EnsureOwnedPath(fullPath);
        if (!string.Equals(Path.GetFileName(fullPath), attachment.FileName, StringComparison.Ordinal) ||
            !File.Exists(fullPath))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.AttachmentIntegrityFailed,
                $"Attachment '{attachment.FileName}' is missing from host-owned storage.");
        }

        var file = new FileInfo(fullPath);
        if (file.Length != attachment.ByteLength)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.AttachmentIntegrityFailed,
                $"Attachment '{attachment.FileName}' no longer has its recorded byte length.");
        }

        string sha256;
        await using (var content = File.OpenRead(fullPath))
        {
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false));
        }

        if (!string.Equals(sha256, attachment.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.AttachmentIntegrityFailed,
                $"Attachment '{attachment.FileName}' failed its SHA-256 integrity check.");
        }
    }

    public void Delete(string serverPath)
    {
        var fullPath = Path.GetFullPath(serverPath);
        EnsureOwnedPath(fullPath);
        TryDeleteFile(fullPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            try
            {
                Directory.Delete(directory, recursive: false);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string ValidateFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.AttachmentInvalid,
                "The attachment file name is required.");
        }

        var fileName = value.Trim();
        if (fileName.Length > 255 ||
            !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
            fileName is "." or ".." ||
            fileName.EndsWith(' ') ||
            fileName.EndsWith('.') ||
            fileName.Any(character => char.IsControl(character) || Path.GetInvalidFileNameChars().Contains(character)) ||
            ReservedNames.Contains(Path.GetFileNameWithoutExtension(fileName)))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.AttachmentInvalid,
                "The attachment file name is not safe to store.");
        }

        return fileName;
    }

    private static string NormalizeMediaType(string? value)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? "application/octet-stream" : value.Trim();
        if (!MediaTypeHeaderValue.TryParse(candidate, out var parsed) ||
            string.IsNullOrWhiteSpace(parsed.MediaType) ||
            parsed.MediaType.Length > 127)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.AttachmentInvalid,
                "The attachment media type is invalid.");
        }

        return parsed.MediaType.ToLowerInvariant();
    }

    private static bool IsImage(string fileName, string mediaType) =>
        mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
        ImageExtensions.Contains(Path.GetExtension(fileName));

    private static string StableDirectoryName(AttachmentId attachmentId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(attachmentId.Value))).ToLowerInvariant();

    private void EnsureOwnedPath(string fullPath)
    {
        if (!fullPath.StartsWith(_attachmentRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.AttachmentInvalid,
                "The attachment path is outside host-owned storage.");
        }
    }

    private static HostOperationException TooLarge(string fileName, long maximumBytes) => new(
        ProtocolErrorCodes.AttachmentTooLarge,
        $"'{fileName}' exceeds the {maximumBytes} byte attachment limit.");

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
