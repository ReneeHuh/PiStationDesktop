using System.Text.Json;
using PiStation.PiRpc.Decoding;
using PiStation.PiRpc.Wire.Events;

namespace PiStation.PiRpc.Tests;

public sealed class PiEventTests
{
    [Fact]
    public void DecoderReadsDeltaOnlyMessageUpdate()
    {
        var record = Parse("""
            {
              "type": "message_update",
              "assistantMessageEvent": {
                "type": "text_delta",
                "contentIndex": 0,
                "delta": "hello"
              }
            }
            """);

        var update = Assert.IsType<PiMessageUpdateEvent>(PiEventDecoder.Decode(record));
        var delta = Assert.IsType<PiTextDelta>(update.Delta);
        Assert.Equal("hello", delta.Delta);
    }

    [Fact]
    public void DecoderReadsOptionalAssistantTokenUsageWithoutInventingMissingValues()
    {
        var completed = Assert.IsType<PiMessageCompletedEvent>(PiEventDecoder.Decode(Parse("""
            {
              "type": "message_end",
              "message": {
                "role": "assistant",
                "content": [],
                "usage": {
                  "input": 120,
                  "output": 30,
                  "cacheRead": 40,
                  "cacheWrite": 10,
                  "reasoning": 12,
                  "totalTokens": 200
                }
              }
            }
            """)));
        var missing = Assert.IsType<PiMessageCompletedEvent>(PiEventDecoder.Decode(Parse("""
            { "type": "message_end", "message": { "role": "assistant", "content": [] } }
            """)));

        Assert.Equal(120, completed.Usage?.InputTokens);
        Assert.Equal(12, completed.Usage?.ReasoningTokens);
        Assert.Equal(200, completed.Usage?.ContextTokens);
        Assert.Null(missing.Usage);
    }

    [Fact]
    public void DecoderPreservesUnknownEvents()
    {
        var unknown = Assert.IsType<PiUnknownEvent>(PiEventDecoder.Decode(Parse("""
            { "type": "future_event", "newField": 42 }
            """)));

        Assert.Equal("future_event", unknown.Type);
        Assert.Equal(42, unknown.Payload.GetProperty("newField").GetInt32());
    }

    [Fact]
    public void DecoderReadsBlockingExtensionUiRequests()
    {
        var approval = Assert.IsType<PiConfirmRequestedEvent>(PiEventDecoder.Decode(Parse("""
            {
              "type": "extension_ui_request",
              "id": "approval-1",
              "method": "confirm",
              "title": "Allow?",
              "message": "Run it",
              "timeout": 5000
            }
            """)));
        var question = Assert.IsType<PiSelectRequestedEvent>(PiEventDecoder.Decode(Parse("""
            {
              "type": "extension_ui_request",
              "id": "question-1",
              "method": "select",
              "title": "Choose",
              "options": ["One", "Two"]
            }
            """)));
        var input = Assert.IsType<PiInputRequestedEvent>(PiEventDecoder.Decode(Parse("""
            {
              "type": "extension_ui_request",
              "id": "input-1",
              "method": "input",
              "title": "Name",
              "placeholder": "Type a name"
            }
            """)));
        var editor = Assert.IsType<PiEditorRequestedEvent>(PiEventDecoder.Decode(Parse("""
            {
              "type": "extension_ui_request",
              "id": "editor-1",
              "method": "editor",
              "title": "Edit plan",
              "prefill": "Initial plan"
            }
            """)));

        Assert.Equal("Run it", approval.Message);
        Assert.Equal(5000, approval.TimeoutMilliseconds);
        Assert.Equal(["One", "Two"], question.Options);
        Assert.Equal("Type a name", input.Placeholder);
        Assert.Equal("Initial plan", editor.Prefill);
    }

    [Fact]
    public void AssemblerUsesAuthoritativeMessageEnd()
    {
        var assembler = new PiStreamAssembler();
        assembler.Apply(new PiAgentStartedEvent());
        assembler.Apply(new PiMessageStartedEvent(Parse("""
            { "role": "assistant", "content": [] }
            """)));
        assembler.Apply(new PiMessageUpdateEvent(new PiTextStartedDelta(0)));
        assembler.Apply(new PiMessageUpdateEvent(new PiTextDelta(0, "draft")));
        Assert.Equal("draft", assembler.Text);

        assembler.Apply(new PiMessageCompletedEvent(Parse("""
            {
              "role": "assistant",
              "content": [{ "type": "text", "text": "authoritative" }]
            }
            """)));
        assembler.Apply(new PiAgentSettledEvent());

        Assert.Equal("authoritative", assembler.Text);
        Assert.True(assembler.IsSettled);
    }

    [Fact]
    public void ToolPartialResultsReplacePreviousOutput()
    {
        var assembler = new PiStreamAssembler();
        var arguments = Parse("{ \"command\": \"echo\" }");
        assembler.Apply(new PiToolExecutionStartedEvent("call-1", "bash", arguments));
        assembler.Apply(new PiToolExecutionUpdatedEvent(
            "call-1",
            "bash",
            arguments,
            ToolResult("one")));
        assembler.Apply(new PiToolExecutionUpdatedEvent(
            "call-1",
            "bash",
            arguments,
            ToolResult("one\ntwo")));

        Assert.Equal("one\ntwo", assembler.ToolExecutions["call-1"].Output);
    }

    private static JsonElement ToolResult(string text) => Parse($$"""
        { "content": [{ "type": "text", "text": {{JsonSerializer.Serialize(text)}} }] }
        """);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
