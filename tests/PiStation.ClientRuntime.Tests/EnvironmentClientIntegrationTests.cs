using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using PiStation.Host;
using PiStation.Host.Hosting;
using PiStation.PiRpc.Discovery;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Receipts;

namespace PiStation.ClientRuntime.Tests;

public sealed class EnvironmentClientIntegrationTests
{
    [Fact]
    public async Task ClientRunsACompleteTurnAndResumesItsSubscriptionAfterReconnect()
    {
        using var temporaryDirectory = new ClientTestDirectory();
        var projectPath = temporaryDirectory.CreateDirectory("project");
        await using var host = await EmbeddedEnvironmentHost.StartAsync(temporaryDirectory.CreateHostOptions());
        await using var client = CreateClient(host);

        await client.ConnectAsync();
        Assert.Equal(EnvironmentConnectionState.Connected, client.ConnectionState);
        Assert.Equal("0.84.4", client.Descriptor?.PiVersion);

        var project = await client.AddProjectAsync(new AddProjectRequest(projectPath));
        var thread = await client.CreateThreadAsync(new CreateThreadRequest(project.ProjectId, "First contact"));
        await using var subscription = client.SubscribeThread(thread.ThreadId);
        var ready = await WaitForProjectionAsync(
            subscription.Store,
            projection => projection.RuntimeState == ThreadRuntimeState.Ready);

        await client.StartTurnAsync(thread.ThreadId, "Hello client", ready.ProjectionEpoch);
        var firstTurn = await WaitForProjectionAsync(
            subscription.Store,
            projection => projection.RuntimeState == ThreadRuntimeState.Ready && projection.Messages.Count == 2);
        Assert.Equal("Hello client", firstTurn.Messages[0].Text);
        Assert.Equal("Hello from Fake Pi 👽", firstTurn.Messages[1].Text);

        await client.DisconnectAsync();
        Assert.Equal(EnvironmentConnectionState.Disconnected, client.ConnectionState);
        await client.ConnectAsync();
        await client.StartTurnAsync(thread.ThreadId, "Second hello", firstTurn.ProjectionEpoch);
        var secondTurn = await WaitForProjectionAsync(
            subscription.Store,
            projection => projection.RuntimeState == ThreadRuntimeState.Ready && projection.Messages.Count == 4);

        Assert.Equal("Second hello", secondTurn.Messages[2].Text);
        Assert.Equal("Hello from Fake Pi 👽", secondTurn.Messages[3].Text);
        Assert.Equal(4, secondTurn.Messages.Select(message => message.MessageId).Distinct().Count());
    }

