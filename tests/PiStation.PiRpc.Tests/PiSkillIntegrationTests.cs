using System.Text.Json;
using System.Text.Json.Nodes;
using PiStation.PiRpc.Decoding;
using PiStation.PiRpc.Diagnostics;
using PiStation.PiRpc.Transport;

namespace PiStation.PiRpc.Tests;

public sealed class PiSkillIntegrationTests
{
    [Theory]
    [InlineData("""{"name":"review","source":"skill","path":"C:/skills/review/SKILL.md","location":"user"}""")]
    [InlineData("""{"name":"skill:review","source":"skill","sourceInfo":{"path":"C:/skills/review/SKILL.md","scope":"user","origin":"package","source":"npm:review","baseDir":"C:/skills"}}""")]
    public void ReadsLegacyAndCurrentResourceMetadata(string json)
    {
        using var document = JsonDocument.Parse(json);
        var command = PiCommandReader.Read(document.RootElement);
        Assert.Equal("skill:review", command.Name);
        Assert.Equal("C:/skills/review/SKILL.md", command.Path);
        Assert.Equal("user", command.Location);
        if (command.SourceInfo is { } source)
        {
            Assert.Equal("npm:review", source.Source);
            Assert.Equal("package", source.Origin);
            Assert.Equal("C:/skills", source.BaseDir);
        }
    }

    [Fact]
    public async Task InlineSkillsIncludeInstructionsOnceAndPreservePromptAndAttachmentsAfterHydration()
    {
        using var directory = new TemporaryDirectory();
        var reviewPath = directory.GetPath("review.md");
        var testPath = directory.GetPath("test.md");
        await File.WriteAllTextAsync(reviewPath, "---\nname: review\ndescription: Review\n---\nReview Unicode 日本語 changes.");
        await File.WriteAllTextAsync(testPath, "Run meaningful checks.");
        PiCommandInfo[] commands =
        [
            new("skill:review", null, "skill", "user", reviewPath),
            new("skill:test", null, "skill", "project", testPath),
        ];
        const string prompt = "Please use $skill:review and $skill:test, then $skill:review again.";
        var expanded = await PiSkillPromptExpander.ExpandAsync(prompt, commands);
        Assert.StartsWith(prompt, expanded);
        Assert.Contains("Review Unicode 日本語 changes.", expanded);
        Assert.Contains("Run meaningful checks.", expanded);
        Assert.DoesNotContain("description: Review", expanded);
        Assert.Equal(1, expanded.Split("Review Unicode").Length - 1);
        Assert.Equal(prompt, PiPromptFormatter.NormalizePersistedMessage(expanded));
        var withAttachment = PiPromptFormatter.CreateRpcMessage(expanded,
            [new("attachment-1", "notes.txt", "text/plain", directory.GetPath("notes.txt"), new string('A', 64))]);
        Assert.Equal(prompt + "\n\n[Attached: notes.txt]", PiPromptFormatter.NormalizePersistedMessage(withAttachment));
    }

    [Fact]
    public async Task QuotedContextCodeAndEscapedMentionsDoNotInvokeSkills()
    {
        const string prompt = """
            Explain `$skill:inline` and \$skill:escaped.
            ```text
            $skill:fenced
            ```
            ~~~
            $skill:tilde
            ~~~
            <pistation_context type="quote">Use $skill:quoted.</pistation_context>
            """;
        Assert.Empty(PiSkillPromptExpander.ReadMentions(prompt));
        Assert.Equal(prompt, await PiSkillPromptExpander.ExpandAsync(prompt, []));
        Assert.Equal("skill:real", Assert.Single(PiSkillPromptExpander.ReadMentions(prompt + "\nUse $skill:real.")));
    }

    [Fact]
    public async Task MissingOrOversizedSkillsFailBeforePromptDispatch()
    {
        using var directory = new TemporaryDirectory();
        await using var process = await FakePiTestHost.StartAsync(directory);
        await Assert.ThrowsAsync<PiRpcCommandException>(() => process.Connection.PromptAsync("$skill:missing do work"));
        var log = File.ReadAllText(directory.GetPath("sessions/command-log.jsonl"));
        Assert.DoesNotContain("\"command\":\"prompt\"", log);
        var path = directory.GetPath("large.md");
        await File.WriteAllBytesAsync(path, new byte[PiSkillPromptExpander.MaximumSkillBytes + 1]);
        await Assert.ThrowsAsync<IOException>(() => PiSkillPromptExpander.ExpandAsync("$skill:large",
            [new("skill:large", null, "skill", null, path)]));
    }

    [Fact]
    public async Task RealisticDiscoveryFixtureExpandsSkillsInPromptTransport()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var process = await FakePiTestHost.StartAsync(directory, cancellationToken: timeout.Token);
        await process.Connection.PromptAsync("Please use $skill:fake-skill on this change.", timeout.Token);
        await FakePiTestHost.ReadUntilSettledAsync(process.Connection, timeout.Token);
        var record = File.ReadLines(directory.GetPath("sessions/command-log.jsonl"))
            .Select(line => JsonNode.Parse(line)!).Single(item => item["command"]?.GetValue<string>() == "prompt");
        Assert.Contains("FAKE_SKILL_INSTRUCTION", record["message"]!.GetValue<string>());
        Assert.Equal("Please use $skill:fake-skill on this change.",
            PiPromptFormatter.NormalizePersistedMessage(record["message"]!.GetValue<string>()));
    }
}
