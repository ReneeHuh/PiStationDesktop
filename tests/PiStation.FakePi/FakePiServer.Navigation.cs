using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PiStation.FakePi;

internal sealed partial class FakePiServer
{
    private TaskCompletionSource? _fakeNavigationCancelled;
    private async Task HandleSessionNavigationAsync(string prompt, CancellationToken cancellationToken)
    {
        var encoded = prompt[(prompt.IndexOf(' ') + 1)..].Replace('-', '+').Replace('_', '/');
        var request = JsonNode.Parse(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')))!;
        JsonObject response;
        try
        {
            if (request["customInstructions"]?.ToString() == "test:fail") throw new InvalidDataException("Navigation hook failed.");
            if (request["customInstructions"]?.ToString() == "test:wait-for-cancel")
            {
                _fakeNavigationCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await _fakeNavigationCancelled.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                response = new() { ["success"] = true, ["data"] = new JsonObject { ["cancelled"] = true } };
            }
            else
            {
                var bytes = await File.ReadAllBytesAsync(_session.SessionFile, cancellationToken).ConfigureAwait(false);
                if (Convert.ToHexString(SHA256.HashData(bytes)) != request["expectedRevision"]?.ToString()) throw new InvalidDataException("The session changed.");
                var (entries, leafId) = await _session.ReadAsync(cancellationToken).ConfigureAwait(false);
                var target = entries.Single(entry => entry?["id"]?.ToString() == request["entryId"]?.ToString())!;
                var labeling = request["action"]?.ToString() == "label";
                if (labeling)
                {
                    var labelEntry = new JsonObject { ["type"] = "label", ["id"] = Guid.NewGuid().ToString("N"), ["parentId"] = leafId,
                        ["targetId"] = target["id"]!.DeepClone(), ["label"] = request["label"]?.DeepClone(), ["timestamp"] = DateTimeOffset.UtcNow.ToString("O") };
                    await File.AppendAllTextAsync(_session.SessionFile, labelEntry.ToJsonString() + "\n", cancellationToken).ConfigureAwait(false);
                    leafId = labelEntry["id"]!.ToString();
                }
                var user = target["type"]?.ToString() == "custom_message" || target["message"]?["role"]?.ToString() == "user";
                var content = target["message"]?["content"] ?? target["content"];
                var text = content is JsonArray parts ? string.Join('\n', parts.Where(part => part?["type"]?.ToString() == "text").Select(part => part!["text"]!.ToString())) : content?.ToString();
                var result = new JsonObject { ["cancelled"] = false, ["editorText"] = user ? text : null };
                var marker = new JsonObject
                {
                    ["type"] = "custom", ["customType"] = labeling ? "pistation.session-label" : "pistation.branch-navigation", ["id"] = Guid.NewGuid().ToString("N"),
                    ["parentId"] = labeling ? JsonValue.Create(leafId) : (user ? target["parentId"] : target["id"])?.DeepClone(), ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["data"] = new JsonObject { ["operationId"] = request["operationId"]!.DeepClone(),
                        ["requestHash"] = request["requestHash"]!.DeepClone(), ["result"] = result.DeepClone() },
                };
                await File.AppendAllTextAsync(_session.SessionFile, marker.ToJsonString() + "\n", new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                response = new() { ["success"] = true, ["data"] = result };
            }
        }
        catch (Exception error) { response = new() { ["success"] = false, ["error"] = error.Message }; }
        finally { _fakeNavigationCancelled = null; }
        await _writer.WriteAsync(new JsonObject { ["type"] = "extension_ui_request", ["id"] = Guid.NewGuid().ToString(),
            ["method"] = "setStatus", ["statusKey"] = "pistation-management:" + request["id"]!.ToString(),
            ["statusText"] = response.ToJsonString() }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
