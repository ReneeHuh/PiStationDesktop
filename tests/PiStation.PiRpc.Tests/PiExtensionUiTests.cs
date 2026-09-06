using System.Text.Json;
using PiStation.PiRpc.Decoding;
using PiStation.PiRpc.Wire.Events;

namespace PiStation.PiRpc.Tests;

public sealed class PiExtensionUiTests
{
    [Theory]
    [InlineData("""{"method":"notify","message":"Notice","notifyType":"warning"}""", "notify", "Notice")]
    [InlineData("""{"method":"setStatus","statusKey":"build","statusText":"Building"}""", "setStatus", "Building")]
    [InlineData("""{"method":"setStatus","statusKey":"build"}""", "setStatus", null)]
    [InlineData("""{"method":"setWidget","widgetKey":"plan","widgetLines":["One","Two"],"widgetPlacement":"belowEditor"}""", "setWidget", null)]
    [InlineData("""{"method":"setWidget","widgetKey":"plan"}""", "setWidget", null)]
    [InlineData("""{"method":"setTitle","title":"Review"}""", "setTitle", "Review")]
    [InlineData("""{"method":"set_editor_text","text":"Suggested draft"}""", "set_editor_text", "Suggested draft")]
    public void DecodesFireAndForgetUpdatesWithoutRequiringDialogFields(string payload, string method, string? text)
    {
        using var json = JsonDocument.Parse("""{"type":"extension_ui_request","id":"update-1",""" + payload[1..]);
        var update = Assert.IsType<PiExtensionUiUpdateEvent>(PiEventDecoder.Decode(json.RootElement));
        Assert.Equal(method, update.Method);
        Assert.Equal(text, update.Text);
        Assert.Equal("update-1", update.RequestId);
        if (update.Lines is not null)
        {
            Assert.Collection(update.Lines, line => Assert.Equal("One", line), line => Assert.Equal("Two", line));
            Assert.Equal("belowEditor", update.Placement);
        }
    }
}
