using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime;

internal static class PiSessionFileTransfer
{
    internal static async Task<ThreadDescriptor> ImportAsync(HttpClient http, PiSessionImportFile import,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        import.Request.Validate();
        var uri = ImportUri(import.Request);
        using (var receipt = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            if (receipt.StatusCode != HttpStatusCode.NotFound)
                return await ReadImportResultAsync(receipt, cancellationToken).ConfigureAwait(false);
        }
        await using var file = new FileStream(import.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        if (file.Length != import.Request.ByteLength) throw new InvalidDataException("The local session file changed. Choose it again to start a new import.");
        using var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = new UploadContent(file, progress) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return await ReadImportResultAsync(response, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<PiSessionExportResult> DownloadAsync(HttpClient http, ThreadId threadId, string destinationPath,
        PiSessionExportFormat format, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(destination), PiSessionTransferDefaults.Extension(format), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The export filename must match the selected format.", nameof(destinationPath));
        using var response = await http.GetAsync($"session-transfers/export/{Uri.EscapeDataString(threadId.Value)}?format={format}",
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var length = response.Content.Headers.ContentLength;
        var etag = response.Headers.ETag;
        var sha256 = etag?.Tag.Trim('"');
        if (response.StatusCode != HttpStatusCode.OK || length is null or <= 0 or > PiSessionTransferDefaults.MaximumTransferBytes ||
            etag?.IsWeak != false || sha256 is not { Length: 64 } || !sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The host returned invalid session export metadata.");

        // Keep the user's previous file until the complete download has been verified.
        var temporary = destination + $".{Guid.NewGuid():N}.partial";
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
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
                    if (received > length) throw new InvalidDataException("The session export exceeded its recorded size.");
                    hash.AppendData(buffer.AsSpan(0, count));
                    await file.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    progress?.Report(received);
                }
                if (received != length || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The session download was incomplete or failed its integrity check. Retry the export.");
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            return new(destination, length.Value);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static string ImportUri(PiSessionImportRequest request) =>
        $"session-transfers/import/{request.OperationId:N}?projectId={Uri.EscapeDataString(request.ProjectId.Value)}" +
        $"&extension={Uri.EscapeDataString(request.Extension)}&length={request.ByteLength.ToString(CultureInfo.InvariantCulture)}" +
        $"&sha256={Uri.EscapeDataString(request.Sha256)}&title={Uri.EscapeDataString(request.Title ?? string.Empty)}";

    private static async Task<ThreadDescriptor> ReadImportResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(await ReadBoundedAsync(response.Content, 1024 * 1024, cancellationToken).ConfigureAwait(false),
            ProtocolJsonContext.Default.ThreadDescriptor) ?? throw new InvalidDataException("The host did not return the imported thread.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Remote access expired or was revoked. Reconnect or pair with the host again.",
            HttpStatusCode.Forbidden => "This connection cannot transfer sessions. Use a connection with permission to operate the host.",
            HttpStatusCode.NotFound => "The session is no longer available on this host.",
            _ => "The session transfer failed. Retry to check the import's saved result or download the export again.",
        };
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            try
            {
                var error = JsonSerializer.Deserialize(await ReadBoundedAsync(response.Content, 8192, cancellationToken).ConfigureAwait(false),
                    ProtocolJsonContext.Default.ProtocolError);
                if (error?.Message is { Length: > 0 and <= 512 } detail) message = detail;
            }
            catch (Exception error) when (error is JsonException or InvalidDataException) { }
        }
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) return result.ToArray();
            if (result.Length + count > maximumBytes) throw new InvalidDataException("The host returned an oversized session response.");
            result.Write(buffer, 0, count);
        }
    }

    private sealed class UploadContent(FileStream file, IProgress<long>? progress) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = file.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => CopyAsync(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => CopyAsync(stream, cancellationToken);
        private async Task CopyAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[81920];
            long sent = 0;
            while (true)
            {
                var count = await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                progress?.Report(sent += count);
            }
        }
    }
}