    [Fact]
    public async Task ClientSurfacesAnInvalidBearerAsAuthenticationRequired()
    {
        using var temporaryDirectory = new ClientTestDirectory();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(temporaryDirectory.CreateHostOptions());
        await using var client = new EnvironmentClient(new ClientRuntimeOptions
        {
            HubAddress = host.HubAddress,
            BearerCredential = "not-the-host-credential",
        });

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.ConnectAsync());
        Assert.Equal(EnvironmentConnectionState.AuthenticationRequired, client.ConnectionState);
    }

    [Fact]
    public async Task ClientListsPersistedProjectsAfterHostRestart()
    {
        using var temporaryDirectory = new ClientTestDirectory();
        var options = temporaryDirectory.CreateHostOptions();
        var projectPath = temporaryDirectory.CreateDirectory("persisted-project");
        await using (var firstHost = await EmbeddedEnvironmentHost.StartAsync(options))
        await using (var firstClient = CreateClient(firstHost))
        {
            await firstClient.ConnectAsync();
            await firstClient.AddProjectAsync(new AddProjectRequest(projectPath));
        }

        await using (var restartedHost = await EmbeddedEnvironmentHost.StartAsync(options))
        await using (var restartedClient = CreateClient(restartedHost))
        {
            var directProjects = await restartedHost.Environment.ListProjectsAsync();
            Assert.Equal("persisted-project", Assert.Single(directProjects).DisplayName);
            await restartedClient.ConnectAsync();
            var projects = await restartedClient.ListProjectsAsync();
            Assert.Equal("persisted-project", Assert.Single(projects).DisplayName);
        }
    }

    [Fact]
    public async Task ClientSearchesProjectFilesThroughTheAuthenticatedHost()
    {
        using var temporaryDirectory = new ClientTestDirectory();
        var projectPath = temporaryDirectory.CreateDirectory("search-project");
        Directory.CreateDirectory(Path.Combine(projectPath, "src"));
        await File.WriteAllTextAsync(Path.Combine(projectPath, "src", "SearchTarget.cs"), "class SearchTarget;");
        await File.WriteAllTextAsync(Path.Combine(projectPath, "README.md"), "read me");
        await using var host = await EmbeddedEnvironmentHost.StartAsync(temporaryDirectory.CreateHostOptions());
        await using var client = CreateClient(host);
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new AddProjectRequest(projectPath));

        var result = await client.SearchProjectFilesAsync(
            new SearchProjectFilesRequest(project.ProjectId, "target", 10));

        Assert.Contains("file.search", client.Descriptor?.Capabilities ?? []);
        Assert.Contains("file.read", client.Descriptor?.Capabilities ?? []);
        var match = Assert.Single(result.Matches);
        Assert.Equal("src/SearchTarget.cs", match.RelativePath);
        Assert.Equal("SearchTarget.cs", match.FileName);
        Assert.False(result.IsTruncated);

        var preview = await client.ReadProjectFileAsync(
            new ReadProjectFileRequest(project.ProjectId, match.RelativePath));
        Assert.Equal("class SearchTarget;", preview.Content);
        Assert.False(preview.IsBinary);
        Assert.False(preview.IsTruncated);

        var listing = await client.ListProjectEntriesAsync(new ListProjectEntriesRequest(project.ProjectId));
        Assert.Contains(listing.Entries, entry => entry.RelativePath == "src");
        var contentSearch = await client.SearchProjectContentsAsync(new SearchProjectContentsRequest(
            project.ProjectId,
            "SearchTarget"));
        Assert.Equal(1, Assert.Single(contentSearch.Matches).LineNumber);
        var saved = await client.SaveProjectFileAsync(new SaveProjectFileRequest(
            project.ProjectId,
            match.RelativePath,
            "class SearchTarget { }",
            preview.Revision));
        Assert.NotEqual(preview.Revision, saved.Revision);
        Assert.Equal("class SearchTarget { }", await File.ReadAllTextAsync(
            Path.Combine(projectPath, "src", "SearchTarget.cs")));
        Assert.Contains("file.content-search", client.Descriptor?.Capabilities ?? []);
        Assert.Contains("file.write", client.Descriptor?.Capabilities ?? []);
    }

    [Fact]
    public async Task ClientReadsGitChangesAndDiffsThroughTheAuthenticatedHost()
    {
        using var temporaryDirectory = new ClientTestDirectory();
        var projectPath = temporaryDirectory.CreateDirectory("git-project");
        RunGit(projectPath, "init", "--quiet", "--initial-branch=main");
        RunGit(projectPath, "config", "user.email", "pistation@example.invalid");
        RunGit(projectPath, "config", "user.name", "Pi Station Tests");
        await File.WriteAllTextAsync(Path.Combine(projectPath, "README.md"), "baseline\n");
        RunGit(projectPath, "add", "README.md");
        RunGit(projectPath, "commit", "--quiet", "-m", "baseline");
        await File.WriteAllTextAsync(Path.Combine(projectPath, "README.md"), "changed through client\n");
        await using var host = await EmbeddedEnvironmentHost.StartAsync(temporaryDirectory.CreateHostOptions());
        await using var client = CreateClient(host);
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new AddProjectRequest(projectPath));

        var changes = await client.GetProjectChangesAsync(new GetProjectChangesRequest(project.ProjectId));
        var diff = await client.GetProjectChangeDiffAsync(
            new GetProjectChangeDiffRequest(project.ProjectId, Assert.Single(changes.Changes).RelativePath));

        Assert.Contains("git.read", client.Descriptor?.Capabilities ?? []);
        Assert.Equal("main", changes.BranchName);
        Assert.Contains("+changed through client", diff.DiffContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientExecutesGitCommandsAndCreatesAThreadWorktreeThroughTheHub()
    {
        using var temporaryDirectory = new ClientTestDirectory();
        var projectPath = temporaryDirectory.CreateDirectory("git-workflow-project");
        await using var host = await EmbeddedEnvironmentHost.StartAsync(temporaryDirectory.CreateHostOptions());
        await using var client = CreateClient(host);
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new AddProjectRequest(projectPath));

        var initialized = await client.ExecuteWorkspaceGitCommandAsync(
            new WorkspaceTarget(project.ProjectId),
            new GitInitCommand());
        Assert.Equal(CommandReceiptState.Completed, initialized.Receipt.State);
        Assert.Equal(initialized, await client.GetWorkspaceGitCommandResultAsync(initialized.Receipt.CommandId));

        RunGit(projectPath, "config", "user.email", "pistation@example.invalid");
        RunGit(projectPath, "config", "user.name", "Pi Station Tests");
        await File.WriteAllTextAsync(Path.Combine(projectPath, "README.md"), "created through client\n");
        var committed = await client.ExecuteWorkspaceGitCommandAsync(
            new WorkspaceTarget(project.ProjectId),
            new GitRunActionCommand(GitActionKind.Commit));
        Assert.Equal(CommandReceiptState.Completed, committed.Receipt.State);

        var branch = await client.ExecuteWorkspaceGitCommandAsync(
            new WorkspaceTarget(project.ProjectId),
            new GitCreateBranchCommand("feature/client"));
        var refs = await client.ListGitRefsAsync(new ListGitRefsRequest(new WorkspaceTarget(project.ProjectId)));
        Assert.Equal(CommandReceiptState.Completed, branch.Receipt.State);
        Assert.Contains(refs.Refs, static item => item.Name == "feature/client");

        var thread = await client.CreateThreadAsync(new CreateThreadRequest(
            project.ProjectId,
            "Worktree thread",
            ThreadWorkspaceMode.Worktree,
            BaseBranch: "main",
            RunSetupScript: false));
        var status = await client.GetProjectChangesAsync(
            new GetProjectChangesRequest(project.ProjectId, ThreadId: thread.ThreadId));
        var worktrees = await client.ListGitWorktreesAsync(new ListGitWorktreesRequest(project.ProjectId));
        Assert.Equal(ThreadWorkspaceMode.Worktree, thread.WorkspaceMode);
        Assert.True(status.IsWorktree);
        Assert.Contains(worktrees.Worktrees, item =>
            string.Equals(item.Path, thread.WorktreePath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("git.write", client.Descriptor?.Capabilities ?? []);
        Assert.Contains("git.worktrees", client.Descriptor?.Capabilities ?? []);
    }

    [Fact]
    public async Task ClientReadsUpdatesAndClassifiesPiConfigurationFailures()
    {
        using var temporaryDirectory = new ClientTestDirectory();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(
            temporaryDirectory.CreateHostOptions());
        await using var client = CreateClient(host);
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new AddProjectRequest(
            temporaryDirectory.CreateDirectory("project")));
        var thread = await client.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));

        var initial = await client.GetThreadPiConfigurationAsync(thread.ThreadId);
        var updated = await client.UpdateThreadPiConfigurationAsync(
            thread.ThreadId,
            initial.Configuration.Revision,
            new PiModelSelection("fake", "fake-standard"),
            PiThinkingLevel.High,
            null);

        Assert.Equal(CommandReceiptState.Completed, updated.Receipt.State);
        Assert.Equal(1, updated.Snapshot?.Configuration.Revision);
        Assert.Equal(PiThinkingLevel.High, updated.Snapshot?.ActiveThinkingLevel);
        Assert.Equal(updated.Snapshot, client.PiConfigurations.GetCurrent(thread.ThreadId));
        Assert.Equal(
            updated.Receipt,
            await client.GetCommandReceiptAsync(updated.Receipt.CommandId));

        var unsupported = await Assert.ThrowsAsync<PiConfigurationUnsupportedException>(() =>
            client.UpdateThreadPiConfigurationAsync(
                thread.ThreadId,
                updated.Snapshot!.Configuration.Revision,
                updated.Snapshot.Configuration.Model,
                updated.Snapshot.Configuration.ThinkingLevel,
                "full-access"));
        Assert.Equal(ProtocolErrorCodes.PiConfigurationUnsupported, unsupported.ErrorCode);
        Assert.Equal(
            CommandReceiptState.Rejected,
            (await client.GetCommandReceiptAsync(unsupported.CommandId))?.State);

        var conflict = await Assert.ThrowsAsync<PiConfigurationConflictException>(() =>
            client.UpdateThreadPiConfigurationAsync(
                thread.ThreadId,
                initial.Configuration.Revision,
                new PiModelSelection("fake", "fake-fast"),
                PiThinkingLevel.Off,
                null));
        Assert.Equal(ProtocolErrorCodes.PiConfigurationConflict, conflict.ErrorCode);
        Assert.Equal(1, client.PiConfigurations.GetCurrent(thread.ThreadId)?.Configuration.Revision);
    }

    [Fact]
    public async Task ClientRefreshesTrackedPiConfigurationAfterReconnect()
    {
        using var temporaryDirectory = new ClientTestDirectory();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(
            temporaryDirectory.CreateHostOptions());
        await using var client = CreateClient(host);
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new AddProjectRequest(
            temporaryDirectory.CreateDirectory("project")));
        var thread = await client.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        var initial = await client.GetThreadPiConfigurationAsync(thread.ThreadId);
        await client.DisconnectAsync();

        await Assert.ThrowsAsync<EnvironmentConnectionException>(() =>
            client.GetThreadPiConfigurationAsync(thread.ThreadId));

        await using (var otherClient = CreateClient(host))
        {
            await otherClient.ConnectAsync();
            var current = await otherClient.GetThreadPiConfigurationAsync(thread.ThreadId);
            await otherClient.UpdateThreadPiConfigurationAsync(
                thread.ThreadId,
                current.Configuration.Revision,
                new PiModelSelection("fake", "fake-standard"),
                PiThinkingLevel.High,
                null);
        }

        await client.ConnectAsync();

        var refreshed = client.PiConfigurations.GetCurrent(thread.ThreadId);
        Assert.NotNull(refreshed);
        Assert.Equal(initial.Configuration.Revision + 1, refreshed.Configuration.Revision);
        Assert.Equal(PiThinkingLevel.High, refreshed.ActiveThinkingLevel);
        Assert.Equal(2, refreshed.Capabilities.Models.Count);
    }

    [Fact]
    public async Task ClientReportsAnUncertainPiConfigurationDispatchWithItsStableCommandId()
    {
        using var temporaryDirectory = new ClientTestDirectory();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(
            temporaryDirectory.CreateHostOptions());
        await using var client = CreateClient(host);
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new AddProjectRequest(
            temporaryDirectory.CreateDirectory("project")));
        var thread = await client.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        var initial = await client.GetThreadPiConfigurationAsync(thread.ThreadId);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var uncertain = await Assert.ThrowsAsync<CommandDispatchUncertainException>(() =>
            client.UpdateThreadPiConfigurationAsync(
                thread.ThreadId,
                initial.Configuration.Revision,
                initial.Configuration.Model,
                initial.Configuration.ThinkingLevel,
                initial.Configuration.RuntimeModeId,
                cancellation.Token));

        Assert.Equal(thread.ThreadId, uncertain.ThreadId);
        Assert.False(string.IsNullOrWhiteSpace(uncertain.CommandId.Value));
        Assert.Null(await client.GetCommandReceiptAsync(uncertain.CommandId));
        Assert.Equal(0, client.PiConfigurations.GetCurrent(thread.ThreadId)?.Configuration.Revision);
    }

    [Fact]
    public async Task ClientLifecycleUpdatesCacheSearchesAndClassifiesFailures()
    {
        using var temporaryDirectory = new ClientTestDirectory();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(
            temporaryDirectory.CreateHostOptions());
        await using var client = CreateClient(host);
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new AddProjectRequest(
            temporaryDirectory.CreateDirectory("project")));
        var thread = await client.CreateThreadAsync(new CreateThreadRequest(project.ProjectId, "Initial"));
        var other = await client.CreateThreadAsync(new CreateThreadRequest(project.ProjectId, "Other"));

        Assert.Equal(thread, client.ThreadMetadata.GetCurrent(thread.ThreadId));
        var renamed = await client.RenameThreadAsync(
            thread.ThreadId,
            thread.Revision,
            "Roadmap review");
        var pinned = await client.SetThreadPinnedAsync(
            thread.ThreadId,
            renamed.Thread!.Revision,
            true);
        var ordered = await client.ListThreadsAsync(project.ProjectId);

        Assert.Equal(CommandReceiptState.Completed, renamed.Receipt.State);
        Assert.Equal("Roadmap review", renamed.Thread.Title);
        Assert.Equal(1, renamed.Thread.Revision);
        Assert.True(pinned.Thread?.IsPinned);
        Assert.Equal(2, pinned.Thread?.Revision);
        Assert.Equal(thread.ThreadId, ordered[0].ThreadId);

        var archived = await client.SetThreadArchivedAsync(
            thread.ThreadId,
            pinned.Thread!.Revision,
            true);
        var activeOnly = await client.ListThreadsAsync(project.ProjectId);
        var defaultSearch = await client.SearchThreadsAsync(
            new SearchThreadsRequest(project.ProjectId, "roadmap"));
        var archivedSearch = await client.SearchThreadsAsync(
            new SearchThreadsRequest(project.ProjectId, "roadmap", IncludeArchived: true));

        Assert.True(archived.Thread?.IsArchived);
        Assert.Equal(other.ThreadId, Assert.Single(activeOnly).ThreadId);
        Assert.Empty(defaultSearch.Threads);
        Assert.Equal(thread.ThreadId, Assert.Single(archivedSearch.Threads).ThreadId);
        Assert.True(client.ThreadMetadata.GetCurrent(thread.ThreadId)?.IsArchived);

        var unarchived = await client.SetThreadArchivedAsync(
            thread.ThreadId,
            archived.Thread!.Revision,
            false);
        var unpinned = await client.SetThreadPinnedAsync(
            thread.ThreadId,
            unarchived.Thread!.Revision,
            false);
        Assert.False(unpinned.Thread?.IsArchived);
        Assert.False(unpinned.Thread?.IsPinned);
        Assert.Equal(5, unpinned.Thread?.Revision);

        var invalid = await Assert.ThrowsAsync<ThreadLifecycleInvalidException>(() =>
            client.RenameThreadAsync(thread.ThreadId, unpinned.Thread!.Revision, "  "));
        Assert.Equal(ProtocolErrorCodes.ThreadInvalid, invalid.ErrorCode);
        Assert.Equal(
            CommandReceiptState.Rejected,
            (await client.GetCommandReceiptAsync(invalid.CommandId))?.State);

        var conflict = await Assert.ThrowsAsync<ThreadLifecycleConflictException>(() =>
            client.RenameThreadAsync(thread.ThreadId, thread.Revision, "Stale overwrite"));
        Assert.Equal(ProtocolErrorCodes.ThreadConflict, conflict.ErrorCode);
        Assert.Equal(thread.ThreadId, conflict.ThreadId);
        Assert.Equal(5, client.ThreadMetadata.GetCurrent(thread.ThreadId)?.Revision);

        var invalidSearch = await Assert.ThrowsAsync<ThreadSearchException>(() =>
            client.SearchThreadsAsync(new SearchThreadsRequest(project.ProjectId, string.Empty, Limit: 0)));
        Assert.Equal(ProtocolErrorCodes.ThreadSearchInvalid, invalidSearch.ErrorCode);

        await client.DisconnectAsync();
        var disconnected = await Assert.ThrowsAsync<EnvironmentConnectionException>(() =>
            client.SetThreadPinnedAsync(thread.ThreadId, unpinned.Thread!.Revision, true));
        Assert.Equal(EnvironmentConnectionState.Disconnected, disconnected.ConnectionState);
    }

    [Fact]
    public async Task ClientRefreshesTrackedLifecycleMetadataAfterReconnect()
    {
        using var temporaryDirectory = new ClientTestDirectory();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(
            temporaryDirectory.CreateHostOptions());
        await using var client = CreateClient(host);
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new AddProjectRequest(
            temporaryDirectory.CreateDirectory("project")));
        var thread = await client.CreateThreadAsync(new CreateThreadRequest(project.ProjectId, "Initial"));
        await client.ListThreadsAsync(project.ProjectId);
        await client.DisconnectAsync();

        await using (var otherClient = CreateClient(host))
        {
            await otherClient.ConnectAsync();
            var renamed = await otherClient.RenameThreadAsync(thread.ThreadId, 0, "Changed elsewhere");
            var pinned = await otherClient.SetThreadPinnedAsync(
                thread.ThreadId,
                renamed.Thread!.Revision,
                true);
            await otherClient.SetThreadArchivedAsync(
                thread.ThreadId,
                pinned.Thread!.Revision,
                true);
        }

        Assert.Equal(0, client.ThreadMetadata.GetCurrent(thread.ThreadId)?.Revision);
        await client.ConnectAsync();

        var refreshed = client.ThreadMetadata.GetCurrent(thread.ThreadId);
        Assert.NotNull(refreshed);
        Assert.Equal("Changed elsewhere", refreshed.Title);
        Assert.Equal(3, refreshed.Revision);
        Assert.True(refreshed.IsPinned);
        Assert.True(refreshed.IsArchived);
        Assert.Empty(client.ThreadMetadata.GetProjectThreads(project.ProjectId));
        Assert.Equal(
            thread.ThreadId,
            Assert.Single(client.ThreadMetadata.GetProjectThreads(
                project.ProjectId,
                includeArchived: true)).ThreadId);
    }

    [Fact]
    public async Task ClientReportsAnUncertainLifecycleDispatchWithoutChangingCachedMetadata()
    {
        using var temporaryDirectory = new ClientTestDirectory();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(
            temporaryDirectory.CreateHostOptions());
        await using var client = CreateClient(host);
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new AddProjectRequest(
            temporaryDirectory.CreateDirectory("project")));
        var thread = await client.CreateThreadAsync(new CreateThreadRequest(project.ProjectId, "Initial"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var uncertain = await Assert.ThrowsAsync<CommandDispatchUncertainException>(() =>
            client.RenameThreadAsync(
                thread.ThreadId,
                thread.Revision,
                "Uncertain rename",
                cancellation.Token));

        Assert.Equal(thread.ThreadId, uncertain.ThreadId);
        Assert.False(string.IsNullOrWhiteSpace(uncertain.CommandId.Value));
        Assert.Null(await client.GetCommandReceiptAsync(uncertain.CommandId));
        Assert.Equal("Initial", client.ThreadMetadata.GetCurrent(thread.ThreadId)?.Title);
        Assert.Equal(0, client.ThreadMetadata.GetCurrent(thread.ThreadId)?.Revision);
    }

    [Fact]
    public async Task ClientSavesAndReloadsARevisionedThreadDraft()
    {
        using var temporaryDirectory = new ClientTestDirectory();
        var projectPath = temporaryDirectory.CreateDirectory("project");
        await using var host = await EmbeddedEnvironmentHost.StartAsync(temporaryDirectory.CreateHostOptions());
        await using var client = CreateClient(host);
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new AddProjectRequest(projectPath));
        var thread = await client.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        var initial = await client.GetThreadDraftAsync(thread.ThreadId);

        var result = await client.SaveThreadDraftAsync(
            thread.ThreadId,
            initial.DraftId,
            initial.Revision,
            "client draft");
        var restored = await client.GetThreadDraftAsync(thread.ThreadId);

        Assert.Equal(PiStation.Protocol.Receipts.CommandReceiptState.Completed, result.Receipt.State);
        Assert.NotNull(result.Draft);
        Assert.Equal(restored.DraftId, result.Draft.DraftId);
        Assert.Equal(restored.Text, result.Draft.Text);
        Assert.Equal(restored.Revision, result.Draft.Revision);
        Assert.Equal(restored.Attachments, result.Draft.Attachments);
        Assert.Equal(initial.DraftId, restored.DraftId);
        Assert.Equal("client draft", restored.Text);
        Assert.Equal(1, restored.Revision);
    }

    [Fact]
    public async Task ClientUploadsPersistsAndRemovesAHostOwnedDraftAttachment()
    {
        using var temporaryDirectory = new ClientTestDirectory();
        var options = temporaryDirectory.CreateHostOptions();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(options);
        await using var client = CreateClient(host);
        await client.ConnectAsync();
        Assert.Contains("attachment.upload", client.Descriptor?.Capabilities ?? []);
        var project = await client.AddProjectAsync(new AddProjectRequest(
            temporaryDirectory.CreateDirectory("project")));
        var thread = await client.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        var initial = await client.GetThreadDraftAsync(thread.ThreadId);
        var bytes = Encoding.UTF8.GetBytes("durable attachment");
        using var content = new MemoryStream(bytes);

        var uploaded = await client.UploadDraftAttachmentAsync(
            thread.ThreadId,
            initial.DraftId,
            initial.Revision,
            "notes.txt",
            "text/plain",
            content,
            bytes.Length);
        var attachment = Assert.Single(uploaded.Draft!.Attachments);
        var restored = await client.GetThreadDraftAsync(thread.ThreadId);

        Assert.Equal(PiStation.Protocol.Receipts.CommandReceiptState.Completed, uploaded.Receipt.State);
        Assert.Equal(1, uploaded.Draft.Revision);
        Assert.Equal(attachment, Assert.Single(restored.Attachments));
        Assert.True(File.Exists(attachment.ServerPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(attachment.ServerPath));
        Assert.StartsWith(
            Path.GetFullPath(options.AttachmentRoot) + Path.DirectorySeparatorChar,
            Path.GetFullPath(attachment.ServerPath),
            StringComparison.OrdinalIgnoreCase);

        var removed = await client.RemoveDraftAttachmentAsync(
            thread.ThreadId,
            restored.DraftId,
            attachment.AttachmentId,
            restored.Revision);

        Assert.Equal(PiStation.Protocol.Receipts.CommandReceiptState.Completed, removed.Receipt.State);
        Assert.Equal(2, removed.Draft?.Revision);
        Assert.Empty(removed.Draft!.Attachments);
        Assert.False(File.Exists(attachment.ServerPath));
    }

    [Fact]
    public async Task ClientSendsDraftAttachmentsClearsOnAcceptanceAndHydratesAPathFreeTranscript()
    {
        using var temporaryDirectory = new ClientTestDirectory();
        var options = temporaryDirectory.CreateHostOptions();
        var projectPath = temporaryDirectory.CreateDirectory("project");
        ThreadDescriptor thread;
        string notesPath;
        string imagePath;

        await using (var host = await EmbeddedEnvironmentHost.StartAsync(options))
        await using (var client = CreateClient(host))
        {
            await client.ConnectAsync();
            var project = await client.AddProjectAsync(new AddProjectRequest(projectPath));
            thread = await client.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
            await using var subscription = client.SubscribeThread(thread.ThreadId);
            var ready = await WaitForProjectionAsync(
                subscription.Store,
                projection => projection.RuntimeState == ThreadRuntimeState.Ready);
            var initial = await client.GetThreadDraftAsync(thread.ThreadId);
            var saved = await client.SaveThreadDraftAsync(
                thread.ThreadId,
                initial.DraftId,
                initial.Revision,
                "Inspect attachments",
                [new ComposerContext("saved-source", "file", "notes source", "original quoted text", thread.ThreadId,
                    RelativePath: "notes.txt", StartLine: 2, EndLine: 2)]);
            var notesBytes = Encoding.UTF8.GetBytes("durable notes");
            await using var notesContent = new MemoryStream(notesBytes);
            var withNotes = await client.UploadDraftAttachmentAsync(
                thread.ThreadId,
                saved.Draft!.DraftId,
                saved.Draft.Revision,
                "notes.txt",
                "text/plain",
                notesContent,
                notesBytes.Length);
            var imageBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            await using var imageContent = new MemoryStream(imageBytes);
            var withImage = await client.UploadDraftAttachmentAsync(
                thread.ThreadId,
                withNotes.Draft!.DraftId,
                withNotes.Draft.Revision,
                "pixel.png",
                "image/png",
                imageContent,
                imageBytes.Length);
            var sentDraft = withImage.Draft!;
            var attachmentIds = sentDraft.Attachments
                .Select(static attachment => attachment.AttachmentId)
                .ToArray();
            notesPath = sentDraft.Attachments[0].ServerPath;
            imagePath = sentDraft.Attachments[1].ServerPath;

            var start = await client.StartTurnAsync(
                thread.ThreadId,
                sentDraft.Text,
                ready.ProjectionEpoch,
                draftId: sentDraft.DraftId,
                draftRevision: sentDraft.Revision,
                attachmentIds: attachmentIds);
            Assert.Contains(start.State, new[] { CommandReceiptState.Accepted, CommandReceiptState.Completed });

            var cleared = await client.ClearThreadDraftAsync(
                thread.ThreadId,
                sentDraft.DraftId,
                sentDraft.Revision,
                attachmentIds);
            Assert.Equal(CommandReceiptState.Completed, cleared.Receipt.State);
            Assert.Equal(sentDraft.Revision + 1, cleared.Draft?.Revision);
            Assert.Equal(string.Empty, cleared.Draft?.Text);
            Assert.Empty(cleared.Draft!.Attachments);
            Assert.True(File.Exists(notesPath));
            Assert.True(File.Exists(imagePath));

            var settled = await WaitForProjectionAsync(
                subscription.Store,
                projection => projection.RuntimeState == ThreadRuntimeState.Ready && projection.Messages.Count == 2);
            Assert.Equal(
                "Inspect attachments\n\n[Attached: notes.txt, pixel.png]",
                settled.Messages[0].Text);

            var commandLogPath = Directory.EnumerateFiles(
                    options.SessionRoot,
                    "command-log.jsonl",
                    SearchOption.AllDirectories)
                .Single();
            var promptRecord = File.ReadLines(commandLogPath)
                .Select(static line => JsonNode.Parse(line) as JsonObject)
                .Single(static record => record?["command"]?.GetValue<string>() == "prompt")!;
            var rpcMessage = promptRecord["message"]!.GetValue<string>();
            var images = Assert.IsType<JsonArray>(promptRecord["images"]);
            var manifest = ReadAttachmentManifest(rpcMessage);
            var manifestAttachments = Assert.IsType<JsonArray>(manifest["attachments"]);
            Assert.Equal(notesPath, manifestAttachments[0]?["path"]?.GetValue<string>());
            Assert.Equal(imagePath, manifestAttachments[1]?["path"]?.GetValue<string>());
            Assert.Single(images);
            Assert.Equal(
                Convert.ToBase64String(imageBytes),
                images[0]?["data"]?.GetValue<string>());
        }

        await using (var restartedHost = await EmbeddedEnvironmentHost.StartAsync(options))
        await using (var restartedClient = CreateClient(restartedHost))
        {
            await restartedClient.ConnectAsync();
            await using var subscription = restartedClient.SubscribeThread(thread.ThreadId);
            var hydrated = await WaitForProjectionAsync(
                subscription.Store,
                projection => projection.RuntimeState == ThreadRuntimeState.Ready && projection.Messages.Count == 2);
            var userText = hydrated.Messages[0].Text;
            Assert.Equal("Inspect attachments\n\n[Attached: notes.txt, pixel.png]", userText);
            Assert.DoesNotContain("<pistation_attachments>", userText, StringComparison.Ordinal);
            Assert.DoesNotContain(notesPath, userText, StringComparison.Ordinal);
            Assert.DoesNotContain(imagePath, userText, StringComparison.Ordinal);
            Assert.DoesNotContain("<pistation_message_ref>", userText, StringComparison.Ordinal);
            var content = Assert.IsType<SentMessageContent>(hydrated.Messages[0].Content);
            Assert.Equal(2, content.Attachments.Count);
            foreach (var attachment in content.Attachments) await SentAttachmentAccess.VerifyAsync(attachment);
            var citation = Assert.Single(content.Citations);
            Assert.Equal("original quoted text", citation.Text);
            Assert.Equal("notes.txt", citation.RelativePath);
            Assert.Equal(2, citation.StartLine);
            Assert.Empty((await restartedClient.GetThreadDraftAsync(thread.ThreadId)).Attachments);
            await restartedClient.DeleteThreadAsync(new DeleteThreadRequest(thread.ThreadId));
            Assert.False(File.Exists(notesPath));
            Assert.False(File.Exists(imagePath));
        }
    }

    [Fact]
    public async Task CitationOnlyPromptRetainsSourcesWithoutAnAttachment()
    {
        using var directory = new ClientTestDirectory();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions());
        await using var client = CreateClient(host);
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new(directory.CreateDirectory("project")));
        var thread = await client.CreateThreadAsync(new(project.ProjectId));
        await using var subscription = client.SubscribeThread(thread.ThreadId);
        var ready = await WaitForProjectionAsync(subscription.Store, p => p.RuntimeState == ThreadRuntimeState.Ready);
        var draft = await client.GetThreadDraftAsync(thread.ThreadId);
        var citation = new ComposerContext("citation", "file", "source.cs", "quoted source", thread.ThreadId,
            RelativePath: "source.cs", StartLine: 3, EndLine: 4);
        var saved = (await client.SaveThreadDraftAsync(thread.ThreadId, draft.DraftId, draft.Revision, "Use source", [citation])).Draft!;
        var receipt = await client.StartTurnAsync(thread.ThreadId, saved.Text, ready.ProjectionEpoch,
            draftId: saved.DraftId, draftRevision: saved.Revision);
        Assert.Contains(receipt.State, new[] { CommandReceiptState.Accepted, CommandReceiptState.Completed });
        await client.ClearThreadDraftAsync(thread.ThreadId, saved.DraftId, saved.Revision, []);
        var settled = await WaitForProjectionAsync(subscription.Store, p => p.RuntimeState == ThreadRuntimeState.Ready && p.Messages.Count == 2);
        var content = Assert.IsType<SentMessageContent>(settled.Messages[0].Content);
        Assert.Empty(content.Attachments);
        Assert.Equal(citation, Assert.Single(content.Citations));
        await client.RestartThreadAsync(thread.ThreadId, settled.ProjectionEpoch);
        var hydrated = await WaitForProjectionAsync(subscription.Store,
            p => p.RuntimeState == ThreadRuntimeState.Ready && p.ProjectionEpoch != settled.ProjectionEpoch && p.Messages.Count == 2);
        Assert.Equal(citation, Assert.Single(hydrated.Messages[0].Content!.Citations));
        Assert.Equal("Use source", hydrated.Messages[0].Text);
    }

    [Fact]
    public async Task ClientRestartsACrashedRuntimeAndReconcilesItsProjection()
    {
        using var temporaryDirectory = new ClientTestDirectory();
        var projectPath = temporaryDirectory.CreateDirectory("project");
        await using var host = await EmbeddedEnvironmentHost.StartAsync(
            temporaryDirectory.CreateHostOptions("crash-once"));
        await using var client = CreateClient(host);
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new AddProjectRequest(projectPath));
        var thread = await client.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        await using var subscription = client.SubscribeThread(thread.ThreadId);
        var initial = await WaitForProjectionAsync(
            subscription.Store,
            projection => projection.RuntimeState == ThreadRuntimeState.Ready);

        await client.StartTurnAsync(thread.ThreadId, "crash once", initial.ProjectionEpoch);
        var crashed = await WaitForProjectionAsync(
            subscription.Store,
            projection => projection.RuntimeState == ThreadRuntimeState.Crashed);
        Assert.Equal(ProtocolErrorCodes.PiRuntimeCrashed, crashed.LastError?.Code);

        var restart = await client.RestartThreadAsync(thread.ThreadId, crashed.ProjectionEpoch);
        Assert.Equal(PiStation.Protocol.Receipts.CommandReceiptState.Completed, restart.State);
        var recovered = await WaitForProjectionAsync(
            subscription.Store,
            projection =>
                projection.RuntimeState == ThreadRuntimeState.Ready &&
                projection.ProjectionEpoch != crashed.ProjectionEpoch);
        Assert.Empty(recovered.Messages);

        await client.StartTurnAsync(thread.ThreadId, "after restart", recovered.ProjectionEpoch);
        var completed = await WaitForProjectionAsync(
            subscription.Store,
            projection => projection.RuntimeState == ThreadRuntimeState.Ready && projection.Messages.Count == 2);
        Assert.Equal("Hello from Fake Pi 👽", completed.Messages[^1].Text);
    }

    [Fact]
    public async Task ClientOperatesAndResumesAHostOwnedTerminalSession()
    {
        using var temporaryDirectory = new ClientTestDirectory();
        var projectPath = temporaryDirectory.CreateDirectory("terminal-project");
        await using var host = await EmbeddedEnvironmentHost.StartAsync(temporaryDirectory.CreateHostOptions());
        await using var client = CreateClient(host);
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new AddProjectRequest(projectPath));
        var terminal = await client.StartTerminalSessionAsync(
            new StartTerminalSessionRequest(project.ProjectId, TerminalShellKind.CommandPrompt, 88, 22));
        await using var subscription = client.SubscribeTerminal(terminal.TerminalSessionId);

        await WaitForTerminalAsync(
            subscription.Store,
            (_, _) => subscription.Store.Descriptor?.State == TerminalSessionState.Running);
        await client.WriteTerminalInputAsync(
            new WriteTerminalInputRequest(terminal.TerminalSessionId, "echo client-terminal-^one-executed\r\n"));
        await WaitForTerminalAsync(
            subscription.Store,
            (_, output) => output.Contains("client-terminal-one-executed", StringComparison.OrdinalIgnoreCase));

        await client.DisconnectAsync();
        await client.ConnectAsync();
        await client.WriteTerminalInputAsync(
            new WriteTerminalInputRequest(terminal.TerminalSessionId, "echo client-terminal-^two-executed\r\n"));
        var resumed = await WaitForTerminalAsync(
            subscription.Store,
            (_, output) => output.Contains("client-terminal-two-executed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(terminal.TerminalSessionId, resumed.Descriptor.TerminalSessionId);

        var resized = await client.ResizeTerminalSessionAsync(
            new ResizeTerminalSessionRequest(terminal.TerminalSessionId, 104, 31));
        Assert.Equal((104, 31), (resized.Columns, resized.Rows));
        var stopped = await client.StopTerminalSessionAsync(
            new StopTerminalSessionRequest(terminal.TerminalSessionId));
        Assert.Equal(TerminalSessionState.Exited, stopped.State);
        await client.CloseTerminalSessionAsync(new CloseTerminalSessionRequest(terminal.TerminalSessionId));
        Assert.Empty(await client.ListTerminalSessionsAsync(project.ProjectId));
    }

    private static EnvironmentClient CreateClient(EmbeddedEnvironmentHost host) => new(new ClientRuntimeOptions
    {
        HubAddress = host.HubAddress,
        BearerCredential = host.BearerCredential,
    });

    private static async Task<ThreadProjection> WaitForProjectionAsync(
        ProjectionStore store,
        Func<ThreadProjection, bool> predicate)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var projection = store.Current;
            if (projection is not null && predicate(projection))
            {
                return projection;
            }

            await Task.Delay(25, cancellation.Token);
        }
    }

    private static async Task<(TerminalSessionDescriptor Descriptor, string Output)> WaitForTerminalAsync(
        TerminalStore store,
        Func<TerminalSessionDescriptor, string, bool> predicate)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var descriptor = store.Descriptor;
            var output = store.Output;
            if (descriptor is not null && predicate(descriptor, output))
            {
                return (descriptor, output);
            }

            await Task.Delay(25, cancellation.Token);
        }
    }

    private static JsonObject ReadAttachmentManifest(string message)
    {
        const string startMarker = "\n\n<pistation_attachments>\n";
        const string endMarker = "\n</pistation_attachments>";
        var start = message.LastIndexOf(startMarker, StringComparison.Ordinal) + startMarker.Length;
        var length = message.Length - start - endMarker.Length;
        return Assert.IsType<JsonObject>(JsonNode.Parse(message.Substring(start, length)));
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Git did not start.");
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, standardError);
    }
}

