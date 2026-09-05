using Microsoft.Data.Sqlite;
using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.Host.Tests;

public sealed class HostDatabaseTests
{
    [Fact]
    public async Task EnvironmentProjectAndThreadIdentitySurviveRestart()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectPath = temporaryDirectory.CreateDirectory("project");
        var firstDatabase = new HostDatabase(options);
        var firstEnvironment = await firstDatabase.InitializeAsync();
        var projects = new ProjectService(firstDatabase);
        var project = await projects.AddAsync(new AddProjectRequest(projectPath));
        var duplicate = await projects.AddAsync(new AddProjectRequest(projectPath));
        var thread = await projects.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));

        var secondDatabase = new HostDatabase(options);
        var secondEnvironment = await secondDatabase.InitializeAsync();
        var restoredThread = await secondDatabase.GetThreadAsync(thread.ThreadId);

        Assert.Equal(firstEnvironment.EnvironmentId, secondEnvironment.EnvironmentId);
        Assert.Equal(project.ProjectId, duplicate.ProjectId);
        Assert.NotNull(restoredThread);
        Assert.Equal(thread.ThreadId.Value, restoredThread.PiSessionId);
        Assert.Null(restoredThread.PiSessionFile);
    }

    [Fact]
    public async Task PiConfigurationRevisionAndSelectionSurviveRestart()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var projects = new ProjectService(database);
        var project = await projects.AddAsync(new AddProjectRequest(
            temporaryDirectory.CreateDirectory("project")));
        var thread = await projects.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        var initial = await database.GetOrCreateThreadPiConfigurationAsync(thread.ThreadId);
        var model = new PiModelSelection("fake", "fake-standard");

        var updated = await database.UpdateThreadPiConfigurationAsync(
            thread.ThreadId,
            initial.Revision,
            model,
            PiThinkingLevel.High,
            null);
        var stale = await database.UpdateThreadPiConfigurationAsync(
            thread.ThreadId,
            initial.Revision,
            new PiModelSelection("fake", "fake-fast"),
            PiThinkingLevel.Off,
            null);
        var restarted = new HostDatabase(options);
        await restarted.InitializeAsync();
        var restored = await restarted.GetOrCreateThreadPiConfigurationAsync(thread.ThreadId);

        Assert.True(updated.WasUpdated);
        Assert.Equal(1, updated.Configuration?.Revision);
        Assert.False(stale.WasUpdated);
        Assert.Equal(model, stale.Configuration?.Model);
        Assert.Equal(model, restored.Model);
        Assert.Equal(PiThinkingLevel.High, restored.ThinkingLevel);
        Assert.Equal(1, restored.Revision);
    }

    [Fact]
    public async Task ThreadLifecycleMetadataFiltersSearchesOrdersAndSurvivesRestart()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var projects = new ProjectService(database);
        var project = await projects.AddAsync(new AddProjectRequest(
            temporaryDirectory.CreateDirectory("project")));
        var first = await projects.CreateThreadAsync(new CreateThreadRequest(project.ProjectId, "Alpha % plan"));
        var pinned = await projects.CreateThreadAsync(new CreateThreadRequest(project.ProjectId, "Pinned work"));
        var active = await projects.CreateThreadAsync(new CreateThreadRequest(project.ProjectId, "Active work"));

        var renamed = await database.UpdateThreadMetadataAsync(
            first.ThreadId,
            first.Revision,
            "Roadmap % review",
            null,
            null);
        var stale = await database.UpdateThreadMetadataAsync(
            first.ThreadId,
            first.Revision,
            "Stale title",
            null,
            null);
        var pinnedUpdate = await database.UpdateThreadMetadataAsync(
            pinned.ThreadId,
            pinned.Revision,
            null,
            null,
            true);
        var archived = await database.UpdateThreadMetadataAsync(
            first.ThreadId,
            renamed.Thread!.Revision,
            null,
            true,
            null);
        var listed = await database.ListThreadsAsync(project.ProjectId);
        var listedWithArchived = await database.ListThreadsAsync(project.ProjectId, includeArchived: true);
        var hiddenSearch = await database.SearchThreadsAsync(project.ProjectId, "roadmap", false, 10);
        var archivedSearch = await database.SearchThreadsAsync(project.ProjectId, "roadmap", true, 10);
        var literalWildcardSearch = await database.SearchThreadsAsync(project.ProjectId, "%", true, 10);

        var restarted = new HostDatabase(options);
        await restarted.InitializeAsync();
        var restoredFirst = await restarted.GetThreadAsync(first.ThreadId);
        var restoredPinned = await restarted.GetThreadAsync(pinned.ThreadId);

        Assert.True(renamed.WasUpdated);
        Assert.Equal(1, renamed.Thread.Revision);
        Assert.False(stale.WasUpdated);
        Assert.Equal("Roadmap % review", stale.Thread?.Title);
        Assert.True(pinnedUpdate.WasUpdated);
        Assert.Equal([pinned.ThreadId, active.ThreadId], listed.Select(static thread => thread.ThreadId));
        Assert.Equal(
            [pinned.ThreadId, active.ThreadId, first.ThreadId],
            listedWithArchived.Select(static thread => thread.ThreadId));
        Assert.Empty(hiddenSearch);
        Assert.Equal(first.ThreadId, Assert.Single(archivedSearch).ThreadId);
        Assert.Equal(first.ThreadId, Assert.Single(literalWildcardSearch).ThreadId);
        Assert.NotNull(restoredFirst);
        Assert.Equal(first.PiSessionId, restoredFirst.PiSessionId);
        Assert.Equal("Roadmap % review", restoredFirst.Title);
        Assert.Equal(2, restoredFirst.Revision);
        Assert.True(restoredFirst.IsArchived);
        Assert.False(restoredFirst.IsPinned);
        Assert.True(restoredPinned?.IsPinned);
        Assert.Equal(1, restoredPinned?.Revision);
        Assert.True(archived.WasUpdated);
    }

    [Fact]
    public async Task InterruptedReceiptBecomesDispatchUncertainOnRestart()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectPath = temporaryDirectory.CreateDirectory("project");
        var database = new HostDatabase(options);
        var environment = await database.InitializeAsync();
        var projects = new ProjectService(database);
        var project = await projects.AddAsync(new AddProjectRequest(projectPath));
        var thread = await projects.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        var now = DateTimeOffset.UtcNow;
        var receipt = new CommandReceipt(
            environment.EnvironmentId,
            ClientId.New(),
            CommandId.New(),
            thread.ThreadId,
            CommandReceiptState.Received,
            null,
            now,
            now);
        var acquired = await database.AcquireReceiptAsync(receipt, "HASH-1");
        await database.UpdateReceiptStateAsync(
            receipt.ClientId,
            receipt.CommandId,
            CommandReceiptState.Dispatching);

        var restarted = new HostDatabase(options);
        await restarted.InitializeAsync();
        var recovered = await restarted.GetReceiptAsync(receipt.ClientId, receipt.CommandId);
        var duplicate = await restarted.AcquireReceiptAsync(receipt, "HASH-2");

        Assert.True(acquired.WasCreated);
        Assert.NotNull(recovered);
        Assert.Equal(CommandReceiptState.DispatchUncertain, recovered.Receipt.State);
        Assert.False(duplicate.WasCreated);
        Assert.Equal("HASH-1", duplicate.StoredReceipt.BodyHash);
    }

    [Fact]
    public async Task TerminalReceiptCannotBeDowngradedByLateAcceptance()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectPath = temporaryDirectory.CreateDirectory("project");
        var database = new HostDatabase(options);
        var environment = await database.InitializeAsync();
        var projects = new ProjectService(database);
        var project = await projects.AddAsync(new AddProjectRequest(projectPath));
        var thread = await projects.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        var now = DateTimeOffset.UtcNow;
        var receipt = new CommandReceipt(
            environment.EnvironmentId,
            ClientId.New(),
            CommandId.New(),
            thread.ThreadId,
            CommandReceiptState.Received,
            null,
            now,
            now);
        await database.AcquireReceiptAsync(receipt, "HASH");

        await database.UpdateReceiptStateAsync(
            receipt.ClientId,
            receipt.CommandId,
            CommandReceiptState.Completed);
        var lateAcceptance = await database.UpdateReceiptStateAsync(
            receipt.ClientId,
            receipt.CommandId,
            CommandReceiptState.Accepted);

        Assert.Equal(CommandReceiptState.Completed, lateAcceptance.State);
    }

    [Fact]
    public async Task DraftIdentityRevisionAndTextSurviveRestart()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectPath = temporaryDirectory.CreateDirectory("project");
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var projects = new ProjectService(database);
        var project = await projects.AddAsync(new AddProjectRequest(projectPath));
        var thread = await projects.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        var initial = await database.GetOrCreateThreadDraftAsync(thread.ThreadId);

        var updated = await database.UpdateThreadDraftAsync(
            thread.ThreadId,
            initial.DraftId,
            initial.Revision,
            "persist this draft");
        var stale = await database.UpdateThreadDraftAsync(
            thread.ThreadId,
            initial.DraftId,
            initial.Revision,
            "stale overwrite");
        var restarted = new HostDatabase(options);
        await restarted.InitializeAsync();
        var restored = await restarted.GetOrCreateThreadDraftAsync(thread.ThreadId);

        Assert.True(updated.WasUpdated);
        Assert.NotNull(updated.Draft);
        Assert.Equal(1, updated.Draft.Revision);
        Assert.False(stale.WasUpdated);
        Assert.Equal("persist this draft", stale.Draft?.Text);
        Assert.Equal(initial.DraftId, restored.DraftId);
        Assert.Equal("persist this draft", restored.Text);
        Assert.Equal(1, restored.Revision);
    }

    [Fact]
    public async Task ExistingPreDraftDatabaseAddsDraftStorageWithoutChangingIdentity()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        Directory.CreateDirectory(options.CanonicalDataRoot);
        var environmentId = EnvironmentId.Parse("existing-environment");
        var projectId = ProjectId.Parse("existing-project");
        var threadId = ThreadId.Parse("existing-thread");
        await using (var connection = new SqliteConnection(
                         new SqliteConnectionStringBuilder
                         {
                             DataSource = options.DatabasePath,
                             Pooling = false,
                         }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Environment (
                    EnvironmentId TEXT PRIMARY KEY NOT NULL,
                    Name TEXT NOT NULL,
                    CreatedUtc TEXT NOT NULL
                );
                CREATE TABLE Projects (
                    ProjectId TEXT PRIMARY KEY NOT NULL,
                    CanonicalPath TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    DisplayName TEXT NOT NULL,
                    CreatedUtc TEXT NOT NULL
                );
                CREATE TABLE Threads (
                    ThreadId TEXT PRIMARY KEY NOT NULL,
                    ProjectId TEXT NOT NULL,
                    PiSessionId TEXT NOT NULL,
                    PiSessionFile TEXT NULL,
                    Title TEXT NOT NULL,
                    CreatedUtc TEXT NOT NULL,
                    UpdatedUtc TEXT NOT NULL,
                    FOREIGN KEY (ProjectId) REFERENCES Projects(ProjectId) ON DELETE CASCADE
                );
                CREATE TABLE CommandReceipts (
                    ClientId TEXT NOT NULL,
                    CommandId TEXT NOT NULL,
                    EnvironmentId TEXT NOT NULL,
                    ThreadId TEXT NOT NULL,
                    BodyHash TEXT NOT NULL,
                    State TEXT NOT NULL,
                    ErrorCode TEXT NULL,
                    CreatedUtc TEXT NOT NULL,
                    UpdatedUtc TEXT NOT NULL,
                    PRIMARY KEY (ClientId, CommandId)
                );
                INSERT INTO Environment VALUES ('existing-environment', 'Existing', '2026-09-01T00:00:00Z');
                INSERT INTO Projects VALUES ('existing-project', 'C:\existing', 'Existing', '2026-09-01T00:00:00Z');
                INSERT INTO Threads VALUES (
                    'existing-thread',
                    'existing-project',
                    'existing-session',
                    NULL,
                    'Existing thread',
                    '2026-09-01T00:00:00Z',
                    '2026-09-01T00:00:00Z'
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var database = new HostDatabase(options);
        var environment = await database.InitializeAsync();
        var draft = await database.GetOrCreateThreadDraftAsync(threadId);

        Assert.Equal(environmentId, environment.EnvironmentId);
        Assert.Equal(threadId, draft.ThreadId);
        Assert.Equal(projectId, (await database.GetThreadAsync(threadId))?.ProjectId);
        Assert.Equal(0, (await database.GetThreadAsync(threadId))?.Revision);
        Assert.False((await database.GetThreadAsync(threadId))?.IsArchived);
        Assert.False((await database.GetThreadAsync(threadId))?.IsPinned);
        Assert.Empty(draft.Text);
        Assert.Equal(0, draft.Revision);
    }

    [Fact]
    public async Task DraftAttachmentsAdvanceRevisionPersistAndRemoveAtomically()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var database = new HostDatabase(options);
        var environment = await database.InitializeAsync();
        var projects = new ProjectService(database);
        var project = await projects.AddAsync(new AddProjectRequest(temporaryDirectory.CreateDirectory("project")));
        var thread = await projects.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        var initial = await database.GetOrCreateThreadDraftAsync(thread.ThreadId);
        var attachment = new DraftAttachment(
            environment.EnvironmentId,
            thread.ThreadId,
            initial.DraftId,
            AttachmentId.New(),
            "notes.txt",
            "text/plain",
            5,
            new string('A', 64),
            Path.Combine(options.AttachmentRoot, "owned", "notes.txt"),
            DateTimeOffset.UtcNow);

        var added = await database.AddDraftAttachmentAsync(
            thread.ThreadId,
            initial.DraftId,
            initial.Revision,
            attachment,
            options.MaximumAttachmentsPerDraft);
        var stale = await database.AddDraftAttachmentAsync(
            thread.ThreadId,
            initial.DraftId,
            initial.Revision,
            attachment with { AttachmentId = AttachmentId.New() },
            options.MaximumAttachmentsPerDraft);
        var restarted = new HostDatabase(options);
        await restarted.InitializeAsync();
        var restored = await restarted.GetOrCreateThreadDraftAsync(thread.ThreadId);
        var removed = await restarted.RemoveDraftAttachmentAsync(
            thread.ThreadId,
            restored.DraftId,
            attachment.AttachmentId,
            restored.Revision);

        Assert.Equal(DraftAttachmentMutationState.Updated, added.State);
        Assert.Equal(1, added.Draft?.Revision);
        Assert.Equal(attachment.AttachmentId, Assert.Single(added.Draft!.Attachments).AttachmentId);
        Assert.Equal(DraftAttachmentMutationState.DraftConflict, stale.State);
        Assert.Equal(attachment, Assert.Single(restored.Attachments));
        Assert.Equal(DraftAttachmentMutationState.Updated, removed.State);
        Assert.Equal(2, removed.Draft?.Revision);
        Assert.Empty(removed.Draft!.Attachments);
        Assert.Equal(attachment.AttachmentId, removed.RemovedAttachment?.AttachmentId);
    }

    [Fact]
    public async Task DraftClearRequiresTheExactRevisionAndAttachmentSet()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var database = new HostDatabase(options);
        var environment = await database.InitializeAsync();
        var projects = new ProjectService(database);
        var project = await projects.AddAsync(new AddProjectRequest(temporaryDirectory.CreateDirectory("project")));
        var thread = await projects.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        var initial = await database.GetOrCreateThreadDraftAsync(thread.ThreadId);
        var withText = await database.UpdateThreadDraftAsync(
            thread.ThreadId,
            initial.DraftId,
            initial.Revision,
            "do not lose this");
        var attachment = new DraftAttachment(
            environment.EnvironmentId,
            thread.ThreadId,
            initial.DraftId,
            AttachmentId.New(),
            "notes.txt",
            "text/plain",
            5,
            new string('C', 64),
            Path.Combine(options.AttachmentRoot, "notes.txt"),
            DateTimeOffset.UtcNow);
        var added = await database.AddDraftAttachmentAsync(
            thread.ThreadId,
            initial.DraftId,
            withText.Draft!.Revision,
            attachment,
            options.MaximumAttachmentsPerDraft);

        var mismatch = await database.ClearThreadDraftAsync(
            thread.ThreadId,
            initial.DraftId,
            added.Draft!.Revision,
            [AttachmentId.New()]);
        var cleared = await database.ClearThreadDraftAsync(
            thread.ThreadId,
            initial.DraftId,
            added.Draft.Revision,
            [attachment.AttachmentId]);

        Assert.Equal(DraftAttachmentMutationState.DraftConflict, mismatch.State);
        Assert.Equal("do not lose this", mismatch.Draft?.Text);
        Assert.Equal(attachment.AttachmentId, Assert.Single(mismatch.Draft!.Attachments).AttachmentId);
        Assert.Equal(DraftAttachmentMutationState.Updated, cleared.State);
        Assert.Equal(string.Empty, cleared.Draft?.Text);
        Assert.Equal(added.Draft.Revision + 1, cleared.Draft?.Revision);
        Assert.Empty(cleared.Draft!.Attachments);
        Assert.Equal(attachment.AttachmentId, Assert.Single(cleared.RemovedAttachments).AttachmentId);
    }

    [Fact]
    public async Task AddingAfterAnEarlierAttachmentWasRemovedKeepsStableOrdering()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var database = new HostDatabase(options);
        var environment = await database.InitializeAsync();
        var projects = new ProjectService(database);
        var project = await projects.AddAsync(new AddProjectRequest(temporaryDirectory.CreateDirectory("project")));
        var thread = await projects.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        var draft = await database.GetOrCreateThreadDraftAsync(thread.ThreadId);
        DraftAttachment CreateAttachment(string name) => new(
            environment.EnvironmentId,
            thread.ThreadId,
            draft.DraftId,
            AttachmentId.New(),
            name,
            "text/plain",
            1,
            new string('B', 64),
            Path.Combine(options.AttachmentRoot, name),
            DateTimeOffset.UtcNow);
        var first = CreateAttachment("first.txt");
        var second = CreateAttachment("second.txt");
        var third = CreateAttachment("third.txt");

        var afterFirst = await database.AddDraftAttachmentAsync(
            thread.ThreadId, draft.DraftId, 0, first, options.MaximumAttachmentsPerDraft);
        var afterSecond = await database.AddDraftAttachmentAsync(
            thread.ThreadId, draft.DraftId, afterFirst.Draft!.Revision, second, options.MaximumAttachmentsPerDraft);
        var afterRemoval = await database.RemoveDraftAttachmentAsync(
            thread.ThreadId, draft.DraftId, first.AttachmentId, afterSecond.Draft!.Revision);
        var afterThird = await database.AddDraftAttachmentAsync(
            thread.ThreadId, draft.DraftId, afterRemoval.Draft!.Revision, third, options.MaximumAttachmentsPerDraft);

        Assert.Equal(DraftAttachmentMutationState.Updated, afterThird.State);
        Assert.Equal(
            [second.AttachmentId, third.AttachmentId],
            afterThird.Draft!.Attachments.Select(static item => item.AttachmentId));
    }
}
