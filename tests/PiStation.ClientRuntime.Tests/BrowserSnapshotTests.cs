using System.Text.Json;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class BrowserSnapshotTests
{
    [Fact]
    public void DiagnosticsAreBoundedTabScopedAndOnlyKeepNetworkFailures()
    {
        var first = new BrowserDiagnostics();
        var other = new BrowserDiagnostics();
        for (var i = 0; i < 205; i++) first.Receive("Runtime.consoleAPICalled", JsonSerializer.Serialize(new { type = "log", args = new[] { new { value = i } } }));
        Assert.Equal(200, first.ConsoleEntries.Count());
        Assert.True(first.ConsoleTruncated);
        Assert.Empty(other.ConsoleEntries);
        first.Receive("Network.requestWillBeSent", "{\"requestId\":\"1\",\"request\":{\"url\":\"http://proxy/fail\",\"method\":\"POST\",\"headers\":{\"Cookie\":\"secret\"}}}");
        first.Receive("Network.responseReceived", "{\"requestId\":\"1\",\"response\":{\"url\":\"http://proxy/fail\",\"status\":500}}", url => url.Replace("proxy", "logical"));
        first.Receive("Network.loadingFinished", "{\"requestId\":\"1\"}");
        first.Receive("Network.responseReceived", "{\"requestId\":\"2\",\"response\":{\"url\":\"http://ok\",\"status\":200}}");
        var failure = Assert.Single(first.NetworkEntries);
        Assert.Equal("http://logical/fail", failure["url"]!.GetValue<string>());
        Assert.Equal("POST", failure["method"]!.GetValue<string>());
        Assert.DoesNotContain("secret", failure.ToJsonString());
        first.Receive("Runtime.consoleAPICalled", "{");
        first.Clear();
        Assert.Empty(first.ConsoleEntries);
        Assert.Empty(first.NetworkEntries);
        Assert.False(first.ConsoleTruncated);
    }

    [Fact]
    public void SnapshotBoundsEverySectionAndReportsOmittedAccessibilityChildren()
    {
        var diagnostics = new BrowserDiagnostics();
        for (var i = 0; i < 210; i++)
        {
            diagnostics.StartAction(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "evaluate");
            diagnostics.FinishAction(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "interrupted", new string('e', 1000));
            diagnostics.Receive("Runtime.consoleAPICalled", JsonSerializer.Serialize(new { type = "error", args = new[] { new { value = new string('界', 2000) } } }));
        }
        var page = JsonSerializer.SerializeToElement(new { url = "http://proxy", title = "Title", loading = false, visibleText = new string('界', 20000),
            interactiveElements = Enumerable.Range(0, 300).Select(i => new { name = new string('n', 300), selector = "#" + i }), textTruncated = true });
        var tree = JsonSerializer.SerializeToElement(new { nodes = Enumerable.Range(0, 1000).Select(i => new { nodeId = i.ToString(System.Globalization.CultureInfo.InvariantCulture), role = new { value = "button" }, name = new { value = new string('界', 600) }, childIds = new[] { (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) } }) });
        var snapshot = BrowserSnapshotBuilder.Build(page, tree, diagnostics, 1280, 720, url => url.Replace("proxy", "logical"));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(snapshot.GetRawText()) <= BrowserAutomationLimits.MaximumDataBytes);
        Assert.Equal("http://logical", snapshot.GetProperty("url").GetString());
        Assert.True(snapshot.GetProperty("truncated").GetProperty("accessibilityTree").GetBoolean());
        Assert.True(snapshot.GetProperty("truncated").GetProperty("interactiveElements").GetBoolean());
        Assert.True(snapshot.GetProperty("truncated").GetProperty("consoleEntries").GetBoolean());
        Assert.True(snapshot.GetProperty("truncated").GetProperty("actionTimeline").GetBoolean());
        Assert.True(snapshot.GetProperty("accessibilityTree").GetProperty("nodes").GetArrayLength() <= 250);
        Assert.Equal(1280, snapshot.GetProperty("screenshot").GetProperty("width").GetInt32());
        Assert.Equal("interrupted", snapshot.GetProperty("actionTimeline").EnumerateArray().Last().GetProperty("status").GetString());
        Assert.Equal(200, diagnostics.ActionTimeline.Count());
    }
}
