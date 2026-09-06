using System.Text.Json.Nodes;
using PiStation.PiRpc.Decoding;
using PiStation.PiRpc.Diagnostics;
using PiStation.PiRpc.Transport;
using PiStation.PiRpc.Wire.Events;

namespace PiStation.PiRpc.Tests;

public sealed class PiProcessIntegrationTests
{
    [Fact]
    public async Task ForkAndNewSessionRewindConversationEntries()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await using var process = await FakePiTestHost.StartAsync(temporaryDirectory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await process.Connection.PromptAsync("first", cancellation.Token);
        await FakePiTestHost.ReadUntilSettledAsync(process.Connection, cancellation.Token);
        var first = await process.Connection.GetEntriesAsync(cancellationToken: cancellation.Token);
        await process.Connection.PromptAsync("second", cancellation.Token);
        await FakePiTestHost.ReadUntilSettledAsync(process.Connection, cancellation.Token);
        Assert.Equal(4, (await process.Connection.GetEntriesAsync(cancellationToken: cancellation.Token)).Entries.Count);

        var fork = await process.Connection.ForkAsync(first.LeafId!, cancellation.Token);
        var rewound = await process.Connection.GetEntriesAsync(cancellationToken: cancellation.Token);
        Assert.False(fork.Cancelled);
        Assert.Equal(2, rewound.Entries.Count);
        Assert.Equal(first.LeafId, rewound.LeafId);

        var fresh = await process.Connection.NewSessionAsync(cancellation.Token);
        var empty = await process.Connection.GetEntriesAsync(cancellationToken: cancellation.Token);
        Assert.False(fresh.Cancelled);
        Assert.Empty(empty.Entries);
        Assert.Null(empty.LeafId);
    }

    [Fact]
    public async Task NormalPromptStreamsUnicodeAndPersistsEntries()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await using var process = await FakePiTestHost.StartAsync(temporaryDirectory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await process.Connection.PromptAsync("Say hello", cancellation.Token);
        var events = await FakePiTestHost.ReadUntilSettledAsync(process.Connection, cancellation.Token);
        var assembler = Assemble(events);
        var state = await process.Connection.GetStateAsync(cancellation.Token);
        var entries = await process.Connection.GetEntriesAsync(cancellationToken: cancellation.Token);

        Assert.Equal("Hello from Fake Pi 👽", assembler.Text);
        Assert.True(assembler.IsSettled);
        var usage = Assert.Single(events.OfType<PiMessageCompletedEvent>()).Usage;
        Assert.Equal(2, usage?.TotalTokens);
        Assert.Equal(2, entries.Entries.Count);
        Assert.Equal(2, state.MessageCount);
        Assert.NotNull(state.SessionFile);
        Assert.True(IsBelow(temporaryDirectory.GetPath("sessions"), state.SessionFile!));
    }

    [Fact]
    public async Task ModelAndThinkingCapabilitiesCanBeReadAndApplied()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await using var process = await FakePiTestHost.StartAsync(temporaryDirectory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var initial = await process.Connection.GetStateAsync(cancellation.Token);
        var models = await process.Connection.GetAvailableModelsAsync(cancellation.Token);
        var initialLevels = await process.Connection.GetAvailableThinkingLevelsAsync(cancellation.Token);
        var selected = await process.Connection.SetModelAsync(
            "fake",
            "fake-standard",
            cancellation.Token);
        await process.Connection.SetThinkingLevelAsync("high", cancellation.Token);
        var updated = await process.Connection.GetStateAsync(cancellation.Token);

        Assert.Equal("fake-standard", initial.Model?.ModelId);
        Assert.Equal(100_000, initial.Model?.ContextWindow);
        Assert.Equal("off", initial.ThinkingLevel);
        Assert.Equal(2, models.Count);
        Assert.Contains(models, static model => model.ModelId == "fake-fast" && !model.SupportsReasoning);
        Assert.All(models, static model => Assert.Equal(100_000, model.ContextWindow));
        Assert.Contains("high", initialLevels);
        Assert.Equal("Fake Standard", selected.DisplayName);
        Assert.Equal("fake-standard", updated.Model?.ModelId);
        Assert.Equal("high", updated.ThinkingLevel);
    }

