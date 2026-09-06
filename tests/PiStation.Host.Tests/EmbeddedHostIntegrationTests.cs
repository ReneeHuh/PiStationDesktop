using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using PiStation.Host.Hosting;
using PiStation.Protocol;
using PiStation.Protocol.Commands;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Serialization;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Tests;

public sealed class EmbeddedHostIntegrationTests
{
    [Fact]
    public async Task LoopbackHubRejectsMissingBearerCredential()
    {
        using var temporaryDirectory = new HostTestDirectory();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(temporaryDirectory.CreateOptions());
        await using var connection = HostTestConnection.Create(host, authenticated: false);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => connection.StartAsync());
    }

    [Fact]
    public async Task AttachmentEndpointAuthenticatesStreamsAndReplaysIdempotently()
    {
        using var temporaryDirectory = new HostTestDirectory();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(temporaryDirectory.CreateOptions());
        var descriptor = host.Environment.GetDescriptor();
        var project = await host.Environment.AddProjectAsync(new AddProjectRequest(
            temporaryDirectory.CreateDirectory("attachment-project")));
        var thread = await host.Environment.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        var draft = await host.Environment.GetThreadDraftAsync(thread.ThreadId);
        var clientId = ClientId.New();
        var commandId = CommandId.New();
        var attachmentId = AttachmentId.New();
        var bytes = "endpoint attachment"u8.ToArray();
        using var httpClient = new HttpClient();

        using (var unauthorizedRequest = CreateAttachmentRequest(
                   host,
                   descriptor.EnvironmentId,
                   clientId,
                   commandId,
                   thread.ThreadId,
                   draft,
                   attachmentId,
                   bytes,
                   authenticated: false))
        using (var unauthorized = await httpClient.SendAsync(unauthorizedRequest))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        }

        DraftAttachmentUploadResult first;
        using (var request = CreateAttachmentRequest(
                   host,
                   descriptor.EnvironmentId,
                   clientId,
                   commandId,
                   thread.ThreadId,
                   draft,
                   attachmentId,
                   bytes,
                   authenticated: true))
        using (var response = await httpClient.SendAsync(request))
        {
            response.EnsureSuccessStatusCode();
            await using var responseBody = await response.Content.ReadAsStreamAsync();
            first = (await JsonSerializer.DeserializeAsync(
                responseBody,
                ProtocolJsonContext.Default.DraftAttachmentUploadResult))!;
        }

        DraftAttachmentUploadResult replay;
        using (var request = CreateAttachmentRequest(
                   host,
                   descriptor.EnvironmentId,
                   clientId,
                   commandId,
                   thread.ThreadId,
                   draft,
                   attachmentId,
                   bytes,
                   authenticated: true))
        using (var response = await httpClient.SendAsync(request))
        {
            response.EnsureSuccessStatusCode();
            await using var responseBody = await response.Content.ReadAsStreamAsync();
            replay = (await JsonSerializer.DeserializeAsync(
                responseBody,
                ProtocolJsonContext.Default.DraftAttachmentUploadResult))!;
        }

        var stored = Assert.Single(first.Draft!.Attachments);
        Assert.Equal(CommandReceiptState.Completed, first.Receipt.State);
        Assert.Equal(first.Receipt, replay.Receipt);
        Assert.Equal(1, replay.Draft?.Revision);
        Assert.Equal(attachmentId, Assert.Single(replay.Draft!.Attachments).AttachmentId);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(stored.ServerPath));

        var removal = await host.Environment.ExecuteThreadCommandAsync(new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            descriptor.EnvironmentId,
            clientId,
            CommandId.New(),
            thread.ThreadId,
            null,
            null,
            new ThreadRemoveDraftAttachmentCommand(draft.DraftId, attachmentId, 1)));
        Assert.Equal(CommandReceiptState.Completed, removal.State);
        Assert.False(File.Exists(stored.ServerPath));

        using (var request = CreateAttachmentRequest(
                   host,
                   descriptor.EnvironmentId,
                   clientId,
                   commandId,
                   thread.ThreadId,
                   draft,
                   attachmentId,
                   bytes,
                   authenticated: true))
        using (var response = await httpClient.SendAsync(request))
        {
            response.EnsureSuccessStatusCode();
            await using var responseBody = await response.Content.ReadAsStreamAsync();
            var historicalReplay = (await JsonSerializer.DeserializeAsync(
                responseBody,
                ProtocolJsonContext.Default.DraftAttachmentUploadResult))!;
            Assert.Equal(CommandReceiptState.Completed, historicalReplay.Receipt.State);
            Assert.Null(historicalReplay.Attachment);
            Assert.Empty(historicalReplay.Draft!.Attachments);
        }

        Assert.False(File.Exists(stored.ServerPath));
    }

    [Fact]
    public async Task ComposerDiscoveryAutomaticTitlesSettlementStashesAndCompactionWorkTogether()
    {
        using var temporaryDirectory = new HostTestDirectory();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(temporaryDirectory.CreateOptions());
        var descriptor = host.Environment.GetDescriptor();
        var project = await host.Environment.AddProjectAsync(new AddProjectRequest(
            temporaryDirectory.CreateDirectory("composer-power-project")));
        var thread = await host.Environment.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var discovery = await host.Environment.GetComposerDiscoveryAsync(thread.ThreadId, cancellation.Token);
        var stash = await host.Environment.SavePromptStashAsync(new SavePromptStashRequest(
            project.ProjectId,
            thread.ThreadId,
            "Keep this prompt for later"), cancellation.Token);
        Assert.Contains(discovery.Commands, static command => command.Name == "compact" && command.Source == ComposerCommandSource.BuiltIn);
        Assert.Contains(discovery.Commands, static command => command.Name == "skill:fake-skill" && command.Source == ComposerCommandSource.Skill && command.SourceInfo?.Scope == "user");
        Assert.Equal(stash.StashId, Assert.Single(await host.Environment.ListPromptStashesAsync(project.ProjectId, cancellation.Token)).StashId);

        await using (var stream = host.Environment.SubscribeThreadAsync(thread.ThreadId, null, cancellation.Token)
                         .GetAsyncEnumerator(cancellation.Token))
        {
            Assert.True(await stream.MoveNextAsync());
            var snapshot = Assert.IsType<ThreadSnapshotEnvelope>(stream.Current);
            var receipt = await host.Environment.ExecuteThreadCommandAsync(new ExecuteThreadCommandRequest(
                ProtocolVersion.Current,
                descriptor.EnvironmentId,
                ClientId.New(),
                CommandId.New(),
                thread.ThreadId,
                snapshot.ProjectionEpoch,
                null,
                new ThreadStartTurnCommand("Implement composer power features")), cancellation.Token);
            Assert.True(receipt.State is CommandReceiptState.Accepted or CommandReceiptState.Completed);
            while (await stream.MoveNextAsync())
            {
                if ((stream.Current as ThreadEventEnvelope)?.Event is TurnSettledEvent)
                {
                    break;
                }
            }
        }

        var updated = await host.Environment.GetThreadAsync(thread.ThreadId, cancellation.Token);
        Assert.Equal("Implement composer power features", updated.Title);
        Assert.Equal(ThreadTitleKind.Generated, updated.TitleKind);
        Assert.False(updated.IsSettled);

        var compactReceipt = await host.Environment.ExecuteThreadCommandAsync(new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            descriptor.EnvironmentId,
            ClientId.New(),
            CommandId.New(),
            thread.ThreadId,
            null,
            null,
            new ThreadCompactContextCommand("Keep implementation decisions")), cancellation.Token);
        Assert.Equal(CommandReceiptState.Completed, compactReceipt.State);

        await using var compactedStream = host.Environment.SubscribeThreadAsync(thread.ThreadId, null, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        Assert.True(await compactedStream.MoveNextAsync());
        var compacted = Assert.IsType<ThreadSnapshotEnvelope>(compactedStream.Current).Projection.Compaction;
        Assert.Equal(ContextCompactionState.Completed, compacted?.State);
        Assert.Equal(1200, compacted?.TokensBefore);
        Assert.Equal(320, compacted?.EstimatedTokensAfter);
    }

    [Fact]
    public async Task SignalRStreamsReconnectsIdempotentlyAndResumesAfterRestart()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectPath = temporaryDirectory.CreateDirectory("project");
        var clientId = ClientId.New();
        EnvironmentId environmentId;
        ThreadDescriptor thread;
        ProjectionEpoch firstEpoch;

        await using (var firstHost = await EmbeddedEnvironmentHost.StartAsync(options))
        await using (var connection = HostTestConnection.Create(firstHost))
        {
            await connection.StartAsync();
            var descriptor = await connection.InvokeAsync<EnvironmentDescriptor>("GetEnvironmentDescriptor");
            environmentId = descriptor.EnvironmentId;
            Assert.True(descriptor.PiAvailable);
            Assert.Equal("0.84.4", descriptor.PiVersion);

            var project = await connection.InvokeAsync<ProjectDescriptor>(
                "AddProject",
                new AddProjectRequest(projectPath));
            thread = await connection.InvokeAsync<ThreadDescriptor>(
                "CreateThread",
                new CreateThreadRequest(project.ProjectId));
            var listedThreads = await connection.InvokeAsync<ThreadDescriptor[]>("ListThreads", project.ProjectId);
            Assert.Equal(thread.ThreadId, Assert.Single(listedThreads).ThreadId);

            var incompatibleRequest = new ExecuteThreadCommandRequest(
                99,
                environmentId,
                clientId,
                CommandId.New(),
                thread.ThreadId,
                null,
                null,
                new ThreadStartTurnCommand("unsupported"));
            var incompatible = await Assert.ThrowsAsync<HubException>(() =>
                connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", incompatibleRequest));
            Assert.Contains("ProtocolIncompatible", incompatible.Message, StringComparison.Ordinal);

            using var streamCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var stream = connection.StreamAsync<ThreadEnvelope>(
                    "SubscribeThread",
                    thread.ThreadId,
                    null,
                    streamCancellation.Token)
                .GetAsyncEnumerator(streamCancellation.Token);
            Assert.True(await stream.MoveNextAsync());
            var snapshot = Assert.IsType<ThreadSnapshotEnvelope>(stream.Current);
            Assert.Equal(ThreadRuntimeState.Ready, snapshot.Projection.RuntimeState);
            Assert.Empty(snapshot.Projection.Messages);
            Assert.True(IsBelow(options.SessionRoot, snapshot.Projection.PiSessionFile));
            firstEpoch = snapshot.ProjectionEpoch;

            var commandId = CommandId.New();
            var request = new ExecuteThreadCommandRequest(
                ProtocolVersion.Current,
                environmentId,
                clientId,
                commandId,
                thread.ThreadId,
                snapshot.ProjectionEpoch,
                null,
                new ThreadStartTurnCommand("Hello host"));
            var initialReceipt = await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", request);
            Assert.True(initialReceipt.State is CommandReceiptState.Accepted or CommandReceiptState.Completed);

            var envelopes = new List<ThreadEventEnvelope>();
            while (await stream.MoveNextAsync())
            {
                var envelope = Assert.IsType<ThreadEventEnvelope>(stream.Current);
                envelopes.Add(envelope);
                if (envelope.Event is TurnSettledEvent)
                {
                    break;
                }
            }

            Assert.NotEmpty(envelopes);
            Assert.All(
                envelopes.Zip(envelopes.Skip(1)),
                static pair => Assert.Equal(pair.First.Sequence.Value + 1, pair.Second.Sequence.Value));
            var completedMessage = Assert.IsType<MessageCompletedEvent>(
                envelopes.Select(static envelope => envelope.Event)
                    .Single(static @event => @event is MessageCompletedEvent));
            var settledTurn = Assert.IsType<TurnSettledEvent>(
                envelopes.Select(static envelope => envelope.Event)
                    .Single(static @event => @event is TurnSettledEvent));
            Assert.Equal("Hello from Fake Pi 👽", completedMessage.Message.Text);
            Assert.NotNull(settledTurn.Metrics?.ElapsedMilliseconds);
            Assert.Equal(2, settledTurn.Metrics?.Usage?.TotalTokens);
            Assert.Equal(2, settledTurn.Metrics?.ContextTokens);
            Assert.Equal(100_000, settledTurn.Metrics?.ContextWindow);

            var completedReceipt = await WaitForReceiptAsync(connection, clientId, commandId);
            Assert.Equal(CommandReceiptState.Completed, completedReceipt.State);
            var replay = await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", request);
            Assert.Equal(CommandReceiptState.Completed, replay.State);

            var conflicting = request with { Command = new ThreadStartTurnCommand("different body") };
            var conflict = await Assert.ThrowsAsync<HubException>(() =>
                connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", conflicting));
            Assert.Contains("CommandConflict", conflict.Message, StringComparison.Ordinal);

            var reconnectBase = envelopes[^3];
            var expectedMissing = envelopes.Where(envelope => envelope.Sequence > reconnectBase.Sequence).ToArray();
            using var reconnectCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var reconnect = connection.StreamAsync<ThreadEnvelope>(
                    "SubscribeThread",
                    thread.ThreadId,
                    new ThreadCursor(reconnectBase.ProjectionEpoch, reconnectBase.Sequence),
                    reconnectCancellation.Token)
                .GetAsyncEnumerator(reconnectCancellation.Token);
            var reconnected = new List<ThreadEventEnvelope>();
            while (reconnected.Count < expectedMissing.Length && await reconnect.MoveNextAsync())
            {
                reconnected.Add(Assert.IsType<ThreadEventEnvelope>(reconnect.Current));
            }

            Assert.Equal(expectedMissing.Select(static envelope => envelope.Sequence),
                reconnected.Select(static envelope => envelope.Sequence));
            Assert.Equal(1, CountFakePiCommands(options.SessionRoot, "prompt"));
        }

        await using (var restartedHost = await EmbeddedEnvironmentHost.StartAsync(options))
        await using (var restartedConnection = HostTestConnection.Create(restartedHost))
        {
            await restartedConnection.StartAsync();
            var restartedDescriptor = await restartedConnection.InvokeAsync<EnvironmentDescriptor>(
                "GetEnvironmentDescriptor");
            Assert.Equal(environmentId, restartedDescriptor.EnvironmentId);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var resumed = restartedConnection.StreamAsync<ThreadEnvelope>(
                    "SubscribeThread",
                    thread.ThreadId,
                    null,
                    cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);
            Assert.True(await resumed.MoveNextAsync());
            var snapshot = Assert.IsType<ThreadSnapshotEnvelope>(resumed.Current);

            Assert.NotEqual(firstEpoch, snapshot.ProjectionEpoch);
            Assert.Equal(ThreadRuntimeState.Ready, snapshot.Projection.RuntimeState);
            Assert.Equal(2, snapshot.Projection.Messages.Count);
            Assert.Equal("Hello from Fake Pi 👽", snapshot.Projection.Messages[^1].Text);
            Assert.True(IsBelow(options.SessionRoot, snapshot.Projection.PiSessionFile));
            Assert.Equal(1, CountFakePiCommands(options.SessionRoot, "prompt"));

            var staleEpochRequest = new ExecuteThreadCommandRequest(
                ProtocolVersion.Current,
                environmentId,
                clientId,
                CommandId.New(),
                thread.ThreadId,
                firstEpoch,
                null,
                new ThreadStartTurnCommand("must not dispatch"));
            var stale = await Assert.ThrowsAsync<HubException>(() =>
                restartedConnection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", staleEpochRequest));
            Assert.Contains("ResyncRequired", stale.Message, StringComparison.Ordinal);
            Assert.Equal(1, CountFakePiCommands(options.SessionRoot, "prompt"));
        }
    }

    [Fact]
    public async Task DraftSavesUseReceiptsAndRejectStaleRevisions()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectPath = temporaryDirectory.CreateDirectory("project");
        await using var host = await EmbeddedEnvironmentHost.StartAsync(options);
        await using var connection = HostTestConnection.Create(host);
        await connection.StartAsync();
        var descriptor = await connection.InvokeAsync<EnvironmentDescriptor>("GetEnvironmentDescriptor");
        Assert.Contains("thread.queue", descriptor.Capabilities);
        Assert.Contains("thread.agents", descriptor.Capabilities);
        Assert.Contains("thread.draft", descriptor.Capabilities);
        var project = await connection.InvokeAsync<ProjectDescriptor>(
            "AddProject",
            new AddProjectRequest(projectPath));
        var thread = await connection.InvokeAsync<ThreadDescriptor>(
            "CreateThread",
            new CreateThreadRequest(project.ProjectId));
        var initial = await connection.InvokeAsync<ThreadDraft>("GetThreadDraft", thread.ThreadId);
        var clientId = ClientId.New();
        var request = new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            descriptor.EnvironmentId,
            clientId,
            CommandId.New(),
            thread.ThreadId,
            null,
            null,
            new ThreadSaveDraftCommand(initial.DraftId, initial.Revision, "durable draft"));

        var receipt = await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", request);
        var replay = await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", request);
        var saved = await connection.InvokeAsync<ThreadDraft>("GetThreadDraft", thread.ThreadId);

        Assert.Equal(CommandReceiptState.Completed, receipt.State);
        Assert.Equal(receipt, replay);
        Assert.Equal("durable draft", saved.Text);
        Assert.Equal(1, saved.Revision);

        var staleRequest = request with
        {
            CommandId = CommandId.New(),
            Command = new ThreadSaveDraftCommand(initial.DraftId, initial.Revision, "stale draft"),
        };
        var stale = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", staleRequest));
        Assert.Contains(ProtocolErrorCodes.DraftConflict, stale.Message, StringComparison.Ordinal);

        var conflict = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync<CommandReceipt>(
                "ExecuteThreadCommand",
                request with { Command = new ThreadSaveDraftCommand(initial.DraftId, initial.Revision, "other") }));
        Assert.Contains(ProtocolErrorCodes.CommandConflict, conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PiConfigurationUsesCapabilitiesReceiptsValidationAndRestartPersistence()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectPath = temporaryDirectory.CreateDirectory("project");
        var clientId = ClientId.New();
        ThreadDescriptor thread;
        EnvironmentId environmentId;

        await using (var host = await EmbeddedEnvironmentHost.StartAsync(options))
        await using (var connection = HostTestConnection.Create(host))
        {
            await connection.StartAsync();
            var descriptor = await connection.InvokeAsync<EnvironmentDescriptor>("GetEnvironmentDescriptor");
            environmentId = descriptor.EnvironmentId;
            Assert.Contains("thread.configure", descriptor.Capabilities);
            var project = await connection.InvokeAsync<ProjectDescriptor>(
                "AddProject",
                new AddProjectRequest(projectPath));
            thread = await connection.InvokeAsync<ThreadDescriptor>(
                "CreateThread",
                new CreateThreadRequest(project.ProjectId));
            var initial = await connection.InvokeAsync<ThreadPiConfigurationSnapshot>(
                "GetThreadPiConfiguration",
                thread.ThreadId);

            Assert.Equal(0, initial.Configuration.Revision);
            Assert.Null(initial.Configuration.Model);
            Assert.Equal("fake-standard", initial.ActiveModel?.ModelId);
            Assert.Equal(PiThinkingLevel.Off, initial.ActiveThinkingLevel);
            Assert.Equal(2, initial.Capabilities.Models.Count);
            Assert.Contains(PiThinkingLevel.High, initial.Capabilities.ThinkingLevels);
            Assert.Empty(initial.Capabilities.RuntimeModes);

            var commandId = CommandId.New();
            var request = new ExecuteThreadCommandRequest(
                ProtocolVersion.Current,
                environmentId,
                clientId,
                commandId,
                thread.ThreadId,
                null,
                null,
                new ThreadUpdatePiConfigurationCommand(
                    initial.Configuration.Revision,
                    new PiModelSelection("fake", "fake-standard"),
                    PiThinkingLevel.High,
                    null));
            var receipt = await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", request);
            var replay = await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", request);
            var updated = await connection.InvokeAsync<ThreadPiConfigurationSnapshot>(
                "GetThreadPiConfiguration",
                thread.ThreadId);

            Assert.Equal(CommandReceiptState.Completed, receipt.State);
            Assert.Equal(receipt, replay);
            Assert.Equal(1, updated.Configuration.Revision);
            Assert.Equal("fake-standard", updated.Configuration.Model?.ModelId);
            Assert.Equal(PiThinkingLevel.High, updated.Configuration.ThinkingLevel);
            Assert.Equal(PiThinkingLevel.High, updated.ActiveThinkingLevel);

            var unsupportedCommandId = CommandId.New();
            var unsupported = request with
            {
                CommandId = unsupportedCommandId,
                Command = new ThreadUpdatePiConfigurationCommand(
                    updated.Configuration.Revision,
                    updated.Configuration.Model,
                    updated.Configuration.ThinkingLevel,
                    "full-access"),
            };
            var unsupportedFailure = await Assert.ThrowsAsync<HubException>(() =>
                connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", unsupported));
            var rejected = await connection.InvokeAsync<CommandReceipt?>(
                "GetCommandReceipt",
                clientId,
                unsupportedCommandId);

            Assert.Contains(
                ProtocolErrorCodes.PiConfigurationUnsupported,
                unsupportedFailure.Message,
                StringComparison.Ordinal);
            Assert.Equal(CommandReceiptState.Rejected, rejected?.State);

            var unsupportedModel = request with
            {
                CommandId = CommandId.New(),
                Command = new ThreadUpdatePiConfigurationCommand(
                    updated.Configuration.Revision,
                    new PiModelSelection("fake", "missing-model"),
                    PiThinkingLevel.Off,
                    null),
            };
            var unsupportedModelFailure = await Assert.ThrowsAsync<HubException>(() =>
                connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", unsupportedModel));
            Assert.Contains(
                ProtocolErrorCodes.PiConfigurationUnsupported,
                unsupportedModelFailure.Message,
                StringComparison.Ordinal);

            var unsupportedThinking = request with
            {
                CommandId = CommandId.New(),
                Command = new ThreadUpdatePiConfigurationCommand(
                    updated.Configuration.Revision,
                    new PiModelSelection("fake", "fake-fast"),
                    PiThinkingLevel.High,
                    null),
            };
            var unsupportedThinkingFailure = await Assert.ThrowsAsync<HubException>(() =>
                connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", unsupportedThinking));
            var afterRejectedThinking = await connection.InvokeAsync<ThreadPiConfigurationSnapshot>(
                "GetThreadPiConfiguration",
                thread.ThreadId);
            Assert.Contains(
                ProtocolErrorCodes.PiConfigurationUnsupported,
                unsupportedThinkingFailure.Message,
                StringComparison.Ordinal);
            Assert.Equal("fake-standard", afterRejectedThinking.ActiveModel?.ModelId);
            Assert.Equal(PiThinkingLevel.High, afterRejectedThinking.ActiveThinkingLevel);
            Assert.Equal(1, afterRejectedThinking.Configuration.Revision);

            var stale = request with
            {
                CommandId = CommandId.New(),
                Command = new ThreadUpdatePiConfigurationCommand(
                    initial.Configuration.Revision,
                    new PiModelSelection("fake", "fake-fast"),
                    PiThinkingLevel.Off,
                    null),
            };
            var staleFailure = await Assert.ThrowsAsync<HubException>(() =>
                connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", stale));
            Assert.Contains(
                ProtocolErrorCodes.PiConfigurationConflict,
                staleFailure.Message,
                StringComparison.Ordinal);
        }

        await using (var restartedHost = await EmbeddedEnvironmentHost.StartAsync(options))
        await using (var restartedConnection = HostTestConnection.Create(restartedHost))
        {
            await restartedConnection.StartAsync();
            var restored = await restartedConnection.InvokeAsync<ThreadPiConfigurationSnapshot>(
                "GetThreadPiConfiguration",
                thread.ThreadId);

            Assert.Equal(environmentId, restored.Configuration.EnvironmentId);
            Assert.Equal(1, restored.Configuration.Revision);
            Assert.Equal("fake-standard", restored.ActiveModel?.ModelId);
            Assert.Equal(PiThinkingLevel.High, restored.ActiveThinkingLevel);
        }
    }

    [Fact]
    public async Task ThreadLifecycleUsesReceiptsConflictsFilteringSearchAndRestartPersistence()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectPath = temporaryDirectory.CreateDirectory("project");
        var clientId = ClientId.New();
        EnvironmentId environmentId;
        ProjectId projectId;
        ThreadDescriptor thread;
        ThreadId otherThreadId;

        await using (var host = await EmbeddedEnvironmentHost.StartAsync(options))
        await using (var connection = HostTestConnection.Create(host))
        {
            await connection.StartAsync();
            var descriptor = await connection.InvokeAsync<EnvironmentDescriptor>("GetEnvironmentDescriptor");
            environmentId = descriptor.EnvironmentId;
            Assert.Contains("thread.lifecycle", descriptor.Capabilities);
            Assert.Contains("thread.search", descriptor.Capabilities);
            var project = await connection.InvokeAsync<ProjectDescriptor>(
                "AddProject",
                new AddProjectRequest(projectPath));
            projectId = project.ProjectId;
            thread = await connection.InvokeAsync<ThreadDescriptor>(
                "CreateThread",
                new CreateThreadRequest(projectId, "Initial title"));
            var other = await connection.InvokeAsync<ThreadDescriptor>(
                "CreateThread",
                new CreateThreadRequest(projectId, "Other work"));
            otherThreadId = other.ThreadId;

            var renameRequest = new ExecuteThreadCommandRequest(
                ProtocolVersion.Current,
                environmentId,
                clientId,
                CommandId.New(),
                thread.ThreadId,
                null,
                null,
                new ThreadRenameCommand(thread.Revision, "Roadmap review"));
            var renamedReceipt = await connection.InvokeAsync<CommandReceipt>(
                "ExecuteThreadCommand",
                renameRequest);
            var replayedReceipt = await connection.InvokeAsync<CommandReceipt>(
                "ExecuteThreadCommand",
                renameRequest);
            var renamedSearch = await connection.InvokeAsync<SearchThreadsResult>(
                "SearchThreads",
                new SearchThreadsRequest(projectId, "ROADMAP"));
            var renamed = Assert.Single(renamedSearch.Threads);

            Assert.Equal(CommandReceiptState.Completed, renamedReceipt.State);
            Assert.Equal(renamedReceipt, replayedReceipt);
            Assert.Equal("Roadmap review", renamed.Title);
            Assert.Equal(1, renamed.Revision);

            var pinRequest = renameRequest with
            {
                CommandId = CommandId.New(),
                Command = new ThreadSetPinnedCommand(renamed.Revision, true),
            };
            Assert.Equal(
                CommandReceiptState.Completed,
                (await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", pinRequest)).State);
            var pinnedList = await connection.InvokeAsync<ThreadDescriptor[]>("ListThreads", projectId);
            var pinned = Assert.Single(pinnedList, candidate => candidate.ThreadId == thread.ThreadId);
            Assert.Equal(thread.ThreadId, pinnedList[0].ThreadId);
            Assert.True(pinned.IsPinned);
            Assert.Equal(2, pinned.Revision);

            var archiveRequest = renameRequest with
            {
                CommandId = CommandId.New(),
                Command = new ThreadSetArchivedCommand(pinned.Revision, true),
            };
            Assert.Equal(
                CommandReceiptState.Completed,
                (await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", archiveRequest)).State);
            var activeOnly = await connection.InvokeAsync<ThreadDescriptor[]>("ListThreads", projectId);
            Assert.Equal(other.ThreadId, Assert.Single(activeOnly).ThreadId);
            Assert.Empty((await connection.InvokeAsync<SearchThreadsResult>(
                "SearchThreads",
                new SearchThreadsRequest(projectId, "roadmap"))).Threads);
            var archivedSearch = await connection.InvokeAsync<SearchThreadsResult>(
                "SearchThreads",
                new SearchThreadsRequest(projectId, "roadmap", IncludeArchived: true));
            var archived = Assert.Single(archivedSearch.Threads);
            Assert.True(archived.IsArchived);
            Assert.True(archived.IsPinned);
            Assert.Equal(3, archived.Revision);
            Assert.Equal(thread.PiSessionId, archived.PiSessionId);
            Assert.Null(archived.PiSessionFile);
            var limited = await connection.InvokeAsync<SearchThreadsResult>(
                "SearchThreads",
                new SearchThreadsRequest(projectId, string.Empty, IncludeArchived: true, Limit: 1));
            Assert.Single(limited.Threads);
            Assert.True(limited.IsTruncated);

            var unarchiveRequest = renameRequest with
            {
                CommandId = CommandId.New(),
                Command = new ThreadSetArchivedCommand(archived.Revision, false),
            };
            await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", unarchiveRequest);
            var unarchived = Assert.Single((await connection.InvokeAsync<SearchThreadsResult>(
                "SearchThreads",
                new SearchThreadsRequest(projectId, "roadmap"))).Threads);
            Assert.False(unarchived.IsArchived);
            Assert.Equal(4, unarchived.Revision);

            var unpinRequest = renameRequest with
            {
                CommandId = CommandId.New(),
                Command = new ThreadSetPinnedCommand(unarchived.Revision, false),
            };
            await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", unpinRequest);
            var unpinned = Assert.Single(
                await connection.InvokeAsync<ThreadDescriptor[]>("ListThreads", projectId),
                candidate => candidate.ThreadId == thread.ThreadId);
            Assert.False(unpinned.IsPinned);
            Assert.Equal(5, unpinned.Revision);

            var staleCommandId = CommandId.New();
            var staleRequest = renameRequest with
            {
                CommandId = staleCommandId,
                Command = new ThreadRenameCommand(0, "Stale overwrite"),
            };
            var staleFailure = await Assert.ThrowsAsync<HubException>(() =>
                connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", staleRequest));
            var staleReceipt = await connection.InvokeAsync<CommandReceipt?>(
                "GetCommandReceipt",
                clientId,
                staleCommandId);
            Assert.Contains(ProtocolErrorCodes.ThreadConflict, staleFailure.Message, StringComparison.Ordinal);
            Assert.Equal(CommandReceiptState.Rejected, staleReceipt?.State);
            Assert.Equal(ProtocolErrorCodes.ThreadConflict, staleReceipt?.ErrorCode);
        }

        await using (var restartedHost = await EmbeddedEnvironmentHost.StartAsync(options))
        await using (var restartedConnection = HostTestConnection.Create(restartedHost))
        {
            await restartedConnection.StartAsync();
            var restoredSearch = await restartedConnection.InvokeAsync<SearchThreadsResult>(
                "SearchThreads",
                new SearchThreadsRequest(projectId, "roadmap", IncludeArchived: true));
            var restored = Assert.Single(restoredSearch.Threads);

            Assert.Equal(environmentId, restored.EnvironmentId);
            Assert.Equal(thread.ThreadId, restored.ThreadId);
            Assert.Equal(thread.PiSessionId, restored.PiSessionId);
            Assert.Equal("Roadmap review", restored.Title);
            Assert.Equal(5, restored.Revision);
            Assert.False(restored.IsArchived);
            Assert.False(restored.IsPinned);
            var restoredList = await restartedConnection.InvokeAsync<ThreadDescriptor[]>(
                "ListThreads",
                projectId);
            Assert.Equal(2, restoredList.Length);
            Assert.Contains(restoredList, candidate => candidate.ThreadId == otherThreadId);
            Assert.Contains(restoredList, candidate => candidate.ThreadId == thread.ThreadId);
        }
    }

    [Fact]
    public async Task PendingInteractionsReconnectResolveIdempotentlyAndRejectStaleAnswers()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions("interactions");
        var projectPath = temporaryDirectory.CreateDirectory("project");
        await using var host = await EmbeddedEnvironmentHost.StartAsync(options);
        await using var connection = HostTestConnection.Create(host);
        await connection.StartAsync();
        var descriptor = await connection.InvokeAsync<EnvironmentDescriptor>("GetEnvironmentDescriptor");
        Assert.Contains("thread.interact", descriptor.Capabilities);
        var project = await connection.InvokeAsync<ProjectDescriptor>(
            "AddProject",
            new AddProjectRequest(projectPath));
        var thread = await connection.InvokeAsync<ThreadDescriptor>(
            "CreateThread",
            new CreateThreadRequest(project.ProjectId));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var stream = connection.StreamAsync<ThreadEnvelope>(
                "SubscribeThread",
                thread.ThreadId,
                null,
                cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        Assert.True(await stream.MoveNextAsync());
        var initial = Assert.IsType<ThreadSnapshotEnvelope>(stream.Current);
        var clientId = ClientId.New();
        var startRequest = new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            descriptor.EnvironmentId,
            clientId,
            CommandId.New(),
            thread.ThreadId,
            initial.ProjectionEpoch,
            null,
            new ThreadStartTurnCommand("exercise interactions"));
        await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", startRequest, cancellation.Token);

        TurnId? turnId = null;
        ApprovalRequestedEvent? approvalRequested = null;
        while (approvalRequested is null && await stream.MoveNextAsync())
        {
            if (stream.Current is not ThreadEventEnvelope envelope)
            {
                continue;
            }

            turnId = (envelope.Event as TurnStartedEvent)?.TurnId ?? turnId;
            approvalRequested = envelope.Event as ApprovalRequestedEvent ?? approvalRequested;
        }

        Assert.NotNull(turnId);
        Assert.NotNull(approvalRequested);

        await using (var reconnected = connection.StreamAsync<ThreadEnvelope>(
                         "SubscribeThread",
                         thread.ThreadId,
                         null,
                         cancellation.Token)
                     .GetAsyncEnumerator(cancellation.Token))
        {
            Assert.True(await reconnected.MoveNextAsync());
            var pendingSnapshot = Assert.IsType<ThreadSnapshotEnvelope>(reconnected.Current);
            var pendingApproval = Assert.Single(pendingSnapshot.Projection.Timeline.OfType<ApprovalTimelineItem>());
            Assert.Equal(InteractionState.Pending, pendingApproval.State);
            Assert.Equal(approvalRequested.InteractionId, pendingApproval.InteractionId);
        }

        var approvalCommandId = CommandId.New();
        var approvalRequest = new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            descriptor.EnvironmentId,
            clientId,
            approvalCommandId,
            thread.ThreadId,
            initial.ProjectionEpoch,
            turnId,
            new ThreadRespondToApprovalCommand(
                approvalRequested.InteractionId,
                ApprovalDecision.Approve));
        var approvalReceipt = await connection.InvokeAsync<CommandReceipt>(
            "ExecuteThreadCommand",
            approvalRequest,
            cancellation.Token);
        Assert.Equal(CommandReceiptState.Completed, approvalReceipt.State);
        var approvalReplay = await connection.InvokeAsync<CommandReceipt>(
            "ExecuteThreadCommand",
            approvalRequest,
            cancellation.Token);
        Assert.Equal(CommandReceiptState.Completed, approvalReplay.State);

        QuestionRequestedEvent? questionRequested = null;
        while (questionRequested is null && await stream.MoveNextAsync())
        {
            questionRequested = (stream.Current as ThreadEventEnvelope)?.Event as QuestionRequestedEvent;
        }

        Assert.NotNull(questionRequested);
        Assert.Equal(["Run tests", "Skip tests"], questionRequested.Options);
        var answerRequest = new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            descriptor.EnvironmentId,
            clientId,
            CommandId.New(),
            thread.ThreadId,
            initial.ProjectionEpoch,
            turnId,
            new ThreadAnswerQuestionCommand(questionRequested.InteractionId, "Run tests"));
        var answerReceipt = await connection.InvokeAsync<CommandReceipt>(
            "ExecuteThreadCommand",
            answerRequest,
            cancellation.Token);
        Assert.Equal(CommandReceiptState.Completed, answerReceipt.State);

        var staleRequest = answerRequest with
        {
            CommandId = CommandId.New(),
            ExpectedTurnId = null,
        };
        var stale = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<CommandReceipt>(
            "ExecuteThreadCommand",
            staleRequest,
            cancellation.Token));
        Assert.Contains(ProtocolErrorCodes.InteractionNotPending, stale.Message, StringComparison.Ordinal);

        var settled = false;
        while (!settled && await stream.MoveNextAsync())
        {
            settled = (stream.Current as ThreadEventEnvelope)?.Event is TurnSettledEvent;
        }

        Assert.True(settled);
        Assert.Equal(
            "approved",
            await File.ReadAllTextAsync(Path.Combine(options.SessionRoot, "approval-response.txt"), cancellation.Token));
        Assert.Equal(
            "Run tests",
            await File.ReadAllTextAsync(Path.Combine(options.SessionRoot, "question-response.txt"), cancellation.Token));
    }

    [Fact]
    public async Task StopCommandClearsQueueAbortsAndCompletesBothReceipts()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions("stop");
        var projectPath = temporaryDirectory.CreateDirectory("project");
        await using var host = await EmbeddedEnvironmentHost.StartAsync(options);
        await using var connection = HostTestConnection.Create(host);
        await connection.StartAsync();
        var descriptor = await connection.InvokeAsync<EnvironmentDescriptor>("GetEnvironmentDescriptor");
        var project = await connection.InvokeAsync<ProjectDescriptor>(
            "AddProject",
            new AddProjectRequest(projectPath));
        var thread = await connection.InvokeAsync<ThreadDescriptor>(
            "CreateThread",
            new CreateThreadRequest(project.ProjectId));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var stream = connection.StreamAsync<ThreadEnvelope>(
                "SubscribeThread",
                thread.ThreadId,
                null,
                cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        Assert.True(await stream.MoveNextAsync());
        var snapshot = Assert.IsType<ThreadSnapshotEnvelope>(stream.Current);
        var clientId = ClientId.New();
        var startCommandId = CommandId.New();
        var startRequest = new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            descriptor.EnvironmentId,
            clientId,
            startCommandId,
            thread.ThreadId,
            snapshot.ProjectionEpoch,
            null,
            new ThreadStartTurnCommand("start retry"));
        await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", startRequest, cancellation.Token);

        TurnStartedEvent? turnStarted = null;
        while (turnStarted is null && await stream.MoveNextAsync())
        {
            turnStarted = (stream.Current as ThreadEventEnvelope)?.Event as TurnStartedEvent;
        }

        Assert.NotNull(turnStarted);
        var stopCommandId = CommandId.New();
        var stopRequest = new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            descriptor.EnvironmentId,
            clientId,
            stopCommandId,
            thread.ThreadId,
            snapshot.ProjectionEpoch,
            turnStarted.TurnId,
            new ThreadStopTurnCommand());
        await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", stopRequest, cancellation.Token);

        var settled = false;
        while (!settled && await stream.MoveNextAsync())
        {
            settled = (stream.Current as ThreadEventEnvelope)?.Event is TurnSettledEvent;
        }

        Assert.True(settled);
        Assert.Equal(CommandReceiptState.Completed,
            (await WaitForReceiptAsync(connection, clientId, startCommandId)).State);
        Assert.Equal(CommandReceiptState.Completed,
            (await WaitForReceiptAsync(connection, clientId, stopCommandId)).State);
        var commands = ReadFakePiCommands(options.SessionRoot)
            .Where(static command => command is "clear_queue" or "abort" or "abort_retry")
            .ToArray();
        Assert.Equal(["clear_queue", "abort"], commands);
    }

    [Fact]
    public async Task ActiveTurnCommandsProjectQueueContentsModesAndClearing()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions("queue");
        await using var host = await EmbeddedEnvironmentHost.StartAsync(options);
        await using var connection = HostTestConnection.Create(host);
        await connection.StartAsync();
        var descriptor = await connection.InvokeAsync<EnvironmentDescriptor>("GetEnvironmentDescriptor");
        var project = await connection.InvokeAsync<ProjectDescriptor>(
            "AddProject",
            new AddProjectRequest(temporaryDirectory.CreateDirectory("queue-project")));
        var thread = await connection.InvokeAsync<ThreadDescriptor>(
            "CreateThread",
            new CreateThreadRequest(project.ProjectId));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var stream = connection.StreamAsync<ThreadEnvelope>(
                "SubscribeThread",
                thread.ThreadId,
                null,
                cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        Assert.True(await stream.MoveNextAsync());
        var snapshot = Assert.IsType<ThreadSnapshotEnvelope>(stream.Current);
        var clientId = ClientId.New();

        async Task<CommandReceipt> ExecuteAsync(ThreadCommand command, TurnId? expectedTurnId = null) =>
            await connection.InvokeAsync<CommandReceipt>(
                "ExecuteThreadCommand",
                new ExecuteThreadCommandRequest(
                    ProtocolVersion.Current,
                    descriptor.EnvironmentId,
                    clientId,
                    CommandId.New(),
                    thread.ThreadId,
                    snapshot.ProjectionEpoch,
                    expectedTurnId,
                    command),
                cancellation.Token);

        await ExecuteAsync(new ThreadStartTurnCommand("Start the task"));
        TurnStartedEvent? turnStarted = null;
        while (turnStarted is null && await stream.MoveNextAsync())
        {
            turnStarted = (stream.Current as ThreadEventEnvelope)?.Event as TurnStartedEvent;
        }

        Assert.NotNull(turnStarted);
        var steerReceipt = await ExecuteAsync(
            new ThreadQueueSteeringCommand("Change direction", null, null, null),
            turnStarted.TurnId);
        var steered = await ReadQueueAsync(static queue =>
            queue.Messages.Any(message => message.Kind == QueuedMessageKind.Steering));
        var followUpReceipt = await ExecuteAsync(
            new ThreadQueueFollowUpCommand("Then summarize", null, null, null),
            turnStarted.TurnId);
        var followed = await ReadQueueAsync(static queue => queue.Messages.Count == 2);
        var modeReceipt = await ExecuteAsync(new ThreadSetQueueDeliveryModeCommand(
            QueuedMessageKind.Steering,
            QueueDeliveryMode.OneAtATime));
        var configured = await ReadQueueAsync(static queue =>
            queue.SteeringMode == QueueDeliveryMode.OneAtATime && queue.PendingMessageCount == 2);
        var clearReceipt = await ExecuteAsync(new ThreadClearQueueCommand(), turnStarted.TurnId);
        var cleared = await ReadQueueAsync(static queue => queue.DeliveryState == QueueDeliveryState.Cleared);

        Assert.Equal(CommandReceiptState.Completed, steerReceipt.State);
        Assert.Equal(CommandReceiptState.Completed, followUpReceipt.State);
        Assert.Equal(CommandReceiptState.Completed, modeReceipt.State);
        Assert.Equal(CommandReceiptState.Completed, clearReceipt.State);
        Assert.Equal("Change direction", Assert.Single(steered.Messages).Text);
        Assert.Equal(2, followed.PendingMessageCount);
        Assert.Contains(followed.Messages, static message =>
            message.Kind == QueuedMessageKind.FollowUp && message.Text == "Then summarize");
        Assert.Equal(QueueDeliveryMode.OneAtATime, configured.SteeringMode);
        Assert.Empty(cleared.Messages);
        Assert.Equal(0, cleared.PendingMessageCount);

        await ExecuteAsync(new ThreadStopTurnCommand(), turnStarted.TurnId);
        while (await stream.MoveNextAsync())
        {
            if ((stream.Current as ThreadEventEnvelope)?.Event is TurnSettledEvent)
            {
                break;
            }
        }

        async Task<ThreadQueueProjection> ReadQueueAsync(Func<ThreadQueueProjection, bool> predicate)
        {
            while (await stream.MoveNextAsync())
            {
                if ((stream.Current as ThreadEventEnvelope)?.Event is QueueStateChangedEvent changed &&
                    predicate(changed.Queue))
                {
                    return changed.Queue;
                }
            }

            throw new EndOfStreamException("The thread ended before the expected queue projection arrived.");
        }
    }

    [Fact]
    public async Task CrashedRuntimeCanRestartRehydrateAndRunAnotherTurn()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions("crash-once");
        var projectPath = temporaryDirectory.CreateDirectory("project");
        await using var host = await EmbeddedEnvironmentHost.StartAsync(options);
        await using var connection = HostTestConnection.Create(host);
        await connection.StartAsync();
        var descriptor = await connection.InvokeAsync<EnvironmentDescriptor>("GetEnvironmentDescriptor");
        var project = await connection.InvokeAsync<ProjectDescriptor>(
            "AddProject",
            new AddProjectRequest(projectPath));
        var thread = await connection.InvokeAsync<ThreadDescriptor>(
            "CreateThread",
            new CreateThreadRequest(project.ProjectId));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var stream = connection.StreamAsync<ThreadEnvelope>(
                "SubscribeThread",
                thread.ThreadId,
                null,
                cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        Assert.True(await stream.MoveNextAsync());
        var initial = Assert.IsType<ThreadSnapshotEnvelope>(stream.Current);
        var clientId = ClientId.New();
        var crashingCommandId = CommandId.New();
        var crashingRequest = new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            descriptor.EnvironmentId,
            clientId,
            crashingCommandId,
            thread.ThreadId,
            initial.ProjectionEpoch,
            null,
            new ThreadStartTurnCommand("crash once"));
        await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", crashingRequest, cancellation.Token);

        ThreadEventEnvelope? failed = null;
        while (failed is null && await stream.MoveNextAsync())
        {
            if ((stream.Current as ThreadEventEnvelope)?.Event is RuntimeFailedEvent)
            {
                failed = (ThreadEventEnvelope)stream.Current;
            }
        }

        Assert.NotNull(failed);
        var failedReceipt = await WaitForTerminalReceiptAsync(
            connection,
            clientId,
            crashingCommandId,
            cancellation.Token);
        Assert.Equal(CommandReceiptState.Failed, failedReceipt.State);
        Assert.Equal(ProtocolErrorCodes.PiRuntimeCrashed, failedReceipt.ErrorCode);

        var restartRequest = new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            descriptor.EnvironmentId,
            clientId,
            CommandId.New(),
            thread.ThreadId,
            failed.ProjectionEpoch,
            null,
            new ThreadRestartRuntimeCommand());
        var restartReceipt = await connection.InvokeAsync<CommandReceipt>(
            "ExecuteThreadCommand",
            restartRequest,
            cancellation.Token);
        Assert.Equal(CommandReceiptState.Completed, restartReceipt.State);

        ThreadSnapshotEnvelope? restarted = null;
        while (restarted is null && await stream.MoveNextAsync())
        {
            if (stream.Current is ThreadSnapshotEnvelope candidate &&
                candidate.Projection.RuntimeState == ThreadRuntimeState.Ready)
            {
                restarted = candidate;
            }
        }

        Assert.NotNull(restarted);
        Assert.NotEqual(initial.ProjectionEpoch, restarted.ProjectionEpoch);
        Assert.Empty(restarted.Projection.Messages);

        var recoveredRequest = new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            descriptor.EnvironmentId,
            clientId,
            CommandId.New(),
            thread.ThreadId,
            restarted.ProjectionEpoch,
            null,
            new ThreadStartTurnCommand("after restart"));
        await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", recoveredRequest, cancellation.Token);

        MessageCompletedEvent? completed = null;
        var settled = false;
        while (!settled && await stream.MoveNextAsync())
        {
            if (stream.Current is not ThreadEventEnvelope @event)
            {
                continue;
            }

            completed = @event.Event as MessageCompletedEvent ?? completed;
            settled = @event.Event is TurnSettledEvent;
        }

        Assert.NotNull(completed);
        Assert.Equal("Hello from Fake Pi 👽", completed.Message.Text);
        Assert.Equal(2, CountFakePiCommands(options.SessionRoot, "prompt"));
    }

    [Fact]
    public async Task ToolOutputIsBoundedBeforeItEntersTheProjectionStream()
    {
        const int outputLimit = 128;
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions("large-tool", outputLimit);
        var projectPath = temporaryDirectory.CreateDirectory("project");
        await using var host = await EmbeddedEnvironmentHost.StartAsync(options);
        await using var connection = HostTestConnection.Create(host);
        await connection.StartAsync();
        var descriptor = await connection.InvokeAsync<EnvironmentDescriptor>("GetEnvironmentDescriptor");
        var project = await connection.InvokeAsync<ProjectDescriptor>(
            "AddProject",
            new AddProjectRequest(projectPath));
        var thread = await connection.InvokeAsync<ThreadDescriptor>(
            "CreateThread",
            new CreateThreadRequest(project.ProjectId));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var stream = connection.StreamAsync<ThreadEnvelope>(
                "SubscribeThread",
                thread.ThreadId,
                null,
                cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        Assert.True(await stream.MoveNextAsync());
        var snapshot = Assert.IsType<ThreadSnapshotEnvelope>(stream.Current);
        var request = new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            descriptor.EnvironmentId,
            ClientId.New(),
            CommandId.New(),
            thread.ThreadId,
            snapshot.ProjectionEpoch,
            null,
            new ThreadStartTurnCommand("emit a large tool result"));
        await connection.InvokeAsync<CommandReceipt>("ExecuteThreadCommand", request, cancellation.Token);

        var previews = new List<string>();
        string? argumentsPreview = null;
        var settled = false;
        while (!settled && await stream.MoveNextAsync())
        {
            if (stream.Current is not ThreadEventEnvelope envelope)
            {
                continue;
            }

            if (envelope.Event is ToolStartedEvent started)
            {
                argumentsPreview = started.Tool.ArgumentsPreview;
            }
            else if (envelope.Event is ToolOutputReplacedEvent replaced)
            {
                previews.Add(replaced.OutputPreview);
            }
            else if (envelope.Event is ToolCompletedEvent completed)
            {
                previews.Add(completed.OutputPreview);
            }

            settled = envelope.Event is TurnSettledEvent;
        }

        Assert.NotEmpty(previews);
        Assert.Equal("{\"command\":\"echo hi\"}", argumentsPreview);
        Assert.All(previews, preview => Assert.True(preview.Length <= outputLimit));
        Assert.EndsWith("tail-marker", previews[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TerminalSessionStreamsInputOutputResizeAndExitOverAuthenticatedHub()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var projectPath = temporaryDirectory.CreateDirectory("terminal-project");
        var options = temporaryDirectory.CreateOptions();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(options);
        await using var connection = HostTestConnection.Create(host);
        await connection.StartAsync();
        var project = await connection.InvokeAsync<ProjectDescriptor>(
            "AddProject",
            new AddProjectRequest(projectPath));
        var terminal = await connection.InvokeAsync<TerminalSessionDescriptor>(
            "StartTerminalSession",
            new StartTerminalSessionRequest(project.ProjectId, TerminalShellKind.CommandPrompt, 90, 24));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var stream = connection.StreamAsync<TerminalEnvelope>(
                "SubscribeTerminal",
                terminal.TerminalSessionId,
                null,
                cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await stream.MoveNextAsync());
        var snapshot = Assert.IsType<TerminalSnapshotEnvelope>(stream.Current);
        Assert.True(
            snapshot.Descriptor.State == TerminalSessionState.Running,
            $"Terminal state was {snapshot.Descriptor.State} ({snapshot.Descriptor.ExitCode}): " +
            $"{snapshot.Descriptor.ErrorMessage}; output={snapshot.BufferedOutput}");
        await connection.InvokeAsync(
            "WriteTerminalInput",
            new WriteTerminalInputRequest(terminal.TerminalSessionId, "echo pistation-terminal-^executed\r\n"),
            cancellation.Token);

        var output = new StringBuilder(snapshot.BufferedOutput);
        while (!output.ToString().Contains("pistation-terminal-executed", StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(await stream.MoveNextAsync());
            if (stream.Current is TerminalOutputEnvelope chunk)
            {
                output.Append(chunk.Text);
            }
        }

        var resized = await connection.InvokeAsync<TerminalSessionDescriptor>(
            "ResizeTerminalSession",
            new ResizeTerminalSessionRequest(terminal.TerminalSessionId, 110, 32),
            cancellation.Token);
        Assert.Equal((110, 32), (resized.Columns, resized.Rows));
        await connection.InvokeAsync(
            "WriteTerminalInput",
            new WriteTerminalInputRequest(
                terminal.TerminalSessionId,
                "powershell -NoProfile -Command \"$s=$Host.UI.RawUI.WindowSize; 'SIZE-{0}x{1}' -f $s.Width,$s.Height\"\r"),
            cancellation.Token);
        while (!output.ToString().Contains("SIZE-110x32", StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(await stream.MoveNextAsync());
            if (stream.Current is TerminalOutputEnvelope chunk)
            {
                output.Append(chunk.Text);
            }
        }

        await connection.InvokeAsync(
            "WriteTerminalInput",
            new WriteTerminalInputRequest(
                terminal.TerminalSessionId,
                "powershell -NoProfile -Command \"while ($true) { Write-Output ('conpty-'+'tick'); Start-Sleep -Milliseconds 100 }\"\r"),
            cancellation.Token);
        while (!output.ToString().Contains("conpty-tick", StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(await stream.MoveNextAsync());
            if (stream.Current is TerminalOutputEnvelope chunk)
            {
                output.Append(chunk.Text);
            }
        }

        await connection.InvokeAsync(
            "WriteTerminalInput",
            new WriteTerminalInputRequest(terminal.TerminalSessionId, "\u0003"),
            cancellation.Token);
        var interruptOutput = new StringBuilder();
        while (!interruptOutput.ToString().Contains("system32\\cmd.exe\u0007", StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(await stream.MoveNextAsync());
            if (stream.Current is TerminalOutputEnvelope chunk)
            {
                output.Append(chunk.Text);
                interruptOutput.Append(chunk.Text);
            }
        }

        await connection.InvokeAsync(
            "WriteTerminalInput",
            new WriteTerminalInputRequest(terminal.TerminalSessionId, "echo CTRL-C-^RECOVERED\r"),
            cancellation.Token);
        while (!output.ToString().Contains("CTRL-C-RECOVERED", StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(await stream.MoveNextAsync());
            if (stream.Current is TerminalOutputEnvelope chunk)
            {
                output.Append(chunk.Text);
            }
        }

        var escapedFakePi = options.PiInstallation!.ExecutablePath.Replace("\"", "\"\"");
        await connection.InvokeAsync(
            "WriteTerminalInput",
            new WriteTerminalInputRequest(
                terminal.TerminalSessionId,
                $"\"{escapedFakePi}\" --terminal-mouse-probe\r"),
            cancellation.Token);
        while (!output.ToString().Contains("MOUSE-READY", StringComparison.Ordinal))
        {
            Assert.True(await stream.MoveNextAsync());
            if (stream.Current is TerminalOutputEnvelope chunk)
            {
                output.Append(chunk.Text);
            }
        }

        await connection.InvokeAsync(
            "WriteTerminalInput",
            new WriteTerminalInputRequest(
                terminal.TerminalSessionId,
                "\u001b[<0;1;1M\u001b[<0;1;1m"),
            cancellation.Token);
        while (!output.ToString().Contains("MOUSE-RECEIVED:LEFT:0:0", StringComparison.Ordinal))
        {
            Assert.True(await stream.MoveNextAsync());
            if (stream.Current is TerminalOutputEnvelope chunk)
            {
                output.Append(chunk.Text);
            }
        }

        var stopped = await connection.InvokeAsync<TerminalSessionDescriptor>(
            "StopTerminalSession",
            new StopTerminalSessionRequest(terminal.TerminalSessionId),
            cancellation.Token);
        Assert.Equal(TerminalSessionState.Exited, stopped.State);
        await connection.InvokeAsync(
            "CloseTerminalSession",
            new CloseTerminalSessionRequest(terminal.TerminalSessionId),
            cancellation.Token);
        Assert.Empty(await connection.InvokeAsync<TerminalSessionDescriptor[]>(
            "ListTerminalSessions",
            project.ProjectId,
            cancellation.Token));
    }

    [Fact]
    public async Task TerminalSessionValidatesDimensionsAndRunningSessionLimit()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions() with { MaximumTerminalSessionsPerProject = 1 };
        await using var host = await EmbeddedEnvironmentHost.StartAsync(options);
        await using var connection = HostTestConnection.Create(host);
        await connection.StartAsync();
        var project = await connection.InvokeAsync<ProjectDescriptor>(
            "AddProject",
            new AddProjectRequest(temporaryDirectory.CreateDirectory("limited-terminal-project")));

        var invalid = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<TerminalSessionDescriptor>(
            "StartTerminalSession",
            new StartTerminalSessionRequest(project.ProjectId, Columns: 1, Rows: 1)));
        Assert.Contains(ProtocolErrorCodes.TerminalInvalid, invalid.Message, StringComparison.Ordinal);

        var first = await connection.InvokeAsync<TerminalSessionDescriptor>(
            "StartTerminalSession",
            new StartTerminalSessionRequest(project.ProjectId, TerminalShellKind.CommandPrompt));
        var limited = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<TerminalSessionDescriptor>(
            "StartTerminalSession",
            new StartTerminalSessionRequest(project.ProjectId, TerminalShellKind.CommandPrompt)));
        Assert.Contains(ProtocolErrorCodes.TerminalLimitExceeded, limited.Message, StringComparison.Ordinal);
        await connection.InvokeAsync(
            "CloseTerminalSession",
            new CloseTerminalSessionRequest(first.TerminalSessionId));
    }

    private static async Task<CommandReceipt> WaitForReceiptAsync(
        HubConnection connection,
        ClientId clientId,
        CommandId commandId)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var receipt = await connection.InvokeAsync<CommandReceipt?>(
                "GetCommandReceipt",
                clientId,
                commandId,
                cancellation.Token);
            if (receipt?.State == CommandReceiptState.Completed)
            {
                return receipt;
            }

            await Task.Delay(20, cancellation.Token);
        }
    }

    private static async Task<CommandReceipt> WaitForTerminalReceiptAsync(
        HubConnection connection,
        ClientId clientId,
        CommandId commandId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var receipt = await connection.InvokeAsync<CommandReceipt?>(
                "GetCommandReceipt",
                clientId,
                commandId,
                cancellationToken);
            if (receipt?.State is CommandReceiptState.Completed or
                                  CommandReceiptState.Rejected or
                                  CommandReceiptState.Failed or
                                  CommandReceiptState.DispatchUncertain)
            {
                return receipt;
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private static int CountFakePiCommands(string sessionRoot, string command) =>
        ReadFakePiCommands(sessionRoot)
            .Count(value => string.Equals(value, command, StringComparison.Ordinal));

    private static IEnumerable<string> ReadFakePiCommands(string sessionRoot) =>
        File.ReadLines(Path.Combine(sessionRoot, "command-log.jsonl"))
            .Select(static line => JsonNode.Parse(line)?["command"]?.GetValue<string>())
            .OfType<string>();

    private static HttpRequestMessage CreateAttachmentRequest(
        EmbeddedEnvironmentHost host,
        EnvironmentId environmentId,
        ClientId clientId,
        CommandId commandId,
        ThreadId threadId,
        ThreadDraft draft,
        AttachmentId attachmentId,
        byte[] bytes,
        bool authenticated)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(
                host.Address,
                $"threads/{Uri.EscapeDataString(threadId.Value)}/draft-attachments?fileName=notes.txt"));
        if (authenticated)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.BearerCredential);
        }

        request.Headers.Add(
            "X-PiStation-Protocol-Version",
            ProtocolVersion.Current.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-PiStation-Environment-Id", environmentId.Value);
        request.Headers.Add("X-PiStation-Client-Id", clientId.Value);
        request.Headers.Add("X-PiStation-Command-Id", commandId.Value);
        request.Headers.Add("X-PiStation-Draft-Id", draft.DraftId.Value);
        request.Headers.Add("X-PiStation-Attachment-Id", attachmentId.Value);
        request.Headers.Add(
            "X-PiStation-Draft-Revision",
            draft.Revision.ToString(CultureInfo.InvariantCulture));
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        return request;
    }

    private static bool IsBelow(string parent, string? candidate)
    {
        if (candidate is null)
        {
            return false;
        }

        var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
