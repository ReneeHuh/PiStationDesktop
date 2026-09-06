using System.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using PiStation.Host.Errors;
using PiStation.Host.Persistence;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Models;
using PiStation.Host.Workspaces;

namespace PiStation.Host.Files;

public sealed class WorkspaceFileReadService(
    HostDatabase database,
    ThreadWorkspaceResolver? workspaceResolver = null,
    WorkspaceOperationLocks? workspaceLocks = null)
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly HostDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly ThreadWorkspaceResolver _workspaceResolver = workspaceResolver ?? new ThreadWorkspaceResolver(database);
    private readonly WorkspaceOperationLocks _workspaceLocks = workspaceLocks ?? new WorkspaceOperationLocks();

    public async Task<ReadProjectFileResult> ReadAsync(
        ReadProjectFileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var workspace = await _workspaceResolver.ResolveAsync(
            request.ProjectId,
            request.ThreadId,
            cancellationToken).ConfigureAwait(false);
        var root = workspace.WorkspaceRoot;
        if (!Directory.Exists(root))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.FileReadUnavailable,
                "The project directory is not currently available.");
        }

        var normalizedRelativePath = request.RelativePath.Replace('\\', '/');
        var platformRelativePath = normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(root, platformRelativePath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw InvalidPath();
        }

        if (!IsContained(root, fullPath) || HasReparsePoint(root, platformRelativePath))
        {
            throw InvalidPath();
        }

        if (!File.Exists(fullPath))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.FileReadNotFound,
                $"Project file '{normalizedRelativePath}' was not found.");
        }

        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                useAsync: true);
            var byteLength = stream.Length;
            var bytesToRead = (int)Math.Min(byteLength, request.MaximumBytes);
            var buffer = new byte[bytesToRead];
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            var preview = buffer.AsSpan(0, offset);
            var isBinary = preview.Contains((byte)0);
            var content = isBinary ? string.Empty : Encoding.UTF8.GetString(preview);
            if (content.Length != 0 && content[0] == '\uFEFF')
            {
                content = content[1..];
            }

            stream.Position = 0;
            var revision = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false));
            return new ReadProjectFileResult(
                request.ProjectId,
                normalizedRelativePath,
                content,
                byteLength,
                byteLength > offset,
                isBinary,
                revision,
                GetMediaType(normalizedRelativePath));
        }
        catch (HostOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.FileReadUnavailable,
                $"Project file '{normalizedRelativePath}' could not be read.");
        }
    }

    public async Task<ReadProjectFileAssetResult> ReadAssetAsync(
        ReadProjectFileAssetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaximumBytes is < 1 or > FileAssetDefaults.MaximumBytes)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.FileReadInvalid,
                $"Workspace assets accept a byte limit between 1 and {FileAssetDefaults.MaximumBytes}.");
        }

        var (root, normalizedRelativePath, fullPath) = await ResolveExistingFileAsync(
            request.ProjectId,
            request.ThreadId,
            request.RelativePath,
            ProtocolErrorCodes.FileReadInvalid,
            cancellationToken).ConfigureAwait(false);
        _ = root;
        try
        {
            var info = new FileInfo(fullPath);
            if (info.Length > request.MaximumBytes)
            {
                throw new HostOperationException(
                    ProtocolErrorCodes.FileAssetTooLarge,
                    $"Workspace asset '{normalizedRelativePath}' is {info.Length:N0} bytes; the limit is {request.MaximumBytes:N0} bytes.");
            }

            var content = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            return new ReadProjectFileAssetResult(
                request.ProjectId,
                normalizedRelativePath,
                content,
                content.LongLength,
                GetMediaType(normalizedRelativePath),
                Convert.ToHexString(SHA256.HashData(content)));
        }
        catch (HostOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.FileReadUnavailable,
                $"Workspace asset '{normalizedRelativePath}' could not be read.");
        }
    }

    public async Task<SaveProjectFileResult> SaveAsync(
        SaveProjectFileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bytes = Encoding.UTF8.GetBytes(request.Content ?? string.Empty);
        if (string.IsNullOrWhiteSpace(request.ExpectedRevision) ||
            bytes.Length > FileReadDefaults.MaximumWriteBytes)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.FileWriteInvalid,
                $"Editable files require a revision and may contain at most {FileReadDefaults.MaximumWriteBytes:N0} UTF-8 bytes.");
        }

        var (root, normalizedRelativePath, fullPath) = await ResolveExistingFileAsync(
            request.ProjectId,
            request.ThreadId,
            request.RelativePath,
            ProtocolErrorCodes.FileWriteInvalid,
            cancellationToken).ConfigureAwait(false);
        await using var workspaceLock = await _workspaceLocks.AcquireAsync(root, cancellationToken)
            .ConfigureAwait(false);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(fullPath)!,
            $".{Path.GetFileName(fullPath)}.pistation-{RandomNumberGenerator.GetHexString(8)}.tmp");
        try
        {
            await using (var current = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                useAsync: true))
            {
                var currentRevision = Convert.ToHexString(
                    await SHA256.HashDataAsync(current, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(currentRevision, request.ExpectedRevision, StringComparison.OrdinalIgnoreCase))
                {
                    throw new HostOperationException(
                        ProtocolErrorCodes.FileWriteConflict,
                        $"Workspace file '{normalizedRelativePath}' changed after it was opened. Reload it before saving.");
                }
            }

            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, fullPath, overwrite: true);
            return new SaveProjectFileResult(
                request.ProjectId,
                normalizedRelativePath,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch (HostOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.FileWriteUnavailable,
                $"Workspace file '{normalizedRelativePath}' could not be saved.");
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public async Task<OpenProjectFileInEditorResult> OpenInEditorAsync(
        OpenProjectFileInEditorRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.LineNumber is < 1 || request.ColumnNumber is < 1)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.FileReadInvalid,
                "Editor line and column numbers must be positive.");
        }

        var (_, normalizedRelativePath, fullPath) = await ResolveExistingFileAsync(
            request.ProjectId,
            request.ThreadId,
            request.RelativePath,
            ProtocolErrorCodes.FileReadInvalid,
            cancellationToken).ConfigureAwait(false);
        foreach (var (command, name, supportsGoto) in EditorCandidates())
        {
            try
            {
                var start = new ProcessStartInfo(command)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                if (supportsGoto)
                {
                    start.ArgumentList.Add("--goto");
                    start.ArgumentList.Add(request.LineNumber is { } line
                        ? $"{fullPath}:{line}:{request.ColumnNumber ?? 1}"
                        : fullPath);
                }
                else
                {
                    start.ArgumentList.Add(fullPath);
                }

                if (Process.Start(start) is not null)
                {
                    return new OpenProjectFileInEditorResult(
                        request.ProjectId,
                        normalizedRelativePath,
                        fullPath,
                        name);
                }
            }
            catch (Exception exception) when (
                exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
        }

        throw new HostOperationException(
            ProtocolErrorCodes.EditorUnavailable,
            "No supported external editor could be launched. Install VS Code, Cursor, or Notepad.");
    }

    private async Task<(string Root, string RelativePath, string FullPath)> ResolveExistingFileAsync(
        PiStation.Protocol.Identifiers.ProjectId projectId,
        PiStation.Protocol.Identifiers.ThreadId? threadId,
        string relativePath,
        string invalidCode,
        CancellationToken cancellationToken)
    {
        ValidateRelativePath(relativePath, invalidCode);
        var workspace = await _workspaceResolver.ResolveAsync(projectId, threadId, cancellationToken)
            .ConfigureAwait(false);
        var root = workspace.WorkspaceRoot;
        if (!Directory.Exists(root))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.FileReadUnavailable,
                "The project directory is not currently available.");
        }

        var normalizedRelativePath = relativePath.Replace('\\', '/');
        var platformRelativePath = normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(root, platformRelativePath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw InvalidPath(invalidCode);
        }

        if (!IsContained(root, fullPath) || HasReparsePoint(root, platformRelativePath))
        {
            throw InvalidPath(invalidCode);
        }

        if (!File.Exists(fullPath))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.FileReadNotFound,
                $"Project file '{normalizedRelativePath}' was not found.");
        }

        return (root, normalizedRelativePath, fullPath);
    }

    private static IEnumerable<(string Command, string Name, bool SupportsGoto)> EditorCandidates()
    {
        yield return ("code", "Visual Studio Code", true);
        yield return ("cursor", "Cursor", true);
        yield return ("code-insiders", "Visual Studio Code Insiders", true);
        if (OperatingSystem.IsWindows())
        {
            yield return ("notepad.exe", "Notepad", false);
        }
    }

    private static string GetMediaType(string relativePath) => Path.GetExtension(relativePath).ToLowerInvariant() switch
    {
        ".avif" => "image/avif",
        ".gif" => "image/gif",
        ".ico" => "image/x-icon",
        ".jpeg" or ".jpg" => "image/jpeg",
        ".png" => "image/png",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".aac" => "audio/aac",
        ".flac" => "audio/flac",
        ".m4a" => "audio/mp4",
        ".mp3" => "audio/mpeg",
        ".ogg" or ".oga" => "audio/ogg",
        ".wav" => "audio/wav",
        ".wma" => "audio/x-ms-wma",
        ".m4v" => "video/x-m4v",
        ".mov" => "video/quicktime",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".wmv" => "video/x-ms-wmv",
        ".pdf" => "application/pdf",
        ".md" or ".markdown" or ".mdx" => "text/markdown",
        ".html" or ".htm" => "text/html",
        ".json" => "application/json",
        ".xml" => "application/xml",
        _ => "text/plain",
    };

    private static void ValidateRequest(ReadProjectFileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectId.Value) ||
            request.MaximumBytes is < 1 or > FileReadDefaults.MaximumBytes)
        {
            throw InvalidPath(ProtocolErrorCodes.FileReadInvalid);
        }

        ValidateRelativePath(request.RelativePath, ProtocolErrorCodes.FileReadInvalid);
    }

    private static void ValidateRelativePath(string relativePath, string invalidCode)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.Length > FileReadDefaults.MaximumRelativePathLength ||
            Path.IsPathRooted(relativePath) ||
            relativePath.StartsWith('/') ||
            relativePath.StartsWith('\\') ||
            relativePath.Contains(':') ||
            relativePath.Contains('\0'))
        {
            throw InvalidPath(invalidCode);
        }

        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
        {
            throw InvalidPath(invalidCode);
        }
    }

    private static bool IsContained(string root, string path) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    private static bool HasReparsePoint(string root, string relativePath)
    {
        var current = root;
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch (IOException)
            {
                return true;
            }
        }

        return false;
    }

    private static HostOperationException InvalidPath(string code = ProtocolErrorCodes.FileReadInvalid) => new(
        code,
        "File previews require a contained project-relative path and a valid byte limit.");
}
