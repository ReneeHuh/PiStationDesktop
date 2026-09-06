using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace PiStation.FakePi;

internal sealed partial class FakePiServer
{
    private readonly bool _resourceEnabledAtStart;
    private readonly bool _projectTrustedAtStart;
    private string ManagementSettingsPath => Path.Combine(_arguments.SessionDirectory, "fake-management.json");
    private JsonObject ReadManagementSettings() => File.Exists(ManagementSettingsPath)
        ? JsonNode.Parse(File.ReadAllText(ManagementSettingsPath))!.AsObject() : new();

    private async Task HandleManagementAsync(string promptId, string prompt, CancellationToken cancellationToken)
    {
        var payload = prompt.Split(' ', 2)[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
        var request = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)))!;
        await _writer.WriteAsync(Response(promptId, "prompt"), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (_arguments.Scenario == "resource-management-crash") { _exitCode = 41; _stop.Cancel(); return; }
        var settings = ReadManagementSettings();
        var revision = (settings["revision"]?.GetValue<int>() ?? 0).ToString(CultureInfo.InvariantCulture);
        var action = request["action"]?.GetValue<string>();
        string? error = null;
        if (action is "toggle" or "saveModel" && request["revision"]?.GetValue<string>() != revision)
            error = "Pi settings changed. Refresh before saving again.";
        if (error is null && action != "inspect")
        {
            if (action == "toggle") settings["enabled"] = request["enabled"]!.DeepClone();
            if (action == "trust") settings["trusted"] = request["enabled"]!.DeepClone();
            if (action == "saveModel") settings["model"] = request["model"]!.DeepClone();
            settings["revision"] = int.Parse(revision, CultureInfo.InvariantCulture) + 1;
            await File.WriteAllTextAsync(ManagementSettingsPath, settings.ToJsonString(), cancellationToken).ConfigureAwait(false);
            revision = settings["revision"]!.ToString();
        }
        var providers = new JsonArray(new JsonObject { ["providerId"] = "fake", ["displayName"] = "Fake Provider",
            ["credentialConfigured"] = true, ["credentialSource"] = "runtime", ["modelCount"] = 2 });
        if (settings["model"] is { } model) providers.Add(new JsonObject
        {
            ["providerId"] = model["providerId"]!.DeepClone(), ["displayName"] = "Custom Provider",
            ["credentialConfigured"] = true, ["credentialSource"] = "models_json_key", ["modelCount"] = 1,
        });
        var result = error is not null ? new JsonObject { ["success"] = false, ["error"] = error } : new JsonObject
        {
            ["success"] = true,
            ["data"] = new JsonObject
            {
                ["agentDirectory"] = _arguments.SessionDirectory, ["projectDirectory"] = Environment.CurrentDirectory,
                ["projectTrusted"] = _projectTrustedAtStart, ["savedProjectTrust"] = settings["trusted"]?.GetValue<bool>(),
                ["modelsRevision"] = revision, ["message"] = action == "inspect" ? "Loaded state reflects this runtime; saved changes apply after restart." : "Configuration saved. Restart to apply.",
                ["resources"] = new JsonArray(new JsonObject
                {
                    ["id"] = "skills:managed-fixture", ["kind"] = "skills", ["name"] = "managed-fixture",
                    ["path"] = Path.Combine(_arguments.SessionDirectory, "skills", "managed-fixture", "SKILL.md"),
                    ["source"] = "local", ["scope"] = "user", ["enabled"] = settings["enabled"]?.GetValue<bool>() ?? true,
                    ["confirmedLoaded"] = _resourceEnabledAtStart, ["canToggle"] = true, ["revision"] = revision,
                }),
                ["providers"] = providers, ["diagnostics"] = new JsonArray(),
            },
        };
        await _writer.WriteAsync(new JsonObject
        {
            ["type"] = "extension_ui_request", ["id"] = Guid.NewGuid().ToString(), ["method"] = "setStatus",
            ["statusKey"] = "pistation-management:" + request["id"]!.GetValue<string>(), ["statusText"] = result.ToJsonString(),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
