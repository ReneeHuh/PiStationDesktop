using System.Text.Json;
using System.Text.Json.Nodes;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

public static class BrowserSnapshotBuilder
{
    public static JsonElement Build(JsonElement page, JsonElement accessibility, BrowserDiagnostics diagnostics,
        int imageWidth, int imageHeight, Func<string, string>? logicalUrl = null)
    {
        var truncated = new JsonObject();
        var text = BrowserDiagnostics.Text(page, "visibleText", 20_000);
        truncated["visibleText"] = page.TryGetProperty("textTruncated", out var textCut) && textCut.ValueKind == JsonValueKind.True;
        var elements = page.TryGetProperty("interactiveElements", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(item => JsonNode.Parse(item.GetRawText())!.AsObject()) : [];
        var elementList = Bounded(elements, 200, 64 * 1024, out var elementCut);
        truncated["interactiveElements"] = elementCut || page.TryGetProperty("elementsTruncated", out var elementsCut) && elementsCut.ValueKind == JsonValueKind.True;
        var nodes = accessibility.TryGetProperty("nodes", out var axNodes) && axNodes.ValueKind == JsonValueKind.Array
            ? axNodes.EnumerateArray().Select(ProjectAccessibilityNode) : [];
        var tree = Bounded(nodes, 250, 64 * 1024, out var treeCut);
        // Child references outside the returned tree also signal depth/count truncation.
        var ids = tree.OfType<JsonObject>().Select(node => node["nodeId"]!.GetValue<string>()).ToHashSet();
        treeCut |= tree.OfType<JsonObject>().Any(node => node["childrenTruncated"]?.GetValue<bool>() == true ||
            node["propertiesTruncated"]?.GetValue<bool>() == true ||
            node["textTruncated"]?.GetValue<bool>() == true ||
            node["childIds"]!.AsArray().Any(id => !ids.Contains(id!.GetValue<string>())));
        truncated["accessibilityTree"] = treeCut;
        var console = Bounded(diagnostics.ConsoleEntries.Reverse(), 200, 24 * 1024, out var consoleCut);
        var network = Bounded(diagnostics.NetworkEntries.Reverse(), 200, 24 * 1024, out var networkCut);
        var actions = Bounded(diagnostics.ActionTimeline.Reverse(), 200, 16 * 1024, out var actionCut);
        truncated["consoleEntries"] = consoleCut || diagnostics.ConsoleTruncated;
        truncated["networkEntries"] = networkCut || diagnostics.NetworkTruncated;
        truncated["actionTimeline"] = actionCut || diagnostics.ActionsTruncated;
        var url = BrowserDiagnostics.Text(page, "url", 2048);
        var result = new JsonObject
        {
            ["url"] = logicalUrl?.Invoke(url) ?? url, ["title"] = BrowserDiagnostics.Text(page, "title", 512),
            ["loading"] = page.TryGetProperty("loading", out var loading) && loading.ValueKind == JsonValueKind.True,
            ["visibleText"] = text, ["interactiveElements"] = elementList, ["accessibilityTree"] = new JsonObject { ["nodes"] = tree },
            ["consoleEntries"] = Reverse(console), ["networkEntries"] = Reverse(network), ["actionTimeline"] = Reverse(actions),
            ["diagnosticsSinceUtc"] = diagnostics.SinceUtc, ["truncated"] = truncated,
            ["screenshot"] = new JsonObject { ["mimeType"] = "image/png", ["width"] = imageWidth, ["height"] = imageHeight },
        };
        // Escape-heavy/non-ASCII text can consume more bytes than characters. Prefer an
        // explicit truncation over rejecting an otherwise useful snapshot.
        while (JsonSerializer.SerializeToUtf8Bytes(result).Length > BrowserAutomationLimits.MaximumDataBytes)
        {
            text = text[..(text.Length / 2)];
            result["visibleText"] = text;
            truncated["visibleText"] = true;
            if (text.Length == 0 && JsonSerializer.SerializeToUtf8Bytes(result).Length > BrowserAutomationLimits.MaximumDataBytes)
                throw new InvalidOperationException("The snapshot exceeded its total byte budget.");
        }
        return JsonSerializer.SerializeToElement(result);
    }

    private static JsonArray Reverse(JsonArray array) => new(array.Reverse().Select(node => node!.DeepClone()).ToArray());

    private static JsonArray Bounded(IEnumerable<JsonObject> source, int count, int byteBudget, out bool truncated)
    {
        var result = new JsonArray();
        var bytes = 2;
        truncated = false;
        foreach (var entry in source)
        {
            var size = JsonSerializer.SerializeToUtf8Bytes(entry).Length + 1;
            if (result.Count == count || bytes + size > byteBudget) { truncated = true; break; }
            bytes += size;
            result.Add(entry.DeepClone());
        }
        return result;
    }

    private static JsonObject ProjectAccessibilityNode(JsonElement node)
    {
        JsonNode? Value(string name) => node.TryGetProperty(name, out var item) && item.TryGetProperty("value", out var value)
            ? JsonValue.Create(BrowserDiagnostics.Limit(value.ToString(), 512)) : null;
        var children = node.TryGetProperty("childIds", out var ids) && ids.ValueKind == JsonValueKind.Array ? ids.EnumerateArray().ToArray() : [];
        var properties = node.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Array ? props.EnumerateArray().ToArray() : [];
        var textTruncated = node.EnumerateObject().Any(property => property.Value.ValueKind == JsonValueKind.Object &&
            property.Value.TryGetProperty("value", out var value) && value.ToString().Length > 512);
        return new JsonObject
        {
            ["nodeId"] = BrowserDiagnostics.Text(node, "nodeId", 160),
            ["backendDOMNodeId"] = node.TryGetProperty("backendDOMNodeId", out var backend) && backend.TryGetInt32(out var id) ? id : null,
            ["ignored"] = node.TryGetProperty("ignored", out var ignored) && ignored.ValueKind == JsonValueKind.True,
            ["role"] = Value("role"), ["name"] = Value("name"), ["description"] = Value("description"), ["value"] = Value("value"),
            ["childIds"] = new JsonArray(children.Take(32).Select(child => (JsonNode?)JsonValue.Create(BrowserDiagnostics.Limit(child.ToString(), 160))).ToArray()),
            ["childrenTruncated"] = children.Length > 32, ["propertiesTruncated"] = properties.Length > 16,
            ["textTruncated"] = textTruncated,
            ["properties"] = new JsonArray(properties.Take(16).Select(property => (JsonNode)new JsonObject
            {
                ["name"] = BrowserDiagnostics.Text(property, "name", 64),
                ["value"] = property.TryGetProperty("value", out var value) ? BrowserDiagnostics.Text(value, "value", 256) : "",
            }).ToArray()),
        };
    }
}
