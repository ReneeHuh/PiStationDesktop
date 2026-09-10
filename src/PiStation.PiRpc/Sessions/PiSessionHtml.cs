using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using static PiStation.PiRpc.Sessions.PiSessionDocument;

namespace PiStation.PiRpc.Sessions;

internal static class PiSessionHtml
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    internal static string Render(PiSessionDocument document, string title)
    {
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
            <style>body{max-width:960px;margin:32px auto;padding:0 24px;font:16px/1.6 system-ui;color:#202124;background:#fff}article{border-top:1px solid #ccc;padding:16px 0}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#f3f4f6;padding:12px}code{font-family:Consolas,monospace}img{max-width:100%;height:auto}small{color:#555}table{border-collapse:collapse}td,th{border:1px solid #bbb;padding:6px}details{padding:8px;border:1px solid #ddd}blockquote{border-left:3px solid #aaa;padding-left:16px}a{overflow-wrap:anywhere}</style><title>
            """).Append(Encode(title)).Append("</title></head><body><h1>").Append(Encode(title))
            .Append("</h1><p>Active conversation branch. Embedded images are included. Workspace files and remote images are not fetched.</p>");
        var labels = document.Labels();
        foreach (var entry in document.Branch())
        {
            var kind = Text(entry, "type");
            var message = entry["message"] as JsonObject;
            if (message is null && kind is not ("custom_message" or "compaction" or "branch_summary")) continue;
            html.Append("<article id=\"").Append(Encode(Text(entry, "id"))).Append("\"><h2>")
                .Append(Encode(message is null ? kind : Text(message, "role")));
            if (labels.TryGetValue(Text(entry, "id")!, out var label)) html.Append(" · ").Append(Encode(label.Label));
            html.Append("</h2><small>").Append(Encode(Text(entry, "timestamp"))).Append("</small>");
            if (message is null) Content(html, entry["content"] ?? entry["summary"]);
            else
            {
                if (Text(message, "toolName") is { } tool) html.Append("<h3>").Append(Encode(tool)).Append("</h3>");
                if (Text(message, "command") is { } command) html.Append("<pre>").Append(Encode(command)).Append("</pre>");
                Content(html, message["content"] ?? message["output"] ?? message["summary"]);
                if (message["usage"] is JsonObject usage) html.Append("<small>Usage: ").Append(Encode(usage.ToJsonString())).Append("</small>");
            }
            html.Append("</article>");
            if (html.Length > MaximumBytes) throw new InvalidDataException("HTML export exceeds the 128 MiB limit. Export JSONL instead.");
        }
        var result = html.Append("</body></html>").ToString();
        if (Encoding.UTF8.GetByteCount(result) > MaximumBytes) throw new InvalidDataException("HTML export exceeds the 128 MiB limit. Export JSONL instead.");
        return result;
    }

    private static void Content(StringBuilder html, JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text)) { html.Append(Markdown(text)); return; }
        if (content is not JsonArray blocks) return;
        foreach (var block in blocks.OfType<JsonObject>())
        {
            switch (Text(block, "type"))
            {
                case "text": html.Append(Markdown(Text(block, "text") ?? "")); break;
                case "thinking": html.Append("<details><summary>Thinking</summary>").Append(Markdown(Text(block, "thinking") ?? "")).Append("</details>"); break;
                case "toolCall": html.Append("<details><summary>Tool: ").Append(Encode(Text(block, "name"))).Append("</summary><pre>")
                    .Append(Encode(block["arguments"]?.ToJsonString())).Append("</pre></details>"); break;
                case "image":
                    var mime = Text(block, "mimeType");
                    var data = Text(block, "data");
                    if (mime is "image/png" or "image/jpeg" or "image/gif" or "image/webp" && data is { Length: > 0 and <= 24 * 1024 * 1024 } &&
                        data.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '\r' or '\n'))
                        html.Append("<img alt=\"Embedded session image\" src=\"data:").Append(mime).Append(";base64,").Append(data).Append("\">");
                    else html.Append("<p>[Unsupported image; preserved in JSONL export]</p>");
                    break;
                default: html.Append("<pre>").Append(Encode(block.ToJsonString())).Append("</pre>"); break;
            }
        }
    }

    private static string Markdown(string text)
    {
        var document = Markdig.Markdown.Parse(text, Pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            // Do not load tracking images or permit executable/file URLs in a standalone export.
            if (link.IsImage || !Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "mailto"))
                link.Url = "#";
        }
        return document.ToHtml(Pipeline);
    }
}
