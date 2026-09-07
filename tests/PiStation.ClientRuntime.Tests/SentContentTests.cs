using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;

namespace PiStation.ClientRuntime.Tests;

public sealed class SentContentTests
{
    [Fact]
    public void ReferenceRoundTripDoesNotAlterOtherPromptContent()
    {
        var id = Guid.NewGuid().ToString("N");
        var prompt = "a prompt\nwith more text";
        var referenced = SentMessageReference.Append(prompt, id);
        Assert.Equal(id, SentMessageReference.Read(referenced));
        Assert.Equal(prompt, SentMessageReference.Remove(referenced));
        Assert.Equal(prompt + "\n[Attached: file]", SentMessageReference.Remove(referenced + "\n[Attached: file]"));
        Assert.Throws<ArgumentException>(() => SentMessageReference.Append(prompt, "invalid"));
    }

    [Fact]
    public void CitationFindsReloadedAnswerButDoesNotGuessBetweenDuplicatesOrChangedContent()
    {
        var citation = new ComposerContext("id", "response", "Pi", "selection", MessageId: "message-live",
            SourceTextSha256: CitationSourceResolver.Fingerprint("complete answer"));
        var reloaded = new MessageTimelineItem("message-persisted", null, "persisted", MessageRole.Assistant, "complete answer", true);
        Assert.Equal("message-persisted", CitationSourceResolver.Resolve(citation, [reloaded]));
        Assert.Null(CitationSourceResolver.Resolve(citation, [reloaded, reloaded with { ItemId = "message-other", MessageId = "other" }]));
        Assert.Null(CitationSourceResolver.Resolve(citation, [reloaded with { Text = "different answer" }]));
        Assert.Null(CitationSourceResolver.Resolve(citation, [reloaded with { Role = MessageRole.User }]));
        Assert.Null(CitationSourceResolver.Resolve(citation with { SourceTextSha256 = null }, [reloaded]));
        Assert.Equal("message-persisted", CitationSourceResolver.Resolve(citation with { MessageId = "persisted" }, [reloaded]));
    }

    [Fact]
    public async Task AttachmentAccessRejectsChangedOrMissingFiles()
    {
        using var directory = new ClientTestDirectory();
        var path = Path.Combine(directory.CreateDirectory("files"), "notes.txt");
        await File.WriteAllTextAsync(path, "original");
        var attachment = new DraftAttachment(EnvironmentId.New(), ThreadId.New(), DraftId.New(), AttachmentId.New(),
            "notes.txt", "text/plain", 8, CitationSourceResolver.Fingerprint("original"), path, DateTimeOffset.UtcNow);
        await SentAttachmentAccess.VerifyAsync(attachment);
        await File.WriteAllTextAsync(path, "modified");
        await Assert.ThrowsAsync<IOException>(() => SentAttachmentAccess.VerifyAsync(attachment));
        File.Delete(path);
        await Assert.ThrowsAsync<FileNotFoundException>(() => SentAttachmentAccess.VerifyAsync(attachment));
        await Assert.ThrowsAsync<IOException>(() => SentAttachmentAccess.VerifyAsync(attachment with { ServerPath = "relative.txt" }));
    }

    [Theory]
    [InlineData("normal prompt")]
    [InlineData("\n\n<pistation_message_ref>not-an-id</pistation_message_ref>")]
    [InlineData("\n\n<pistation_message_ref>01234567890123456789012345678901")]
    public void MalformedReferencesAreNotInterpreted(string text) => Assert.Null(SentMessageReference.Read(text));
}
