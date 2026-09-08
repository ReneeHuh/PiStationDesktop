using System.Net;
using System.Security.Cryptography;
using System.Text;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

internal static class AttachmentFileCache
{
    private const long MaximumCacheBytes = 512L * 1024 * 1024;
    // Fixed stripes avoid unbounded per-file locks and coordinate windows sharing a cache.
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    internal static async Task<string> GetFileAsync(HttpClient http, DraftAttachment attachment, string cacheRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        if (attachment.ByteLength is < 0 or > AttachmentDefaults.MaximumFileBytes ||
            attachment.Sha256.Length != 64 || !attachment.Sha256.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(attachment.EnvironmentId.Value) || string.IsNullOrWhiteSpace(attachment.ThreadId.Value) ||
            string.IsNullOrWhiteSpace(attachment.AttachmentId.Value))
            throw new InvalidDataException("The host returned invalid attachment metadata.");

        var root = Path.GetFullPath(cacheRoot);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{attachment.EnvironmentId.Value.Length}:{attachment.EnvironmentId.Value}" +
            $"{attachment.ThreadId.Value.Length}:{attachment.ThreadId.Value}" +
            $"{attachment.AttachmentId.Value.Length}:{attachment.AttachmentId.Value}{attachment.Sha256.ToUpperInvariant()}")));
        var extension = Path.GetExtension(attachment.FileName);
        if (extension.Length is < 2 or > 17 || !extension[1..].All(char.IsAsciiLetterOrDigit)) extension = ".bin";
        var path = Path.Combine(root, key + extension.ToLowerInvariant());
        var gate = Gates[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(root) % Gates.Length];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(root);
            var cached = await IsValidAsync(path, attachment, cancellationToken).ConfigureAwait(false);
            var etag = $"\"{attachment.Sha256.ToUpperInvariant()}\"";
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"attachments/{Uri.EscapeDataString(attachment.ThreadId.Value)}/{Uri.EscapeDataString(attachment.AttachmentId.Value)}");
            request.Headers.Add("X-PiStation-Environment-Id", attachment.EnvironmentId.Value);
            if (cached) request.Headers.IfNoneMatch.Add(new(etag));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && cached && response.Headers.ETag?.Tag == etag)
            {
                Touch(path);
                Prune(root, path);
                return path;
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var message = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Attachment access was revoked or expired. Reconnect or pair with the host again.",
                    HttpStatusCode.NotFound => "This attachment is no longer available on the host.",
                    HttpStatusCode.Conflict => "The host attachment changed or is unavailable. Refresh the conversation and try again.",
                    _ => "The attachment download failed. Reconnect and try again.",
                };
                throw new HttpRequestException(message, null, response.StatusCode);
            }
            if (response.Headers.ETag?.Tag != etag ||
                response.Content.Headers.ContentLength is { } length && length != attachment.ByteLength)
                throw new InvalidDataException("The host attachment does not match its recorded metadata. Refresh the conversation and retry.");

            var temporary = Path.Combine(root, $"{key}.{Guid.NewGuid():N}.partial");
            try
            {
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[64 * 1024];
                    long written = 0;
                    while (true)
                    {
                        var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (count == 0) break;
                        written += count;
                        if (written > attachment.ByteLength) throw new InvalidDataException("The attachment download exceeded its recorded size.");
                        hash.AppendData(buffer.AsSpan(0, count));
                        await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    }
                    if (written != attachment.ByteLength ||
                        !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), attachment.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The attachment download was incomplete or failed its integrity check. Try again.");
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
                Prune(root, path);
                return path;
            }
            finally { TryDelete(temporary); }
        }
        finally { gate.Release(); }
    }

    private static async Task<bool> IsValidAsync(string path, DraftAttachment attachment, CancellationToken cancellationToken)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            return file.Length == attachment.ByteLength && string.Equals(
                Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false)),
                attachment.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
    }

    private static void Touch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Prune(string root, string current)
    {
        try
        {
            // Only generated cache files are eligible, never arbitrary files or subdirectories.
            var files = new DirectoryInfo(root).EnumerateFiles().Where(file => file.Name.Length > 65 &&
                file.Name[64] == '.' && file.Name[..64].All(Uri.IsHexDigit)).OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
            long retained = 0;
            var retainedCount = 0;
            foreach (var file in files)
            {
                var length = file.Length;
                retained += length;
                retainedCount++;
                if (file.FullName == current) continue;
                var expired = file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(file.Extension == ".partial" ? -1 : -7);
                if ((expired || retained > MaximumCacheBytes || retainedCount > 1024) && TryDelete(file.FullName))
                {
                    retained -= length;
                    retainedCount--;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