internal sealed class ClientTestDirectory : IDisposable
{
    private static readonly string TestRoot = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PiStationDesktop.ClientRuntimeTests"));

    public ClientTestDirectory()
    {
        Path = System.IO.Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateDirectory(string name)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public HostOptions CreateHostOptions(string scenario = "normal") => new()
    {
        ApplicationDataRoot = CreateDirectory("data"),
        EnvironmentName = "Client Test Station",
        PiInstallation = new PiInstallation(
            PiInstallationKind.NativeExecutable,
            FindFakePiExecutable(),
            [],
            new SemanticVersion(0, 84, 4),
            null,
            null,
            "test"),
        AdditionalPiArguments = ["--fake-pi-scenario", scenario],
    };

    public void Dispose()
    {
        var fullPath = System.IO.Path.GetFullPath(Path);
        var requiredPrefix = TestRoot + System.IO.Path.DirectorySeparatorChar;
        if (Directory.Exists(fullPath) &&
            fullPath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            ClearReadOnlyAttributes(fullPath);
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    private static string FindFakePiExecutable()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (!File.Exists(System.IO.Path.Combine(directory.FullName, "PiStationDesktop.slnx")))
            {
                continue;
            }

            var configuration = AppContext.BaseDirectory.Contains(
                $"{System.IO.Path.DirectorySeparatorChar}Release{System.IO.Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase)
                ? "Release"
                : "Debug";
            var executable = System.IO.Path.Combine(
                directory.FullName,
                "tests",
                "PiStation.FakePi",
                "bin",
                configuration,
                "net10.0",
                "PiStation.FakePi.exe");
            return File.Exists(executable)
                ? executable
                : throw new FileNotFoundException("The FakePi test executable was not built.", executable);
        }

        throw new DirectoryNotFoundException("Could not locate the solution root.");
    }
}