    [Fact]
    public async Task ComposerCommandsCompactionAndSessionNamesUseNativeRpcCommands()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await using var process = await FakePiTestHost.StartAsync(temporaryDirectory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var commands = await process.Connection.GetCommandsAsync(cancellation.Token);
        await process.Connection.SetSessionNameAsync("Generated thread title", cancellation.Token);
        var compaction = await process.Connection.CompactAsync("Keep decisions", cancellation.Token);
        var state = await process.Connection.GetStateAsync(cancellation.Token);

        Assert.Collection(
            commands,
            command => Assert.Equal(("review", "extension"), (command.Name, command.Source)),
            command => Assert.Equal(("release-notes", "prompt"), (command.Name, command.Source)),
            command => Assert.Equal(("skill:fake-skill", "skill"), (command.Name, command.Source)));
        Assert.Equal("user", commands[2].SourceInfo?.Scope);
        Assert.EndsWith("SKILL.md", commands[2].Path);
        Assert.Equal("Generated thread title", state.SessionName);
        Assert.True(state.AutoCompactionEnabled);
        Assert.Equal("Fake compacted context summary.", compaction.Summary);
        Assert.Equal(1200, compaction.TokensBefore);
        Assert.Equal(320, compaction.EstimatedTokensAfter);
        Assert.Equal(1330, compaction.Usage?.TotalTokens);
        Assert.Equal(0.0125m, compaction.Usage?.TotalCost);

        var log = File.ReadLines(Path.Combine(temporaryDirectory.GetPath("sessions"), "command-log.jsonl"))
            .Select(static line => JsonNode.Parse(line) as JsonObject)
            .Where(static record => record is not null)
            .ToArray();
        Assert.Contains(log, static record => record!["command"]?.GetValue<string>() == "get_commands");
        Assert.Contains(log, static record => record!["command"]?.GetValue<string>() == "compact" &&
            record["request"]?["customInstructions"]?.GetValue<string>() == "Keep decisions");
        Assert.Contains(log, static record => record!["command"]?.GetValue<string>() == "set_session_name" &&
            record["request"]?["name"]?.GetValue<string>() == "Generated thread title");
    }

    [Fact]
    public async Task PromptSendsNativeImagesAndAPathManifestForEveryAttachment()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var imageBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        var imagePath = temporaryDirectory.GetPath("pixel.png");
        var notesPath = temporaryDirectory.GetPath("notes.txt");
        await File.WriteAllBytesAsync(imagePath, imageBytes);
        await File.WriteAllTextAsync(notesPath, "attachment notes");
        await using var process = await FakePiTestHost.StartAsync(temporaryDirectory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        PiPromptAttachment[] attachments =
        [
            new("attachment-image", "pixel.png", "image/png", imagePath, new string('A', 64)),
            new("attachment-notes", "notes.txt", "text/plain", notesPath, new string('B', 64)),
        ];

        await process.Connection.PromptAsync("Inspect these", attachments, cancellation.Token);
        await FakePiTestHost.ReadUntilSettledAsync(process.Connection, cancellation.Token);

        var promptRecord = File.ReadLines(Path.Combine(
                temporaryDirectory.GetPath("sessions"),
                "command-log.jsonl"))
            .Select(static line => JsonNode.Parse(line) as JsonObject)
            .Single(static record => record?["command"]?.GetValue<string>() == "prompt")!;
        var message = promptRecord["message"]!.GetValue<string>();
        var images = Assert.IsType<JsonArray>(promptRecord["images"]);
        var image = Assert.IsType<JsonObject>(Assert.Single(images));
        var manifest = ReadAttachmentManifest(message);
        var manifestAttachments = Assert.IsType<JsonArray>(manifest["attachments"]);

        Assert.StartsWith("Inspect these", message, StringComparison.Ordinal);
        Assert.Contains("<pistation_attachments>", message, StringComparison.Ordinal);
        Assert.Equal(imagePath, manifestAttachments[0]?["path"]?.GetValue<string>());
        Assert.Equal(notesPath, manifestAttachments[1]?["path"]?.GetValue<string>());
        Assert.Equal("image/png", image["mimeType"]?.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(imageBytes), image["data"]?.GetValue<string>());
        Assert.Equal(
            "Inspect these\n\n[Attached: pixel.png, notes.txt]",
            PiPromptFormatter.NormalizePersistedMessage(message));
    }

    [Fact]
    public async Task ToolUpdatesReplaceCumulativeOutput()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await using var process = await FakePiTestHost.StartAsync(temporaryDirectory, "tool");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var assembler = new PiStreamAssembler();
        var observedOutputs = new List<string>();

