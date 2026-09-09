using System.Security.Cryptography;
using PiStation.Protocol.Models;

namespace PiStation.Host.Projects;

internal static class ProjectIconStorage
{
    public static async Task<string> SaveAsync(string dataRoot, ProjectIconUpload upload, CancellationToken cancellationToken)
    {
        var extension = Validate(upload.FileName, upload.Content);
        var root = Path.Combine(dataRoot, "project-icons");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Convert.ToHexString(SHA256.HashData(upload.Content)) + extension);
        var temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, upload.Content, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }

    public static async Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        if (stream.Length is <= 0 or > ProjectIconLimits.MaximumBytes) throw new InvalidDataException("Choose an image no larger than 512 KiB.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        Validate(path, bytes);
        return bytes;
    }

    private static string Validate(string fileName, byte[] content)
    {
        if (content is null || content.Length is < 12 or > ProjectIconLimits.MaximumBytes)
            throw new ArgumentException("Choose a PNG, JPEG, GIF, ICO or WebP image no larger than 512 KiB.");
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var valid = extension switch
        {
            ".png" => content.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            ".jpg" or ".jpeg" => content[0] == 255 && content[1] == 216 && content[2] == 255,
            ".gif" => content.AsSpan(0, 6).SequenceEqual("GIF87a"u8) || content.AsSpan(0, 6).SequenceEqual("GIF89a"u8),
            ".ico" => content.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 0, 1, 0 }),
            ".webp" => content.AsSpan(0, 4).SequenceEqual("RIFF"u8) && content.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false,
        };
        if (!valid) throw new ArgumentException("The project icon must contain a supported image matching its file extension.");
        return extension;
    }
}
