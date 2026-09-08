using System.Text;
using System.Text.Json.Nodes;
using PiStation.PiRpc.Sessions;

namespace PiStation.PiRpc.Tests;

public sealed class PiSessionDocumentTests
{
    public static string Fixture(string cwd = "C:/project") =>
        new JsonObject { ["type"] = "session", ["version"] = 3, ["id"] = "11111111111111111111111111111111", ["cwd"] = cwd, ["timestamp"] = "2026-09-06T00:00:00Z" }.ToJsonString() + "\n" +
        """
        {"type":"model_change","id":"model","parentId":null,"provider":"fake","modelId":"fake-fast"}
        {"type":"thinking_level_change","id":"thinking","parentId":"model","thinkingLevel":"off"}
        {"type":"message","id":"u1","parentId":"thinking","message":{"role":"user","content":"First question"}}
        {"type":"message","id":"a1","parentId":"u1","message":{"role":"assistant","content":[{"type":"text","text":"First answer <script>alert(1)</script>"}],"stopReason":"stop"}}
        {"type":"message","id":"u2","parentId":"a1","message":{"role":"user","content":"Other branch"}}
        {"type":"message","id":"a2","parentId":"u2","message":{"role":"assistant","content":[{"type":"text","text":"Other answer"}],"stopReason":"stop"}}
        {"type":"message","id":"u3","parentId":"a1","message":{"role":"user","content":"Active branch"}}
        {"type":"message","id":"a3","parentId":"u3","message":{"role":"assistant","content":[{"type":"text","text":"Active answer"}],"stopReason":"stop"}}
        """ + "\n";

    [Fact]
    public void ForkPreservesConfigurationAndSelectedBranchWithoutMutatingOriginal()
    {
        var source = Encoding.UTF8.GetBytes(Fixture());
        var document = PiSessionDocument.Parse(source);
        var copy = PiSessionDocument.Parse(document.Copy(Guid.NewGuid().ToString("N"), "C:/target", "C:/source.jsonl", "a2"));
        Assert.NotEqual(document.SessionId, copy.SessionId);
        Assert.Equal("C:/target", copy.ProjectDirectory);
        Assert.Equal("a2", copy.LeafId);
        Assert.Equal(("fake", "fake-fast", "off"), copy.Configuration());
        Assert.DoesNotContain(copy.Entries, entry => PiSessionDocument.Text(entry, "id") == "u3");
        Assert.Equal("a3", document.LeafId);
        Assert.Equal(8, document.Entries.Count);
        Assert.Equal(source, Encoding.UTF8.GetBytes(Fixture()));
        var fullCopy = PiSessionDocument.Parse(document.Copy(Guid.NewGuid().ToString("N"), "C:/target", "C:/source.jsonl"));
        Assert.Equal(8, fullCopy.Entries.Count);
    }

    [Fact]
    public void HtmlExportEscapesContentAndIncludesOnlyTheActiveBranch()
    {
        var document = PiSessionDocument.Parse(Encoding.UTF8.GetBytes(Fixture()));
        var html = document.ToHtml("<unsafe title>");
        Assert.Contains("&lt;unsafe title&gt;", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("Active answer", html);
        Assert.DoesNotContain("Other answer", html);
    }

    [Theory]
    [InlineData("\"parentId\":\"u3\"", "\"parentId\":\"missing\"")]
    [InlineData("\"id\":\"a3\"", "\"id\":\"a1\"")]
    [InlineData("\"version\":3", "\"version\":4")]
    public void InvalidOrUnsupportedSessionsAreRejected(string before, string after) =>
        Assert.Throws<InvalidDataException>(() => PiSessionDocument.Parse(Encoding.UTF8.GetBytes(Fixture().Replace(before, after, StringComparison.Ordinal))));

    [Fact]
    public void PartialTurnsCannotBeForked()
    {
        var document = PiSessionDocument.Parse(Encoding.UTF8.GetBytes(Fixture().Replace("\"stopReason\":\"stop\"", "\"stopReason\":\"toolUse\"", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => document.Copy(Guid.NewGuid().ToString("N"), "C:/target", "C:/source.jsonl", "a1"));
        Assert.Throws<InvalidDataException>(() => document.Copy(Guid.NewGuid().ToString("N"), "C:/target", "C:/source.jsonl", "u1"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LegacySessionsMigrateWithoutChangingSourceAndKeepStableEntryIds(int version)
    {
        var lines = Fixture().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        lines[0]["version"] = version;
        if (version == 1) foreach (var entry in lines.Skip(1)) { entry.Remove("id"); entry.Remove("parentId"); }
        lines[4]["message"]!["role"] = "hookMessage";
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines.Select(line => line.ToJsonString())));
        var original = bytes.ToArray();
        var first = PiSessionDocument.Parse(bytes);
        var second = PiSessionDocument.Parse(bytes);
        Assert.Equal(first.LeafId, second.LeafId);
        Assert.Equal("custom", first.Entries[3]["message"]!["role"]!.GetValue<string>());
        Assert.Equal(8, first.Entries.Count);
        Assert.Equal(original, bytes);
    }
}
