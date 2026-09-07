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
}