        await process.Connection.PromptAsync("Use a tool", cancellation.Token);
        await foreach (var @event in process.Connection.ReadEventsAsync(cancellation.Token))
        {
            assembler.Apply(@event);
            if (@event is PiToolExecutionUpdatedEvent)
            {
                observedOutputs.Add(assembler.ToolExecutions["call-1"].Output);
            }

            if (@event is PiAgentSettledEvent)
            {
                break;
            }
        }

        Assert.Equal(["one", "one\ntwo"], observedOutputs);
        var tool = assembler.ToolExecutions["call-1"];
        Assert.Equal("one\ntwo\ncomplete", tool.Output);
        Assert.Equal(PiToolExecutionStatus.Completed, tool.Status);
        Assert.Equal("Tool finished.", assembler.Text);
    }

    [Fact]
    public async Task BlockingExtensionDialogIsForwardedAndCanBeCancelled()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await using var process = await FakePiTestHost.StartAsync(temporaryDirectory, "dialog");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await process.Connection.PromptAsync("Trigger dialog", cancellation.Token);
        var events = new List<PiRpcEvent>();
        await foreach (var @event in process.Connection.ReadEventsAsync(cancellation.Token))
        {
            events.Add(@event);
            if (@event is PiConfirmRequestedEvent request)
            {
                Assert.Equal("dialog-1", request.RequestId);
                Assert.Equal("Allow operation?", request.Title);
                await process.Connection.CancelExtensionUiAsync(request.RequestId, cancellation.Token);
            }

            if (@event is PiAgentSettledEvent)
            {
                break;
            }
        }

        Assert.Contains(events, static @event => @event is PiConfirmRequestedEvent);
        Assert.Contains(events, static @event => @event is PiAgentSettledEvent);
        Assert.True(File.Exists(System.IO.Path.Combine(
            temporaryDirectory.GetPath("sessions"),
            "dialog-cancelled.txt")));
    }

    [Fact]
    public async Task StopClearsQueueThenAbortsAndWaitsForSettlement()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await using var process = await FakePiTestHost.StartAsync(temporaryDirectory, "stop");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await process.Connection.PromptAsync("Retry forever", cancellation.Token);
        var beforeStop = new List<PiRpcEvent>();
        await foreach (var @event in process.Connection.ReadEventsAsync(cancellation.Token))
        {
            beforeStop.Add(@event);
            if (@event is PiAutoRetryStartedEvent)
            {
                break;
            }
        }

        var cleared = await process.Connection.StopAsync(cancellation.Token);
        var afterStop = await FakePiTestHost.ReadUntilSettledAsync(process.Connection, cancellation.Token);
        var commands = File.ReadLines(System.IO.Path.Combine(
                temporaryDirectory.GetPath("sessions"),
                "command-log.jsonl"))
            .Select(static line => JsonNode.Parse(line)?["command"]?.GetValue<string>())
            .Where(static command => command is not null)
            .ToArray();

        Assert.Single(cleared.Steering);
        Assert.Single(cleared.FollowUp);
        Assert.Contains(beforeStop, static @event => @event is PiAutoRetryStartedEvent);
        Assert.Contains(afterStop, static @event => @event is PiAgentSettledEvent);
        Assert.True(Array.IndexOf(commands, "clear_queue") < Array.IndexOf(commands, "abort"));
        Assert.DoesNotContain("abort_retry", commands);
    }

    [Fact]
    public async Task ActiveTurnMessagesExposeQueueUpdatesModesAndClearing()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await using var process = await FakePiTestHost.StartAsync(temporaryDirectory, "queue");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await process.Connection.PromptAsync("Start working", cancellation.Token);
        await ReadUntilAsync<PiTurnStartedEvent>(process.Connection, cancellation.Token);
        await process.Connection.SteerAsync("Change direction", [], cancellation.Token);
        var steered = await ReadUntilAsync<PiQueueUpdatedEvent>(process.Connection, cancellation.Token);
        await process.Connection.FollowUpAsync("Then summarize", [], cancellation.Token);
        var followed = await ReadUntilAsync<PiQueueUpdatedEvent>(process.Connection, cancellation.Token);
        var queuedState = await process.Connection.GetStateAsync(cancellation.Token);

        await process.Connection.SetSteeringModeAsync("one-at-a-time", cancellation.Token);
        await process.Connection.SetFollowUpModeAsync("one-at-a-time", cancellation.Token);
        var configuredState = await process.Connection.GetStateAsync(cancellation.Token);
        var cleared = await process.Connection.ClearQueueAsync(cancellation.Token);
        var clearedUpdate = await ReadUntilAsync<PiQueueUpdatedEvent>(process.Connection, cancellation.Token);

        Assert.Equal(["Change direction"], steered.Steering);
        Assert.Empty(steered.FollowUp);
        Assert.Equal(["Change direction"], followed.Steering);
        Assert.Equal(["Then summarize"], followed.FollowUp);
        Assert.True(queuedState.IsStreaming);
        Assert.Equal(2, queuedState.PendingMessageCount);
        Assert.Equal("all", queuedState.SteeringMode);
        Assert.Equal("all", queuedState.FollowUpMode);
        Assert.Equal("one-at-a-time", configuredState.SteeringMode);
        Assert.Equal("one-at-a-time", configuredState.FollowUpMode);
        Assert.Equal(["Change direction"], cleared.Steering);
        Assert.Equal(["Then summarize"], cleared.FollowUp);
        Assert.Empty(clearedUpdate.Steering);
        Assert.Empty(clearedUpdate.FollowUp);

        await process.Connection.AbortAsync(cancellation.Token);
        await FakePiTestHost.ReadUntilSettledAsync(process.Connection, cancellation.Token);
    }

    [Fact]
    public async Task StopCancelsRetryDelayWhenAbortDoesNotRespond()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var timeouts = new PiRpcConnectionOptions
        {
            DefaultCommandTimeout = TimeSpan.FromSeconds(1),
            LongRunningCommandTimeout = TimeSpan.FromSeconds(5),
        };
        await using var process = await FakePiTestHost.StartAsync(
            temporaryDirectory,
            "stop-retry-delay",
            connectionOptions: timeouts);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await process.Connection.PromptAsync("Retry forever", cancellation.Token);
        await foreach (var @event in process.Connection.ReadEventsAsync(cancellation.Token))
        {
            if (@event is PiAutoRetryStartedEvent)
            {
                break;
            }
        }

        await process.Connection.StopAsync(cancellation.Token);
        var events = await FakePiTestHost.ReadUntilSettledAsync(process.Connection, cancellation.Token);
        var commands = File.ReadLines(System.IO.Path.Combine(
                temporaryDirectory.GetPath("sessions"),
                "command-log.jsonl"))
            .Select(static line => JsonNode.Parse(line)?["command"]?.GetValue<string>())
            .Where(static command => command is not null)
            .ToArray();

        Assert.Contains(events, static @event => @event is PiAgentSettledEvent);
        Assert.Equal(
            ["clear_queue", "abort", "abort_retry", "abort"],
            commands.Where(static command => command is "clear_queue" or "abort" or "abort_retry"));
    }

    [Fact]
    public async Task SessionCanResumeAndStaleCursorRequestsFullHydrationFallback()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using (var first = await FakePiTestHost.StartAsync(
                         temporaryDirectory,
                         sessionId: "resume-session",
                         cancellationToken: cancellation.Token))
        {
            await first.Connection.PromptAsync("first", cancellation.Token);
            await FakePiTestHost.ReadUntilSettledAsync(first.Connection, cancellation.Token);
        }

        await using var second = await FakePiTestHost.StartAsync(
            temporaryDirectory,
            sessionId: "resume-session",
            cancellationToken: cancellation.Token);
        var hydrated = await second.Connection.GetEntriesAsync(cancellationToken: cancellation.Token);
        Assert.Equal(2, hydrated.Entries.Count);

        await second.Connection.PromptAsync("second", cancellation.Token);
        await FakePiTestHost.ReadUntilSettledAsync(second.Connection, cancellation.Token);
        var allEntries = await second.Connection.GetEntriesAsync(cancellationToken: cancellation.Token);
        Assert.Equal(4, allEntries.Entries.Count);

        var staleCursor = await Assert.ThrowsAsync<PiRpcCommandException>(() =>
            second.Connection.GetEntriesAsync("missing-entry", cancellation.Token));
        Assert.Equal("get_entries", staleCursor.Command);
    }

    [Fact]
    public async Task ResponsesAreCorrelatedWhenTheyArriveOutOfOrder()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await using var process = await FakePiTestHost.StartAsync(temporaryDirectory, "out-of-order");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var entriesTask = process.Connection.GetEntriesAsync(cancellationToken: cancellation.Token);
        var textTask = process.Connection.SendCommandAsync(
            "get_last_assistant_text",
            cancellationToken: cancellation.Token);
        await Task.WhenAll(entriesTask, textTask);
        var entries = await entriesTask;
        var text = await textTask;

        Assert.Empty(entries.Entries);
        Assert.Equal("latest", text.Data.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("parse-error")]
    [InlineData("malformed-json")]
    [InlineData("invalid-utf8")]
    [InlineData("unterminated-jsonl")]
    public async Task StartupFailsClosedForBrokenProtocol(string scenario)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var exception = await Assert.ThrowsAsync<PiRpcConnectionException>(() =>
            FakePiTestHost.StartAsync(
                temporaryDirectory,
                scenario,
                connectionOptions: ShortTimeouts(),
                cancellationToken: cancellation.Token));

        Assert.Contains("did not become ready", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(exception.InnerException);
    }

    [Fact]
    public async Task ProcessCrashFaultsConnectionAndRetainsStderr()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await using var process = await FakePiTestHost.StartAsync(temporaryDirectory, "crash");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await process.Connection.PromptAsync("crash", cancellation.Token);
        await Assert.ThrowsAsync<PiRpcConnectionException>(async () =>
            await process.Connection.Completion.WaitAsync(cancellation.Token));
        await process.Exit.WaitAsync(cancellation.Token);

        Assert.Contains("crashed after prompt", process.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CommandsUsePerCommandTimeouts()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var options = new PiRpcConnectionOptions
        {
            DefaultCommandTimeout = TimeSpan.FromSeconds(1),
            LongRunningCommandTimeout = TimeSpan.FromMilliseconds(1500),
        };
        await using var process = await FakePiTestHost.StartAsync(
            temporaryDirectory,
            "command-timeout",
            connectionOptions: options);

        var compact = await Assert.ThrowsAsync<PiRpcTimeoutException>(() =>
            process.Connection.SendCommandAsync("compact"));
        var prompt = await Assert.ThrowsAsync<PiRpcTimeoutException>(() =>
            process.Connection.PromptAsync("never answer"));
        var ordinary = await Assert.ThrowsAsync<PiRpcTimeoutException>(() =>
            process.Connection.SendCommandAsync("get_last_assistant_text"));

        Assert.Equal(options.LongRunningCommandTimeout, compact.Timeout);
        Assert.Equal(options.LongRunningCommandTimeout, prompt.Timeout);
        Assert.Equal(options.DefaultCommandTimeout, ordinary.Timeout);
    }

    [Fact]
    public async Task StandardErrorCaptureIsBoundedToNewestCharacters()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await using var process = await FakePiTestHost.StartAsync(
            temporaryDirectory,
            "stderr-flood",
            standardErrorCharacterLimit: 1024);

        await WaitUntilAsync(
            () => process.StandardError.Contains("stderr-tail-marker", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        Assert.True(process.StandardError.Length <= 1024);
        Assert.EndsWith("stderr-tail-marker" + Environment.NewLine, process.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposingAnOwnedProcessWaitsForItsExit()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var process = await FakePiTestHost.StartAsync(temporaryDirectory);

        await process.DisposeAsync();

        Assert.True(process.Exit.IsCompletedSuccessfully);
        Assert.NotNull(await process.Exit);
    }

    private static PiStreamAssembler Assemble(IEnumerable<PiRpcEvent> events)
    {
        var assembler = new PiStreamAssembler();
        foreach (var @event in events)
        {
            assembler.Apply(@event);
        }

        return assembler;
    }

    private static async Task<TEvent> ReadUntilAsync<TEvent>(
        PiRpcConnection connection,
        CancellationToken cancellationToken)
        where TEvent : PiRpcEvent
    {
        await foreach (var @event in connection.ReadEventsAsync(cancellationToken))
        {
            if (@event is TEvent result)
            {
                return result;
            }
        }

        throw new EndOfStreamException($"Pi exited before emitting {typeof(TEvent).Name}.");
    }

    private static PiRpcConnectionOptions ShortTimeouts() => new()
    {
        DefaultCommandTimeout = TimeSpan.FromSeconds(1),
        LongRunningCommandTimeout = TimeSpan.FromSeconds(1),
    };

    private static bool IsBelow(string parent, string candidate)
    {
        var prefix = System.IO.Path.GetFullPath(parent) + System.IO.Path.DirectorySeparatorChar;
        return System.IO.Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject ReadAttachmentManifest(string message)
    {
        const string startMarker = "\n\n<pistation_attachments>\n";
        const string endMarker = "\n</pistation_attachments>";
        var start = message.LastIndexOf(startMarker, StringComparison.Ordinal) + startMarker.Length;
        var length = message.Length - start - endMarker.Length;
        return Assert.IsType<JsonObject>(JsonNode.Parse(message.Substring(start, length)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not met before the test timeout.");
            }

            await Task.Delay(20);
        }
    }
}
