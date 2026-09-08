using System.Security.Cryptography;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

/// <summary>A local file and stable import identity. Keep this value to recover an interrupted import.</summary>
public sealed record PiSessionImportFile(string FilePath, PiSessionImportRequest Request)
{
    public static async Task<PiSessionImportFile> CreateAsync(ProjectId projectId, string filePath,
        string? title = null, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(filePath);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        var request = new PiSessionImportRequest(Guid.NewGuid(), projectId, Path.GetExtension(path).ToLowerInvariant(),
            file.Length, new string('0', 64), string.IsNullOrWhiteSpace(title) ? null : title);
        request.Validate();
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        return new(path, request with { Sha256 = hash });
    }
}
