using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class TemporaryHistoryTests
{
    [Fact]
    public async Task DraftAndConversationIdentityAreMemoryOnlyAndDisappearOnClose()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions() with { TemporaryHistory = true };
        string id;
        await using (var database = new HostDatabase(options))
        {
            await database.InitializeAsync();
            var projects = new ProjectService(database);
            var project = await projects.AddAsync(new AddProjectRequest(directory.CreateDirectory("project")));
            var thread = await projects.CreateThreadAsync(new(project.ProjectId));
            id = thread.ThreadId.Value;
            var draft = await database.GetOrCreateThreadDraftAsync(thread.ThreadId);
            await database.UpdateThreadDraftAsync(thread.ThreadId, draft.DraftId, draft.Revision, "private temporary draft");
            Assert.Equal("private temporary draft", (await database.GetThreadDraftAsync(thread.ThreadId))!.Text);
            Assert.False(File.Exists(options.DatabasePath));
        }
        await using var reopened = new HostDatabase(options);
        await reopened.InitializeAsync();
        Assert.Null(await reopened.GetThreadAsync(PiStation.Protocol.Identifiers.ThreadId.Parse(id)));
        Assert.False(File.Exists(options.DatabasePath));
    }
}
