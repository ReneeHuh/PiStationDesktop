using System.Text.Json;

namespace PiStation.ClientRuntime.Tests;

public sealed class BrowserAutomationCommandTests
{
    private static BrowserAutomationCommand Parse(string operation, string json) =>
        BrowserAutomationCommand.Parse(operation, JsonDocument.Parse(json).RootElement);

    [Theory]
    [InlineData("scroll", "{\"deltaY\":600}", true)]
    [InlineData("press_key", "{\"key\":\"Enter\"}", true)]
    [InlineData("wait", "{\"condition\":\"loaded\"}", false)]
    [InlineData("wait", "{\"selector\":\"#ready\"}", false)]
    [InlineData("status", "{}", false)]
    [InlineData("open", "{}", true)]
    [InlineData("resize", "{\"mode\":\"fill\"}", true)]
    [InlineData("set_appearance", "{\"colorScheme\":\"dark\"}", true)]
    [InlineData("evaluate", "{\"expression\":\"document.title\"}", true)]
    [InlineData("snapshot", "{}", false)]
    public void ValidCommandsHaveExplicitPermissionClassification(string operation, string json, bool interacts) =>
        Assert.Equal(interacts, Parse(operation, json).RequiresInteraction);

    [Theory]
    [InlineData("press_key", "{\"key\":\"F99\"}")]
    [InlineData("press_key", "{\"key\":\"Enter\",\"modifiers\":16}")]
    [InlineData("scroll", "{\"deltaY\":10001}")]
    [InlineData("scroll", "{\"deltaY\":1.5}")]
    [InlineData("wait", "{\"condition\":\"script\"}")]
    [InlineData("wait", "{\"condition\":\"text\",\"selector\":\"body\"}")]
    [InlineData("wait", "{\"selector\":\"body\",\"timeoutMs\":20001}")]
    [InlineData("status", "{\"tabId\":null}")]
    [InlineData("evaluate", "{}")]
    [InlineData("evaluate", "{\"expression\":\"1\",\"returnByValue\":false}")]
    [InlineData("evaluate", "{\"expression\":\"1\",\"awaitPromise\":\"yes\"}")]
    [InlineData("evaluate", "{\"expression\":\" \"}")]
    [InlineData("open", "{\"tabId\":\"existing\",\"reuseExistingTab\":false}")]
    [InlineData("open", "{\"open\":\"false\"}")]
    [InlineData("resize", "{\"mode\":\"fill\",\"width\":800}")]
    [InlineData("resize", "{\"mode\":\"freeform\",\"width\":800}")]
    [InlineData("resize", "{\"mode\":\"freeform\",\"width\":3840,\"height\":3840}")]
    [InlineData("resize", "{\"mode\":\"freeform\",\"width\":239,\"height\":768}")]
    [InlineData("resize", "{\"mode\":\"preset\",\"preset\":\"phone\",\"orientation\":\"upside-down\"}")]
    [InlineData("resize", "{\"mode\":\"preset\",\"preset\":\"unknown\"}")]
    [InlineData("set_appearance", "{\"colorScheme\":\"automatic\"}")]
    [InlineData("set_appearance", "{}")]
    public void InvalidCommandsAreRejected(string operation, string json) =>
        Assert.Throws<ArgumentException>(() => Parse(operation, json));

    [Fact]
    public void ExplicitTabAndWaitParametersArePreserved()
    {
        var command = Parse("wait", "{\"tabId\":\"other-tab\",\"condition\":\"text\",\"selector\":\"#result\",\"value\":\"done\",\"timeoutMs\":1500}");
        Assert.Equal("other-tab", command.TabId);
        Assert.Equal(1500, command.TimeoutMs);
        Assert.Equal("done", command.Value);
    }

    [Fact]
    public void OpenAndViewportContractsPreserveIntent()
    {
        var open = Parse("open", "{\"open\":false,\"reuseExistingTab\":false,\"url\":\"http://localhost:5173\"}");
        Assert.False(open.Open);
        Assert.False(open.ReuseExistingTab);
        Assert.Equal("http://localhost:5173", open.Url);
        Assert.Equal(new("freeform", 1024, 768), Parse("resize", "{\"mode\":\"freeform\",\"width\":1024,\"height\":768}").Viewport);
        Assert.Equal(new("preset", 844, 390, "phone"), Parse("resize", "{\"mode\":\"preset\",\"preset\":\"phone\",\"orientation\":\"landscape\"}").Viewport);
    }

    [Fact]
    public void EvaluationPreservesPromiseAndTargetIntentAndBoundsExpression()
    {
        var command = Parse("evaluate", "{\"tabId\":\"background\",\"expression\":\"Promise.resolve(42)\",\"awaitPromise\":false,\"timeoutMs\":1200}");
        Assert.Equal("background", command.TabId);
        Assert.Equal("Promise.resolve(42)", command.Expression);
        Assert.False(command.AwaitPromise);
        Assert.Equal(1200, command.TimeoutMs);
        Assert.True(Parse("evaluate", "{\"expression\":\"1\"}").AwaitPromise);
        Assert.Throws<ArgumentException>(() => Parse("evaluate", JsonSerializer.Serialize(new { expression = new string('x', 32001) })));
    }
}
