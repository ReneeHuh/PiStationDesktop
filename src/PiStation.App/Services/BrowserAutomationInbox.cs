using System.Text.Json;
using PiStation.App.ViewModels;

namespace PiStation.App.Services;

internal sealed class BrowserAutomationInbox
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;

    public BrowserAutomationInbox(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public async Task SetPermissionAsync(
        string threadId,
        PreviewAutomationAccess permission,
        CancellationToken cancellationToken = default)
    {
        var directory = ThreadDirectory(threadId);
        var permissionPath = Path.Combine(directory, "permission.json");
        if (permission == PreviewAutomationAccess.Off)
        {
            if (File.Exists(permissionPath))
            {
                File.Delete(permissionPath);
            }

            return;
        }

        Directory.CreateDirectory(directory);
        var mode = permission == PreviewAutomationAccess.Interact ? "interact" : "inspect";
        var temporaryPath = permissionPath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(new { mode, grantedUtc = DateTimeOffset.UtcNow }, JsonOptions),
            cancellationToken);
        File.Move(temporaryPath, permissionPath, overwrite: true);
    }

    public async Task<BrowserAutomationRequest?> ReadNextAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        var requestDirectory = Path.Combine(ThreadDirectory(threadId), "requests");
        if (!Directory.Exists(requestDirectory))
        {
            return null;
        }

        var path = Directory.EnumerateFiles(requestDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(static candidate => candidate, StringComparer.Ordinal)
            .FirstOrDefault();
        if (path is null)
        {
            return null;
        }

        var info = new FileInfo(path);
        if (info.Length is <= 0 or > 64 * 1024)
        {
            File.Delete(path);
            return null;
        }

        try
        {
            var request = JsonSerializer.Deserialize<BrowserAutomationRequest>(
                await File.ReadAllTextAsync(path, cancellationToken),
                JsonOptions);
            var expectedId = Path.GetFileNameWithoutExtension(path);
            if (request is null || request.Id != expectedId ||
                request.Operation.Length is <= 0 or > 32)
            {
                File.Delete(path);
                return null;
            }

            return request with { RequestPath = path };
        }
        catch (JsonException)
        {
            File.Delete(path);
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task CompleteAsync(
        string threadId,
        BrowserAutomationRequest request,
        bool success,
        object? data = null,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        var responseDirectory = Path.Combine(ThreadDirectory(threadId), "responses");
        Directory.CreateDirectory(responseDirectory);
        var responsePath = Path.Combine(responseDirectory, request.Id + ".json");
        var temporaryPath = responsePath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(new { success, data, error }, JsonOptions),
            cancellationToken);
        File.Move(temporaryPath, responsePath, overwrite: true);
        if (!string.IsNullOrWhiteSpace(request.RequestPath) && File.Exists(request.RequestPath))
        {
            File.Delete(request.RequestPath);
        }
    }

    private string ThreadDirectory(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        if (threadId.Length > 160 || threadId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new ArgumentException("The browser automation thread id is invalid.", nameof(threadId));
        }

        return Path.Combine(_root, threadId);
    }
}

internal sealed record BrowserAutomationRequest(
    string Id,
    string Operation,
    JsonElement Input,
    DateTimeOffset CreatedUtc,
    string? RequestPath = null);
