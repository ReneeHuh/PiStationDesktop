using PiStation.Protocol.Models;

namespace PiStation.Host.Files;

internal static class ArtifactFileService
{
    public static async Task<ReadArtifactFileResult> ReadAsync(string absolutePath, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathFullyQualified(absolutePath) ||
            absolutePath.StartsWith("\\\\", StringComparison.Ordinal) || absolutePath.Any(char.IsControl))
            throw new ArgumentException("An artifact must be an absolute local file path on the environment.");
        var path = Path.GetFullPath(absolutePath);
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
            throw new FileNotFoundException("This artifact is unavailable or was moved.", path);
        var mediaType = WorkspaceFileReadService.GetMediaType(path);
        var asset = mediaType.StartsWith("image/", StringComparison.Ordinal) || mediaType.StartsWith("audio/", StringComparison.Ordinal) ||
            mediaType.StartsWith("video/", StringComparison.Ordinal) || mediaType == "application/pdf";
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        var maximum = asset ? FileAssetDefaults.MaximumBytes : FileReadDefaults.MaximumBytes;
        if (asset && length > maximum) throw new IOException($"This artifact exceeds the {maximum / (1024 * 1024)} MB preview limit.");
        var content = new byte[(int)Math.Min(length, maximum)];
        await stream.ReadExactlyAsync(content, token).ConfigureAwait(false);
        var binary = !asset && content.AsSpan().Contains((byte)0);
        return new(path, binary ? [] : content, length, mediaType, length > maximum, binary);
    }
}
