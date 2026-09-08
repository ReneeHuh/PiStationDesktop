using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiStation.PiRpc.Sessions;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Sessions;

public static class PiSessionBundle
{
    private const long MaximumBundleBytes = 512L * 1024 * 1024;
    private const int MaximumManifestBytes = 32 * 1024 * 1024;

    public static async Task<long> ExportAsync(string destination, string sessionPath, PiSessionDocument document,
        IReadOnlyCollection<SentMessageContent> messages, CancellationToken cancellationToken = default)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                await WriteAsync(archive, "session.jsonl", await File.ReadAllBytesAsync(sessionPath, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                var exported = new List<SentMessageContent>();
                var copied = new HashSet<string>(StringComparer.Ordinal);
                var links = new StringBuilder("<h2>Attachments and citation comments</h2>");
                long totalBytes = new FileInfo(sessionPath).Length;
                foreach (var message in messages)
                {
                    var attachments = new List<DraftAttachment>();
                    foreach (var attachment in message.Attachments)
                    {
                        var name = EntryName(attachment);
                        if (copied.Add(name))
                        {
                            totalBytes += attachment.ByteLength;
                            if (totalBytes > MaximumBundleBytes || copied.Count > 4096) throw new InvalidDataException("Session bundle exceeds 512 MiB or 4,096 files.");
                            await using var source = new FileStream(attachment.ServerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                            if (source.Length != attachment.ByteLength || !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false)), attachment.Sha256, StringComparison.OrdinalIgnoreCase))
                                throw new IOException($"Attachment changed since it was sent: {attachment.FileName}");
                            source.Position = 0;
                            await using var target = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                        }
                        attachments.Add(attachment with { ServerPath = name });
                        links.Append("<p><a download href=\"").Append(WebUtility.HtmlEncode(name)).Append("\">")
                            .Append(WebUtility.HtmlEncode(attachment.FileName)).Append("</a></p>");
                    }
                    foreach (var citation in message.Citations)
                        links.Append("<details><summary>").Append(WebUtility.HtmlEncode(citation.Label)).Append("</summary><pre>")
                            .Append(WebUtility.HtmlEncode(citation.Text)).Append("</pre><p>").Append(WebUtility.HtmlEncode(citation.Comment)).Append("</p></details>");
                    exported.Add(message with { Attachments = attachments });
                }
                var manifest = JsonSerializer.SerializeToUtf8Bytes(exported.ToArray(), ProtocolJsonContext.Default.SentMessageContentArray);
                if (manifest.Length > MaximumManifestBytes) throw new InvalidDataException("Session bundle metadata exceeds 32 MiB.");
                await WriteAsync(archive, "sent-content.json", manifest, cancellationToken).ConfigureAwait(false);
                await WriteAsync(archive, "transcript.html", Encoding.UTF8.GetBytes(document.ToHtml(document.Title).Replace("</body>", links + "</body>", StringComparison.Ordinal)), cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, destination, overwrite: true);
            return new FileInfo(destination).Length;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<(PiSessionDocument Document, IReadOnlyList<SentMessageContent> Messages)> ImportAsync(
        string source, string attachmentDirectory, CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(source);
        if (archive.Entries.Count > 4100 || archive.Entries.Sum(entry => entry.Length) > MaximumBundleBytes + MaximumManifestBytes + PiSessionDocument.MaximumBytes ||
            archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() != archive.Entries.Count)
            throw new InvalidDataException("Invalid or oversized session bundle.");
        var sessionBytes = await ReadAsync(archive, "session.jsonl", PiSessionDocument.MaximumBytes, cancellationToken).ConfigureAwait(false);
        var document = PiSessionDocument.Parse(sessionBytes);
        var messages = JsonSerializer.Deserialize(await ReadAsync(archive, "sent-content.json", MaximumManifestBytes, cancellationToken).ConfigureAwait(false),
            ProtocolJsonContext.Default.SentMessageContentArray) ?? throw new InvalidDataException("Missing session bundle metadata.");
        var imported = new List<SentMessageContent>();
        var createdFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(attachmentDirectory);
        try
        {
        foreach (var message in messages)
        {
            if (message is null || !Guid.TryParseExact(message.Id, "N", out _) || message.Attachments is null || message.Citations is null)
                throw new InvalidDataException("Invalid sent-message metadata.");
            ComposerContextDefaults.Validate(message.Citations);
            var attachments = new List<DraftAttachment>();
            foreach (var attachment in message.Attachments)
            {
                var name = EntryName(attachment);
                if (attachment.ServerPath != name) throw new InvalidDataException("Bundle attachments must use their declared relative entry names.");
                var bytes = await ReadAsync(archive, name, AttachmentDefaults.MaximumFileBytes, cancellationToken).ConfigureAwait(false);
                if (bytes.LongLength != attachment.ByteLength || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), attachment.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A bundled attachment failed its content hash check.");
                // Only a generated content-hash filename is ever written; archive paths are not extracted.
                var target = Path.Combine(attachmentDirectory, Path.GetFileName(name));
                if (!File.Exists(target)) createdFiles.Add(target);
                await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
                attachments.Add(attachment with { ServerPath = target });
            }
            imported.Add(message with { Attachments = attachments });
        }
        return (document, imported);
        }
        catch
        {
            foreach (var path in createdFiles) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            throw;
        }
    }

    private static string EntryName(DraftAttachment attachment)
    {
        if (attachment is null || attachment.Sha256 is not { Length: 64 } || !attachment.Sha256.All(Uri.IsHexDigit) || attachment.FileName is null || attachment.ByteLength is < 0 or > AttachmentDefaults.MaximumFileBytes)
            throw new InvalidDataException("Invalid attachment hash or size.");
        var extension = Path.GetExtension(attachment.FileName);
        if (extension.Length > 16 || extension.Skip(1).Any(character => !char.IsAsciiLetterOrDigit(character))) extension = ".bin";
        return "attachments/" + attachment.Sha256.ToLowerInvariant() + extension.ToLowerInvariant();
    }

    private static async Task WriteAsync(ZipArchive archive, string name, byte[] bytes, CancellationToken token)
    {
        await using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadAsync(ZipArchive archive, string name, long limit, CancellationToken token)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException("Bundle entry missing: " + name);
        if (entry.Length > limit) throw new InvalidDataException("Bundle entry is too large: " + name);
        await using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var bytes = new byte[81920];
        while (await stream.ReadAsync(bytes, token).ConfigureAwait(false) is var count && count > 0)
        {
            if (buffer.Length + count > limit) throw new InvalidDataException("Bundle entry exceeded its declared limit.");
            buffer.Write(bytes, 0, count);
        }
        return buffer.ToArray();
    }
}
