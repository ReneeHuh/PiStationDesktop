using System.Text.Json.Nodes;

namespace PiStation.PiRpc.Transport;

public sealed record PiPromptAttachment(
    string AttachmentId,
    string FileName,
    string MediaType,
    string ServerPath,
    string Sha256);

public static class PiPromptFormatter
{
    private const string ManifestStart = "\n\n<pistation_attachments>\n";
    private const string ManifestEnd = "\n</pistation_attachments>";

    public static string CreateRpcMessage(
        string message,
        IReadOnlyList<PiPromptAttachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Count == 0)
        {
            return message;
        }

        var items = new JsonArray();
        foreach (var attachment in attachments)
        {
            items.Add(new JsonObject
            {
                ["attachmentId"] = attachment.AttachmentId,
                ["fileName"] = attachment.FileName,
                ["mediaType"] = attachment.MediaType,
                ["path"] = attachment.ServerPath,
                ["sha256"] = attachment.Sha256,
            });
        }

        var manifest = new JsonObject
        {
            ["source"] = "PiStationDesktop",
            ["version"] = 1,
            ["attachments"] = items,
        };
        return message + ManifestStart + manifest.ToJsonString() + ManifestEnd;
    }

    public static string CreateDisplayMessage(
        string message,
        IReadOnlyList<PiPromptAttachment> attachments) =>
        CreateDisplayMessage(message, attachments.Select(static attachment => attachment.FileName));

    public static string NormalizePersistedMessage(string message) => PiSkillPromptExpander.RemoveExpansion(NormalizeAttachments(message));

    private static string NormalizeAttachments(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var start = message.LastIndexOf(ManifestStart, StringComparison.Ordinal);
        if (start < 0 || !message.EndsWith(ManifestEnd, StringComparison.Ordinal))
        {
            return message;
        }

        var jsonStart = start + ManifestStart.Length;
        var jsonLength = message.Length - jsonStart - ManifestEnd.Length;
        try
        {
            var manifest = JsonNode.Parse(message.Substring(jsonStart, jsonLength)) as JsonObject;
            if (manifest?["source"]?.GetValue<string>() != "PiStationDesktop" ||
                manifest["version"]?.GetValue<int>() != 1 ||
                manifest["attachments"] is not JsonArray attachments)
            {
                return message;
            }

            var names = new List<string>();
            foreach (var node in attachments)
            {
                if (node is not JsonObject attachment)
                {
                    return message;
                }

                var attachmentId = attachment["attachmentId"]?.GetValue<string>();
                var path = attachment["path"]?.GetValue<string>();
                var fileName = attachment["fileName"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(attachmentId) ||
                    string.IsNullOrWhiteSpace(path) ||
                    string.IsNullOrWhiteSpace(fileName))
                {
                    return message;
                }

                names.Add(fileName);
            }

            return CreateDisplayMessage(PiSkillPromptExpander.RemoveExpansion(message[..start]), names);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException)
        {
            return message;
        }
    }

    private static string CreateDisplayMessage(string message, IEnumerable<string> fileNames)
    {
        var names = string.Join(", ", fileNames);
        if (string.IsNullOrEmpty(names))
        {
            return message;
        }

        return string.IsNullOrEmpty(message)
            ? $"[Attached: {names}]"
            : $"{message}\n\n[Attached: {names}]";
    }
}
