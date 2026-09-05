using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiStation.Protocol.Models;

namespace PiStation.Host.Projects;

internal static class ProjectConfigurationLoader
{
    private const int MaximumConfigurationBytes = 256 * 1024;
    private const int MaximumScripts = 50;

    public static async Task<(ThreadWorkspaceMode DefaultWorkspaceMode, IReadOnlyList<ProjectScript> Scripts)> LoadAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(projectRoot, "t3.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumConfigurationBytes)
            {
                return (ThreadWorkspaceMode.Local, []);
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            var mode = root.TryGetProperty("defaultThreadEnvMode", out var modeElement) &&
                       string.Equals(modeElement.GetString(), "worktree", StringComparison.OrdinalIgnoreCase)
                ? ThreadWorkspaceMode.Worktree
                : ThreadWorkspaceMode.Local;
            if (!root.TryGetProperty("scripts", out var scriptsElement) ||
                scriptsElement.ValueKind != JsonValueKind.Array)
            {
                return (mode, []);
            }

            var scripts = new List<ProjectScript>();
            var index = 0;
            foreach (var element in scriptsElement.EnumerateArray().Take(MaximumScripts))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (element.ValueKind != JsonValueKind.Object ||
                    !element.TryGetProperty("name", out var nameElement) ||
                    !element.TryGetProperty("command", out var commandElement))
                {
                    continue;
                }

                var name = nameElement.GetString()?.Trim();
                var command = commandElement.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(command) ||
                    name.Length > 200 || command.Length > 32 * 1024)
                {
                    continue;
                }

                var icon = ProjectScriptIcon.Play;
                if (element.TryGetProperty("icon", out var iconElement))
                {
                    _ = Enum.TryParse(iconElement.GetString(), ignoreCase: true, out icon);
                }

                var runOnCreate = element.TryGetProperty("runOnWorktreeCreate", out var runElement) &&
                                  runElement.ValueKind == JsonValueKind.True;
                var idInput = Encoding.UTF8.GetBytes($"{index}\0{name}\0{command}");
                var id = Convert.ToHexString(SHA256.HashData(idInput))[..16].ToLowerInvariant();
                scripts.Add(new ProjectScript(id, name, command, icon, runOnCreate));
                index++;
            }

            return (mode, scripts);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return (ThreadWorkspaceMode.Local, []);
        }
    }
}
