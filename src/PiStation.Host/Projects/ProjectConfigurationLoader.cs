using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiStation.Protocol.Models;

namespace PiStation.Host.Projects;

internal sealed record ProjectConfiguration(
    ThreadWorkspaceMode DefaultWorkspaceMode,
    IReadOnlyList<ProjectScript> Scripts,
    string? Icon,
    PiModelSelection? DefaultModel,
    PiThinkingLevel? DefaultThinkingLevel,
    string? DefaultRuntimeModeId,
    bool AutoPullDefaultBranch);

internal static class ProjectConfigurationLoader
{
    private const int MaximumConfigurationBytes = 256 * 1024;
    private const int MaximumScripts = 50;

    public static async Task<ProjectConfiguration> LoadAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var fallback = new ProjectConfiguration(
            ThreadWorkspaceMode.Local, [], ResolveFallbackIcon(projectRoot), null, null, null, false);
        var path = Path.Combine(projectRoot, "t3.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumConfigurationBytes)
            {
                return fallback;
            }

            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            var mode = root.TryGetProperty("defaultThreadEnvMode", out var modeElement) &&
                       string.Equals(modeElement.GetString(), "worktree", StringComparison.OrdinalIgnoreCase)
                ? ThreadWorkspaceMode.Worktree
                : ThreadWorkspaceMode.Local;
            var icon = ResolveIcon(projectRoot, root.TryGetProperty("iconPath", out var iconElement)
                ? iconElement.GetString()
                : null) ?? fallback.Icon;
            var defaultModel = ReadModel(root);
            var thinking = root.TryGetProperty("defaultThinkingLevel", out var thinkingElement) &&
                           Enum.TryParse<PiThinkingLevel>(thinkingElement.GetString(), true, out var parsedThinking)
                ? parsedThinking
                : (PiThinkingLevel?)null;
            var runtimeMode = root.TryGetProperty("defaultRuntimeMode", out var runtimeElement)
                ? runtimeElement.GetString()?.Trim()
                : null;
            var autoPull = (root.TryGetProperty("autoPullDefaultBranch", out var autoPullElement) ||
                            root.TryGetProperty("autoPull", out autoPullElement)) &&
                           autoPullElement.ValueKind == JsonValueKind.True;

            var scripts = new List<ProjectScript>();
            if (root.TryGetProperty("scripts", out var scriptsElement) && scriptsElement.ValueKind == JsonValueKind.Array)
            {
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

                    var scriptIcon = ProjectScriptIcon.Play;
                    if (element.TryGetProperty("icon", out var scriptIconElement))
                    {
                        _ = Enum.TryParse(scriptIconElement.GetString(), ignoreCase: true, out scriptIcon);
                    }

                    var runOnCreate = element.TryGetProperty("runOnWorktreeCreate", out var runElement) &&
                                      runElement.ValueKind == JsonValueKind.True;
                    var idInput = Encoding.UTF8.GetBytes($"{index}\0{name}\0{command}");
                    var id = Convert.ToHexString(SHA256.HashData(idInput))[..16].ToLowerInvariant();
                    scripts.Add(new ProjectScript(id, name, command, scriptIcon, runOnCreate));
                    index++;
                }
            }

            return new ProjectConfiguration(mode, scripts, icon, defaultModel, thinking, runtimeMode, autoPull);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return fallback;
        }
    }

    private static PiModelSelection? ReadModel(JsonElement root)
    {
        if (!root.TryGetProperty("defaultModel", out var model) || model.ValueKind != JsonValueKind.Object ||
            !model.TryGetProperty("provider", out var providerElement) ||
            !model.TryGetProperty("model", out var modelElement))
        {
            return null;
        }

        var provider = providerElement.GetString()?.Trim();
        var modelId = modelElement.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(modelId)
            ? null
            : new PiModelSelection(provider, modelId);
    }

    private static string? ResolveIcon(string projectRoot, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        return candidate.StartsWith($"{root}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
               File.Exists(candidate)
            ? candidate
            : null;
    }

    private static string? ResolveFallbackIcon(string projectRoot)
    {
        foreach (var relative in new[] { "favicon.ico", "favicon.png", "icon.png", "logo.png" })
        {
            var path = Path.Combine(projectRoot, relative);
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        return null;
    }
}
