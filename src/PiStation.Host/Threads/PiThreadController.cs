using System.Diagnostics;
using System.Text.Json;
using PiStation.Host.Errors;
using PiStation.Host.Git;
using PiStation.Host.Persistence;
using PiStation.PiRpc.Decoding;
using PiStation.PiRpc.Diagnostics;
using PiStation.PiRpc.Process;
using PiStation.PiRpc.Transport;
using PiStation.PiRpc.Wire.Events;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Threads;

public sealed partial class PiThreadController : IAsyncDisposable
{
    private readonly HostDatabase _database;
    private readonly HostOptions _options;
    private readonly IPiProcessFactory _processFactory;
    private readonly ProjectDescriptor _project;
    private readonly WorkspaceCheckpointService _checkpoints;
    private readonly object _stateLock = new();
    private HostThreadRecord _thread;
    private readonly SemaphoreSlim _interactionGate = new(1, 1);
    private readonly Dictionary<InteractionId, CancellationTokenSource> _interactionTimeouts = [];
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly HashSet<(ClientId ClientId, CommandId CommandId)> _settlementReceipts = [];
    private readonly CancellationTokenSource _shutdown = new();
    private TurnId? _activeTurnId;
    private int? _activeTurnContextWindow;
    private long? _activeTurnContextTokens;
    private string? _activeTurnEntryIdBefore;
    private long? _activeTurnStartedTimestamp;
    private PiTokenUsage? _activeTurnUsage;
    private string _activeTurnProvider = "unknown";
    private string _activeTurnModel = "unknown";
    private string? _currentAssistantMessageId;
    private int _disposed;
    private long _generation;
    private Task? _eventPump;
    private string? _lastPiEntryId;
    private PiProcess? _process;

    public PiThreadController(
        HostEnvironmentRecord environment,
        ProjectDescriptor project,
        HostThreadRecord thread,
        HostDatabase database,
        IPiProcessFactory processFactory,
        WorkspaceCheckpointService checkpoints,
        HostOptions options)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _thread = thread ?? throw new ArgumentNullException(nameof(thread));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Journal = new ThreadEventJournal(
            ThreadProjectionReducer.Create(
                environment.EnvironmentId,
                thread.ThreadId,
                thread.PiSessionId,
                thread.PiSessionFile),
            options);
    }

    public ThreadEventJournal Journal { get; }

    private long _lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;
    private void TouchRuntime() => Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);

    internal async Task<bool> StopIfIdleAsync(DateTimeOffset now, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (now.UtcTicks - Interlocked.Read(ref _lastActivityTicks) < timeout.Ticks) return false;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Journal.Projection.RuntimeState != ThreadRuntimeState.Ready ||
                now.UtcTicks - Interlocked.Read(ref _lastActivityTicks) < timeout.Ticks ||
                Journal.Projection.Timeline.Any(item => item is ApprovalTimelineItem { State: InteractionState.Pending } or QuestionTimelineItem { State: InteractionState.Pending })) return false;
            Journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Stopping));
            await DisposePreviousProcessAsync().ConfigureAwait(false);
            Journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Stopped));
            return true;
        }
        finally { _lifecycle.Release(); }
    }


    public async Task ApplyRenamedThreadAsync(
        HostThreadRecord thread,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        TouchRuntime();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _thread = thread;
            if (_process is not null && Journal.Projection.RuntimeState is ThreadRuntimeState.Ready or ThreadRuntimeState.Running)
            {
                await _process.Connection.SetSessionNameAsync(thread.Title, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        TouchRuntime();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Journal.Projection.RuntimeState is ThreadRuntimeState.Ready or ThreadRuntimeState.Running)
            {
                return;
            }

            await DisposePreviousProcessAsync().ConfigureAwait(false);
            if (!Directory.Exists(_project.CanonicalPath))
            {
                throw new DirectoryNotFoundException($"Project directory no longer exists: {_project.CanonicalPath}");
            }

            Journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Starting));
            var process = await _processFactory.StartAsync(_project, _thread, cancellationToken).ConfigureAwait(false);
            _process = process;
            var generation = Interlocked.Increment(ref _generation);
            try
            {
                var state = await process.Connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(state.SessionId, _thread.PiSessionId, StringComparison.Ordinal))
                {
                    throw new PiRpcConnectionException(
                        $"Pi opened session '{state.SessionId}' instead of '{_thread.PiSessionId}'.");
                }

                ValidateSessionFile(state.SessionFile);
                var configuration = await _database.GetOrCreateThreadPiConfigurationAsync(
                    _thread.ThreadId,
                    cancellationToken).ConfigureAwait(false);
                await ApplyPersistedPiConfigurationAsync(
                    process.Connection,
                    configuration,
                    cancellationToken).ConfigureAwait(false);
                var activeState = await process.Connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(_thread.Title) &&
                    !string.Equals(activeState.SessionName, _thread.Title, StringComparison.Ordinal))
                {
                    await process.Connection.SetSessionNameAsync(_thread.Title, cancellationToken).ConfigureAwait(false);
                }
                await _database.UpdateThreadSessionFileAsync(_thread.ThreadId, state.SessionFile, cancellationToken)
                    .ConfigureAwait(false);
                Journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Hydrating));
                var entries = await process.Connection.GetEntriesAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                _lastPiEntryId = entries.LeafId;
                var checkpoints = await _checkpoints.ListAsync(_thread.ThreadId, cancellationToken)
                    .ConfigureAwait(false);
                var hydrated = ThreadProjectionReducer.Hydrate(
                    Journal.Projection,
                    entries.Entries,
                    entries.LeafId,
                    state.SessionFile,
                    activeState.Model?.ContextWindow,
                    checkpoints,
                    state.SessionId,
                    await _database.ListSentMessagesAsync(_thread.ThreadId, cancellationToken).ConfigureAwait(false)) with
                {
                    Queue = CreateQueueProjection(activeState, Journal.Projection.Queue?.Messages ?? []),
                    AgentActivities = [],
                    CompletionSequence = await _database.GetCompletionSequenceAsync(_thread.ThreadId, cancellationToken).ConfigureAwait(false),
                    Plan = await ReadPlanOnStartAsync(cancellationToken).ConfigureAwait(false),
                    AgentSetup = await ReadAgentsOnStartAsync(cancellationToken).ConfigureAwait(false),
                };
                var persistedAgentEvents = await _database.ListThreadAgentEventsAsync(
                    _thread.ThreadId,
                    cancellationToken).ConfigureAwait(false);
                foreach (var persistedEvent in persistedAgentEvents)
                {
                    hydrated = ThreadProjectionReducer.Apply(hydrated, persistedEvent);
                }

                if (!activeState.IsStreaming)
                {
                    var now = DateTimeOffset.UtcNow;
                    foreach (var orphaned in (hydrated.AgentActivities ?? []).Where(static activity =>
                                 activity.State is AgentActivityState.Pending or
                                     AgentActivityState.Running or AgentActivityState.Waiting))
                    {
                        var interrupted = new AgentActivityChangedEvent(orphaned with
                        {
                            State = AgentActivityState.Interrupted,
                            CurrentActivity = "Interrupted during host restart",
                            UpdatedUtc = now,
                            CompletedUtc = now,
                            FailureSummary = orphaned.FailureSummary ??
                                "Pi Station restarted before this activity reported completion.",
                            CanInterrupt = false,
                        });
                        await _database.AppendThreadAgentEventAsync(
                            _thread.ThreadId,
                            orphaned.TurnId,
                            CountStartedTurns(hydrated),
                            interrupted,
                            cancellationToken).ConfigureAwait(false);
                        hydrated = ThreadProjectionReducer.Apply(hydrated, interrupted);
                    }
                }

                Journal.ReplaceProjection(hydrated);
                _eventPump = PumpEventsAsync(process, generation, _shutdown.Token);
            }
            catch
            {
                await process.DisposeAsync().ConfigureAwait(false);
                _process = null;
                throw;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Journal.Commit(new RuntimeFailedEvent(ToProtocolError(exception)));
            throw;
        }
        finally
        {
            // Startup and hydration can take longer than the idle threshold. A caller
            // returning from EnsureReady owns a fresh activity window before dispatch.
            TouchRuntime();
            _lifecycle.Release();
        }
    }

    public async Task<ThreadPiConfigurationSnapshot> GetPiConfigurationAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        TouchRuntime();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var process = RequireReadyProcess();
            var configuration = await _database.GetOrCreateThreadPiConfigurationAsync(
                _thread.ThreadId,
                cancellationToken).ConfigureAwait(false);
            return await ReadPiConfigurationSnapshotAsync(
                process.Connection,
                configuration,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<ComposerDiscoveryResult> GetComposerDiscoveryAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var process = _process ?? throw new HostOperationException(
            ProtocolErrorCodes.PiRuntimeCrashed,
            "Pi is unavailable for composer discovery.");
        var commands = await process.Connection.GetCommandsAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ComposerCommandDescriptor>
        {
            new("compact", "Compact the current context, optionally with instructions.", ComposerCommandSource.BuiltIn),
            new("stash", "Save the current draft to the project prompt stash.", ComposerCommandSource.BuiltIn),
            new("background", "Start an independent task and keep a fresh draft here.", ComposerCommandSource.BuiltIn),
        };
        result.AddRange(commands.Where(static command => command.Name != PiRpcConnection.ManagementCommand && command.Name != PiRpcConnection.PlanCommand && command.Name != PiRpcConnection.AgentsCommand).Select(static command => new ComposerCommandDescriptor(
            command.Name,
            command.Description ?? command.Name,
            command.Source switch
            {
                "skill" => ComposerCommandSource.Skill,
                "prompt" => ComposerCommandSource.Prompt,
                _ => ComposerCommandSource.Extension,
            },
            command.Location,
            command.Path,
            command.SourceInfo is { } info ? new ComposerCommandSourceInfo(info.Path, info.Source, info.Scope, info.Origin, info.BaseDir) : null)));
        return new ComposerDiscoveryResult(
            result.GroupBy(static command => command.Name, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .OrderBy(static command => command.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            DateTimeOffset.UtcNow);
    }

    public async Task<ContextCompactionResult> CompactContextAsync(
        string? customInstructions,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        TouchRuntime();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var process = RequireReadyProcess();
            Journal.Commit(new ContextCompactionChangedEvent(new ContextCompactionProjection(
                ContextCompactionState.Running,
                "manual",
                null,
                null,
                null,
                null,
                null,
                DateTimeOffset.UtcNow)));
            try
            {
                var result = await process.Connection.CompactAsync(customInstructions, cancellationToken)
                    .ConfigureAwait(false);
                var projection = new ContextCompactionProjection(
                    ContextCompactionState.Completed,
                    "manual",
                    result.TokensBefore,
                    result.EstimatedTokensAfter,
                    result.Usage?.TotalCost,
                    LimitPreview(result.Summary, 2_000),
                    null,
                    DateTimeOffset.UtcNow);
                Journal.Commit(new ContextCompactionChangedEvent(projection));
                if (result.Usage is not null)
                {
                    await _database.AppendUsageAsync(
                        _thread.ThreadId,
                        _activeTurnProvider,
                        _activeTurnModel,
                        result.Usage.InputTokens,
                        result.Usage.OutputTokens,
                        result.Usage.CacheReadTokens + result.Usage.CacheWriteTokens,
                        result.Usage.TotalTokens,
                        result.Usage.TotalCost,
                        cancellationToken).ConfigureAwait(false);
                }
                return new ContextCompactionResult(
                    result.Summary,
                    result.FirstKeptEntryId,
                    result.TokensBefore,
                    result.EstimatedTokensAfter,
                    result.Usage is null ? null : new TokenCostUsage(
                        result.Usage.InputTokens,
                        result.Usage.OutputTokens,
                        result.Usage.CacheReadTokens,
                        result.Usage.CacheWriteTokens,
                        result.Usage.TotalTokens,
                        result.Usage.TotalCost));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Journal.Commit(new ContextCompactionChangedEvent(new ContextCompactionProjection(
                    ContextCompactionState.Failed,
                    "manual",
                    null,
                    null,
                    null,
                    null,
                    exception.Message,
                    DateTimeOffset.UtcNow)));
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<ThreadDescriptor> RegenerateTitleAsync(
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var firstPrompt = Journal.Projection.Messages.FirstOrDefault(static message => message.Role == MessageRole.User)?.Text;
        if (string.IsNullOrWhiteSpace(firstPrompt))
        {
            throw new HostOperationException(ProtocolErrorCodes.ThreadInvalid, "A thread needs a user turn before its title can be generated.");
        }
        var title = GenerateThreadTitle(firstPrompt);
        var update = await _database.UpdateThreadMetadataAsync(
            _thread.ThreadId, expectedRevision, title, null, null, cancellationToken).ConfigureAwait(false);
        if (!update.WasUpdated || update.Thread is null)
        {
            throw new HostOperationException(ProtocolErrorCodes.ThreadConflict, "The thread changed before its title could be regenerated.");
        }
        await _database.SetThreadTitleKindAsync(_thread.ThreadId, ThreadTitleKind.Generated, cancellationToken)
            .ConfigureAwait(false);
        _thread = update.Thread;
        if (_process is not null)
        {
            try
            {
                await _process.Connection.SetSessionNameAsync(title, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is PiRpcConnectionException or PiRpcTimeoutException)
            {
                // The title is durable in Pi Station even if the runtime exits before its session label is updated.
            }
        }
        return await _database.EnrichThreadDescriptorAsync(_thread, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ThreadPiConfiguration> UpdatePiConfigurationAsync(
        long expectedRevision,
        PiModelSelection? model,
        PiThinkingLevel? thinkingLevel,
        string? runtimeModeId,
        CancellationToken cancellationToken = default)
    {
        if (expectedRevision < 0)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.PiConfigurationConflict,
                "A Pi configuration revision cannot be negative.");
        }

        ValidateConfigurationValues(model, runtimeModeId);
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        TouchRuntime();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var process = RequireReadyProcess();
            var current = await _database.GetOrCreateThreadPiConfigurationAsync(
                _thread.ThreadId,
                cancellationToken).ConfigureAwait(false);
            if (current.Revision != expectedRevision)
            {
                throw new HostOperationException(
                    ProtocolErrorCodes.PiConfigurationConflict,
                    $"The Pi configuration changed from expected revision {expectedRevision} to " +
                    $"revision {current.Revision}; reload it before saving.");
            }

            if (runtimeModeId is not null)
            {
                throw new HostOperationException(
                    ProtocolErrorCodes.PiConfigurationUnsupported,
                    $"Pi does not advertise runtime mode '{runtimeModeId}' through its RPC capability set.");
            }

            var connection = process.Connection;
            var originalState = await connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
            var models = await connection.GetAvailableModelsAsync(cancellationToken).ConfigureAwait(false);
            if (model is not null && !models.Any(candidate =>
                    string.Equals(candidate.ProviderId, model.ProviderId, StringComparison.Ordinal) &&
                    string.Equals(candidate.ModelId, model.ModelId, StringComparison.Ordinal)))
            {
                throw new HostOperationException(
                    ProtocolErrorCodes.PiConfigurationUnsupported,
                    $"Pi does not advertise model '{model.ProviderId}/{model.ModelId}' for this thread.");
            }

            try
            {
                if (model is not null && !Matches(originalState.Model, model))
                {
                    await connection.SetModelAsync(
                        model.ProviderId,
                        model.ModelId,
                        cancellationToken).ConfigureAwait(false);
                }

                var activeState = await connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
                var availableThinkingLevels = await connection.GetAvailableThinkingLevelsAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (thinkingLevel is not null)
                {
                    var requestedLevel = ToPiThinkingLevel(thinkingLevel.Value);
                    if (!availableThinkingLevels.Contains(requestedLevel, StringComparer.Ordinal))
                    {
                        throw new HostOperationException(
                            ProtocolErrorCodes.PiConfigurationUnsupported,
                            $"Pi model '{activeState.Model?.ProviderId}/{activeState.Model?.ModelId}' does not " +
                            $"advertise thinking level '{requestedLevel}'.");
                    }

                    if (!string.Equals(activeState.ThinkingLevel, requestedLevel, StringComparison.Ordinal))
                    {
                        await connection.SetThinkingLevelAsync(requestedLevel, cancellationToken).ConfigureAwait(false);
                    }
                }

                var update = await _database.UpdateThreadPiConfigurationAsync(
                    _thread.ThreadId,
                    expectedRevision,
                    model,
                    thinkingLevel,
                    runtimeModeId,
                    cancellationToken).ConfigureAwait(false);
                if (!update.WasUpdated || update.Configuration is null)
                {
                    throw new HostOperationException(
                        ProtocolErrorCodes.PiConfigurationConflict,
                        "The Pi configuration changed while it was being saved; reload it before retrying.");
                }

                return update.Configuration;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await TryRestoreActivePiConfigurationAsync(connection, originalState).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public Task<TurnId> StartTurnAsync(string prompt, IReadOnlyList<PiPromptAttachment> attachments,
        ClientId clientId, CommandId commandId, CancellationToken cancellationToken = default) =>
        StartTurnCoreAsync(prompt, attachments, clientId, commandId, null, cancellationToken);

    private async Task<TurnId> StartTurnCoreAsync(
        string prompt,
        IReadOnlyList<PiPromptAttachment> attachments,
        ClientId clientId,
        CommandId commandId,
        long? approvedPlanRevision,
        CancellationToken cancellationToken,
        PiAgentWorkflow? agentWorkflow = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(attachments);
        if (string.IsNullOrWhiteSpace(prompt) && attachments.Count == 0)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.PiCommandRejected,
                "A turn must contain text or at least one attachment.");
        }

        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        TouchRuntime();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Journal.Projection.RuntimeState != ThreadRuntimeState.Ready || _process is null)
            {
                throw new HostOperationException(ProtocolErrorCodes.ThreadBusy, "The thread is not ready for a new turn.");
            }

            // Resolve resources before creating a turn or consuming its draft.
            if (agentWorkflow is not null) await ValidateAgentWorkflowAsync(agentWorkflow, cancellationToken).ConfigureAwait(false);
            var preparedPrompt = await _process.Connection.PreparePromptAsync(prompt, cancellationToken).ConfigureAwait(false);
            if (approvedPlanRevision is { } planRevision)
                await ApplyPlanCommandAsync(new("execute", planRevision), cancellationToken).ConfigureAwait(false);
            var firstToken = prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var isExtensionCommand = firstToken?.StartsWith('/') == true &&
                (await _process.Connection.GetCommandsAsync(cancellationToken).ConfigureAwait(false))
                    .Any(command => command.Source == "extension" && command.Name == firstToken[1..]);
            var state = await _process.Connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
            var turnId = TurnId.New();
            var turnCount = CountStartedTurns(Journal.Projection) + 1;
            try
            {
                await _checkpoints.EnsureBaselineAsync(
                    _project,
                    _thread.ThreadId,
                    turnCount - 1,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Checkpoint failures are surfaced beside the settled turn without blocking Pi's coding loop.
            }

            lock (_stateLock)
            {
                _activeTurnId = turnId;
                _activeTurnContextWindow = state.Model?.ContextWindow;
                _activeTurnContextTokens = null;
                _activeTurnEntryIdBefore = _lastPiEntryId;
                _activeTurnStartedTimestamp = Stopwatch.GetTimestamp();
                _activeTurnUsage = null;
                _activeTurnProvider = state.Model?.ProviderId ?? "unknown";
                _activeTurnModel = state.Model?.ModelId ?? "unknown";
                _currentAssistantMessageId = null;
                _settlementReceipts.Add((clientId, commandId));
            }

            var sentMessages = await _database.ListSentMessagesAsync(_thread.ThreadId, cancellationToken).ConfigureAwait(false);
            var sentContent = SentMessageReference.Read(prompt) is { } reference && sentMessages.TryGetValue(reference, out var saved)
                ? saved : null;
            await _database.RecordSettlementActivityAsync(_thread.ThreadId, true, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            Journal.Commit(new TurnStartedEvent(
                turnId,
                sentContent?.Text ?? PiPromptFormatter.CreateDisplayMessage(prompt, attachments), sentContent));
            // Finish title RPCs while Pi is idle. Some runtimes defer later
            // commands until the prompt ends, which would delay its receipt
            // and leave the desktop's Stop action disabled for the whole turn.
            await TryGenerateAutomaticTitleAsync(SentMessageReference.Remove(prompt), cancellationToken).ConfigureAwait(false);
            await _process.Connection.PromptPreparedAsync(preparedPrompt, attachments, cancellationToken).ConfigureAwait(false);
            await _database.UpdateReceiptStateAsync(
                clientId,
                commandId,
                CommandReceiptState.Accepted,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await _database.SetThreadSettlementAutomaticallyAsync(_thread.ThreadId, false, cancellationToken)
                .ConfigureAwait(false);
            if (isExtensionCommand) _ = ObserveExtensionCommandAsync(_process, turnId);
            return turnId;
        }
        catch
        {
            lock (_stateLock)
            {
                _settlementReceipts.Remove((clientId, commandId));
            }

            if (approvedPlanRevision is not null || agentWorkflow is not null)
            {
                // Revoke a possibly delivered approval by closing the owned runtime.
                // Its persisted executing state reopens paused and requires a new approval.
                await DisposePreviousProcessAsync().ConfigureAwait(false);
                Journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Stopped));
            }

            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopTurnAsync(
        ClientId clientId,
        CommandId commandId,
        CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (process is null || Journal.Projection.RuntimeState != ThreadRuntimeState.Running)
        {
            throw new HostOperationException(ProtocolErrorCodes.ThreadBusy, "The thread has no running turn to stop.");
        }

        lock (_stateLock)
        {
            _settlementReceipts.Add((clientId, commandId));
        }

        Journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Stopping));
        try
        {
            await process.Connection.StopAsync(cancellationToken).ConfigureAwait(false);
            CommitQueue([], [], QueueDeliveryState.Cleared);
            await _database.UpdateReceiptStateAsync(
                clientId,
                commandId,
                CommandReceiptState.Accepted,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_stateLock)
            {
                _settlementReceipts.Remove((clientId, commandId));
            }

            throw;
        }
    }

    public async Task QueueMessageAsync(
        QueuedMessageKind kind,
        string prompt,
        IReadOnlyList<PiPromptAttachment> attachments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(attachments);
        if (string.IsNullOrWhiteSpace(prompt) && attachments.Count == 0)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.PiCommandRejected,
                "A queued message must contain text or at least one attachment.");
        }

        var process = _process;
        if (process is null || Journal.Projection.RuntimeState != ThreadRuntimeState.Running)
        {
            throw new HostOperationException(ProtocolErrorCodes.ThreadBusy, "The thread has no active turn to steer.");
        }

        var state = await process.Connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (!state.IsStreaming || Journal.Projection.CurrentTurnId is null)
        {
            throw new HostOperationException(ProtocolErrorCodes.ThreadBusy, "The active turn settled before the message could be queued.");
        }

        await _database.RecordSettlementActivityAsync(_thread.ThreadId, true, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        if (kind == QueuedMessageKind.Steering)
        {
            await process.Connection.SteerAsync(prompt, attachments, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await process.Connection.FollowUpAsync(prompt, attachments, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ClearQueueAsync(CancellationToken cancellationToken = default)
    {
        var process = await RequireQueueProcessAsync(cancellationToken).ConfigureAwait(false);
        await process.Connection.ClearQueueAsync(cancellationToken).ConfigureAwait(false);
        CommitQueue([], [], QueueDeliveryState.Cleared);
    }

    public async Task RefreshQueueAsync(CancellationToken cancellationToken = default)
    {
        var process = await RequireQueueProcessAsync(cancellationToken).ConfigureAwait(false);
        var state = await process.Connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
        Journal.Commit(new QueueStateChangedEvent(CreateQueueProjection(
            state,
            Journal.Projection.Queue?.Messages ?? [])));
    }

    public async Task SetQueueDeliveryModeAsync(
        QueuedMessageKind kind,
        QueueDeliveryMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(mode))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.PiCommandRejected,
                "The requested queue kind or delivery mode is invalid.");
        }

        var process = await RequireQueueProcessAsync(cancellationToken).ConfigureAwait(false);
        var piMode = mode == QueueDeliveryMode.All ? "all" : "one-at-a-time";
        if (kind == QueuedMessageKind.Steering)
        {
            await process.Connection.SetSteeringModeAsync(piMode, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await process.Connection.SetFollowUpModeAsync(piMode, cancellationToken).ConfigureAwait(false);
        }

        await RefreshQueueAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> InterruptAgentAsync(
        string activityId,
        ClientId clientId,
        CommandId commandId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        if (activityId.Length > 512)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.PiCommandRejected,
                "The agent activity identifier is too long.");
        }

        var activity = (Journal.Projection.AgentActivities ?? [])
            .FirstOrDefault(candidate => candidate.ActivityId == activityId);
        if (activity is null || !activity.CanInterrupt)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.PiCommandRejected,
                "That agent activity is no longer interruptible.");
        }

        if (activity.ControlId is { Length: 32 } controlId)
        {
            await StopChildAsync(controlId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        // External extensions without a child handle retain parent-turn cancellation.
        await StopTurnAsync(clientId, commandId, cancellationToken).ConfigureAwait(false);
        return false;
    }

    public async Task RestartRuntimeAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Journal.Projection.RuntimeState is ThreadRuntimeState.Running or ThreadRuntimeState.Stopping ||
                Journal.Projection.Timeline.Any(item => item is ApprovalTimelineItem { State: InteractionState.Pending } or QuestionTimelineItem { State: InteractionState.Pending }))
                throw new HostOperationException(ProtocolErrorCodes.ThreadBusy, "Finish or stop the active work before restarting Pi.");
            await DisposePreviousProcessAsync().ConfigureAwait(false);
            Journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Stopped));
        }
        finally { _lifecycle.Release(); }
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RevertToCheckpointAsync(
        int turnCount,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        TouchRuntime();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Journal.Projection.RuntimeState != ThreadRuntimeState.Ready || _process is null)
            {
                throw new HostOperationException(
                    ProtocolErrorCodes.ThreadBusy,
                    "Stop the running turn before reverting a checkpoint.");
            }

            var completedTurnCount = CountSettledTurns(Journal.Projection);
            if (turnCount < 0 || turnCount >= completedTurnCount)
            {
                throw new HostOperationException(
                    ProtocolErrorCodes.CheckpointInvalid,
                    "A checkpoint revert must target an earlier completed turn.");
            }

            var checkpoints = await _checkpoints.ListAsync(_thread.ThreadId, cancellationToken)
                .ConfigureAwait(false);
            var source = checkpoints.SingleOrDefault(checkpoint => checkpoint.TurnCount == turnCount + 1);
            if (source is null ||
                source.Status != ThreadCheckpointStatus.Ready ||
                turnCount > 0 && string.IsNullOrWhiteSpace(source.PiEntryIdBeforeTurn))
            {
                throw new HostOperationException(
                    ProtocolErrorCodes.CheckpointUnavailable,
                    $"Turn {turnCount + 1} has no checkpoint that can rewind both the workspace and Pi conversation.");
            }

            var recoveryRef = await _checkpoints.CaptureRecoveryRefAsync(
                _project,
                _thread.ThreadId,
                cancellationToken).ConfigureAwait(false);
            var workspaceRestored = false;
            var piRewound = false;
            try
            {
                await _checkpoints.RestoreAsync(
                    _project,
                    _thread.ThreadId,
                    turnCount,
                    cancellationToken).ConfigureAwait(false);
                workspaceRestored = true;

                var mutation = turnCount == 0
                    ? await _process.Connection.NewSessionAsync(cancellationToken).ConfigureAwait(false)
                    : await _process.Connection.ForkAsync(
                        source.PiEntryIdBeforeTurn!,
                        cancellationToken).ConfigureAwait(false);
                if (mutation.Cancelled)
                {
                    throw new PiRpcConnectionException("Pi cancelled the conversation rewind.");
                }

                piRewound = true;

                var state = await _process.Connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
                ValidateSessionFile(state.SessionFile);
                var entries = await _process.Connection.GetEntriesAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                _lastPiEntryId = entries.LeafId;
                await _database.UpdateThreadSessionAsync(
                    _thread.ThreadId,
                    state.SessionId,
                    state.SessionFile,
                    cancellationToken).ConfigureAwait(false);
                _thread = _thread with
                {
                    PiSessionId = state.SessionId,
                    PiSessionFile = state.SessionFile,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                };

                await _checkpoints.DeleteFutureAsync(
                    _project,
                    _thread.ThreadId,
                    turnCount,
                    cancellationToken).ConfigureAwait(false);
                await _database.DeleteThreadAgentEventsAfterTurnAsync(
                    _thread.ThreadId,
                    turnCount,
                    cancellationToken).ConfigureAwait(false);
                var retained = await _checkpoints.ListAsync(_thread.ThreadId, cancellationToken)
                    .ConfigureAwait(false);
                var rewound = ThreadProjectionReducer.Hydrate(
                    Journal.Projection,
                    entries.Entries,
                    entries.LeafId,
                    state.SessionFile,
                    state.Model?.ContextWindow,
                    retained,
                    state.SessionId,
                    await _database.ListSentMessagesAsync(_thread.ThreadId, cancellationToken).ConfigureAwait(false)) with { AgentActivities = [], Plan = await ReadPlanOnStartAsync(cancellationToken).ConfigureAwait(false) };
                var retainedAgentEvents = await _database.ListThreadAgentEventsAsync(
                    _thread.ThreadId,
                    cancellationToken).ConfigureAwait(false);
                foreach (var retainedAgentEvent in retainedAgentEvents)
                {
                    rewound = ThreadProjectionReducer.Apply(rewound, retainedAgentEvent);
                }

                Journal.ReplaceProjection(rewound);
            }
            catch (Exception exception)
            {
                if (workspaceRestored && !piRewound)
                {
                    try
                    {
                        await _checkpoints.RestoreRefAsync(
                            _project,
                            _thread.ThreadId,
                            recoveryRef,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception restoreException) when (restoreException is not OperationCanceledException)
                    {
                    }
                }

                if (exception is OperationCanceledException)
                {
                    throw;
                }

                throw new HostOperationException(
                    ProtocolErrorCodes.CheckpointRewindFailed,
                    $"The checkpoint rewind could not be completed: {exception.Message}");
            }
            finally
            {
                try
                {
                    await _checkpoints.DeleteRefsAsync(
                        _project,
                        _thread.ThreadId,
                        [recoveryRef],
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupException) when (cleanupException is not OperationCanceledException)
                {
                }
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task RespondToApprovalAsync(
        InteractionId interactionId,
        ApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        await _interactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var approval = RequirePendingInteraction<ApprovalTimelineItem>(interactionId);
            var process = RequireInteractionProcess();
            await process.Connection.RespondToExtensionConfirmAsync(
                interactionId.Value,
                decision == ApprovalDecision.Approve,
                cancellationToken).ConfigureAwait(false);
            ResolveInteraction(new InteractionResolvedEvent(
                approval.InteractionId,
                decision == ApprovalDecision.Approve ? InteractionState.Approved : InteractionState.Rejected,
                decision));
        }
        finally
        {
            _interactionGate.Release();
        }
    }

    public async Task AnswerQuestionAsync(
        InteractionId interactionId,
        string answer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        await _interactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var question = RequirePendingInteraction<QuestionTimelineItem>(interactionId);
            if (question.InputKind == QuestionInputKind.Select &&
                !question.Options.Contains(answer, StringComparer.Ordinal))
            {
                throw new HostOperationException(
                    ProtocolErrorCodes.InvalidInteractionResponse,
                    "The selected answer is not one of the options offered by Pi.");
            }

            var process = RequireInteractionProcess();
            await process.Connection.RespondToExtensionTextAsync(
                interactionId.Value,
                answer,
                cancellationToken).ConfigureAwait(false);
            ResolveInteraction(new InteractionResolvedEvent(
                question.InteractionId,
                InteractionState.Answered,
                Answer: answer));
        }
        finally
        {
            _interactionGate.Release();
        }
    }

    public async Task CancelInteractionAsync(
        InteractionId interactionId,
        CancellationToken cancellationToken = default)
    {
        await _interactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var interaction = RequirePendingInteraction<TimelineItem>(interactionId);
            var process = RequireInteractionProcess();
            await process.Connection.CancelExtensionUiAsync(interactionId.Value, cancellationToken).ConfigureAwait(false);
            ResolveInteraction(new InteractionResolvedEvent(
                interactionId,
                InteractionState.Canceled));
        }
        finally
        {
            _interactionGate.Release();
        }
    }

    public IAsyncEnumerable<ThreadEnvelope> SubscribeAsync(
        ThreadCursor? cursor,
        CancellationToken cancellationToken = default) => Journal.SubscribeAsync(cursor, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        lock (_stateLock)
        {
            foreach (var timeout in _interactionTimeouts.Values)
            {
                timeout.Cancel();
                timeout.Dispose();
            }

            _interactionTimeouts.Clear();
        }
        var process = Interlocked.Exchange(ref _process, null);
        if (process is not null)
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }

        if (_eventPump is not null)
        {
            try
            {
                await _eventPump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
        _interactionGate.Dispose();
        _lifecycle.Dispose();
    }

    private async Task PumpEventsAsync(PiProcess process, long generation, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var @event in process.Connection.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (generation != Volatile.Read(ref _generation))
                {
                    continue;
                }

                await ApplyPiEventAsync(@event).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _generation) && Volatile.Read(ref _disposed) == 0)
            {
                await FailRuntimeAsync(exception).ConfigureAwait(false);
            }
        }
    }

    private async Task FailRuntimeAsync(Exception exception)
    {
        var error = ToProtocolError(exception);
        await FinalizeActiveAgentActivitiesAsync(
            AgentActivityState.Failed,
            $"Pi runtime failed: {error.Message}").ConfigureAwait(false);
        CommitQueue([], [], QueueDeliveryState.Cleared);
        (ClientId ClientId, CommandId CommandId)[] receipts;
        lock (_stateLock)
        {
            _activeTurnId = null;
            ClearActiveTurnMetrics();
            _currentAssistantMessageId = null;
            receipts = [.. _settlementReceipts];
            _settlementReceipts.Clear();
        }

        Journal.Commit(new RuntimeFailedEvent(error));
        foreach (var receipt in receipts)
        {
            await _database.UpdateReceiptStateAsync(
                receipt.ClientId,
                receipt.CommandId,
                CommandReceiptState.Failed,
                error.Code).ConfigureAwait(false);
        }
    }

    private async Task DisposePreviousProcessAsync()
    {
        var previous = Interlocked.Exchange(ref _process, null);
        if (previous is null)
        {
            return;
        }

        Interlocked.Increment(ref _generation);
        await previous.DisposeAsync().ConfigureAwait(false);
        var previousPump = Interlocked.Exchange(ref _eventPump, null);
        if (previousPump is not null)
        {
            try
            {
                await previousPump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task ApplyPiEventAsync(PiRpcEvent @event)
    {
        TouchRuntime();
        switch (@event)
        {
            case PiIdlePromptCompletedEvent completed when Journal.Projection.CurrentTurnId?.Value == completed.Tag:
                await SettleAsync().ConfigureAwait(false);
                break;
            case PiExtensionUiUpdateEvent { Key: "pistation-plan-state", Text: { } text }:
                var planState = PiPlanState.Parse(text);
                // Session-transition events can arrive before the rewind operation updates the thread identity.
                if (planState.SessionId == _thread.PiSessionId)
                {
                    await _database.RecordSettlementPlanAsync(_thread.ThreadId, planState, _shutdown.Token).ConfigureAwait(false);
                    Journal.Commit(new PiPlanChangedEvent(planState));
                }
                break;
            case PiExtensionUiUpdateEvent update:
                Journal.Commit(new PiExtensionUiChangedEvent(new PiExtensionUiUpdate(update.RequestId, update.Method,
                    DateTimeOffset.UtcNow, update.Key, update.Text, update.Lines, update.Placement, update.Severity)));
                break;
            case PiAgentStartedEvent:
                Journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Running));
                break;
            case PiMessageStartedEvent started when IsAssistant(started.Message):
                var messageId = $"assistant-{Guid.NewGuid():N}";
                lock (_stateLock)
                {
                    _currentAssistantMessageId = messageId;
                }

                Journal.Commit(new MessageStartedEvent(
                    ThreadProjectionReducer.ReadMessage(messageId, started.Message, isComplete: false)));
                break;
            case PiMessageCompletedEvent completed when completed.Message.TryGetProperty("role", out var role) && role.GetString() == "user":
                var delivered = ThreadProjectionReducer.ReadMessage($"user-{Guid.NewGuid():N}", completed.Message, true,
                    await _database.ListSentMessagesAsync(_thread.ThreadId, _shutdown.Token).ConfigureAwait(false));
                // The first prompt already has an optimistic row. Queued prompts gain a
                // row only on delivery, rather than being shown as sent when merely queued.
                if (delivered.Content is { } content && !Journal.Projection.Timeline.OfType<MessageTimelineItem>()
                        .Any(item => item.Content?.Id == content.Id))
                    Journal.Commit(new MessageCompletedEvent(delivered));
                break;
            case PiMessageUpdateEvent update:
                ApplyAssistantDelta(update.Delta);
                break;
            case PiMessageCompletedEvent completed when IsAssistant(completed.Message):
                Journal.Commit(new MessageCompletedEvent(ThreadProjectionReducer.ReadMessage(
                    GetAssistantMessageId(),
                    completed.Message,
                    isComplete: true)));
                CaptureTurnUsage(completed.Message, completed.Usage);
                break;
            case PiToolExecutionStartedEvent started:
                Journal.Commit(new ToolStartedEvent(new ToolProjection(
                    started.ToolCallId,
                    started.ToolName,
                    LimitToolArguments(started.Arguments),
                    string.Empty,
                    ToolExecutionState.Running)));
                await PersistAgentActivitiesAsync(PiAgentActivityProjector.Start(
                    started.ToolCallId,
                    started.ToolName,
                    started.Arguments,
                    Journal.Projection.CurrentTurnId,
                    DateTimeOffset.UtcNow)).ConfigureAwait(false);
                break;
            case PiToolExecutionUpdatedEvent updated:
                Journal.Commit(new ToolOutputReplacedEvent(
                    updated.ToolCallId,
                    LimitToolOutput(ExtractToolOutput(updated.PartialResult))));
                await PersistAgentActivitiesAsync(PiAgentActivityProjector.Update(
                    updated.ToolCallId,
                    updated.PartialResult,
                    Journal.Projection.AgentActivities ?? [],
                    isFinal: false,
                    isError: false,
                    Journal.Projection.CurrentTurnId,
                    DateTimeOffset.UtcNow)).ConfigureAwait(false);
                break;
            case PiToolExecutionCompletedEvent completed:
                Journal.Commit(new ToolCompletedEvent(
                    completed.ToolCallId,
                    LimitToolOutput(ExtractToolOutput(completed.Result)),
                    completed.IsError ? ToolExecutionState.Failed : ToolExecutionState.Completed));
                await PersistAgentActivitiesAsync(PiAgentActivityProjector.Update(
                    completed.ToolCallId,
                    completed.Result,
                    Journal.Projection.AgentActivities ?? [],
                    isFinal: true,
                    completed.IsError,
                    Journal.Projection.CurrentTurnId,
                    DateTimeOffset.UtcNow)).ConfigureAwait(false);
                break;
            case PiQueueUpdatedEvent updated:
                CommitQueue(updated.Steering, updated.FollowUp, QueueDeliveryState.Queued);
                break;
            case PiCompactionStartedEvent started:
                Journal.Commit(new ContextCompactionChangedEvent(new ContextCompactionProjection(
                    ContextCompactionState.Running,
                    started.Reason,
                    null,
                    null,
                    null,
                    null,
                    null,
                    DateTimeOffset.UtcNow)));
                break;
            case PiCompactionCompletedEvent completed:
                ApplyCompactionCompleted(completed);
                break;
            case PiConfirmRequestedEvent requested:
                Journal.Commit(new ApprovalRequestedEvent(
                    InteractionId.Parse(requested.RequestId),
                    requested.Title,
                    requested.Message,
                    requested.TimeoutMilliseconds));
                ScheduleInteractionTimeout(
                    InteractionId.Parse(requested.RequestId),
                    requested.TimeoutMilliseconds);
                break;
            case PiSelectRequestedEvent requested:
                Journal.Commit(new QuestionRequestedEvent(
                    InteractionId.Parse(requested.RequestId),
                    QuestionInputKind.Select,
                    requested.Title,
                    null,
                    requested.Options,
                    null,
                    requested.TimeoutMilliseconds));
                ScheduleInteractionTimeout(
                    InteractionId.Parse(requested.RequestId),
                    requested.TimeoutMilliseconds);
                break;
            case PiInputRequestedEvent requested:
                Journal.Commit(new QuestionRequestedEvent(
                    InteractionId.Parse(requested.RequestId),
                    QuestionInputKind.Input,
                    requested.Title,
                    requested.Placeholder,
                    [],
                    null,
                    requested.TimeoutMilliseconds));
                ScheduleInteractionTimeout(
                    InteractionId.Parse(requested.RequestId),
                    requested.TimeoutMilliseconds);
                break;
            case PiEditorRequestedEvent requested:
                Journal.Commit(new QuestionRequestedEvent(
                    InteractionId.Parse(requested.RequestId),
                    QuestionInputKind.Editor,
                    requested.Title,
                    null,
                    [],
                    requested.Prefill,
                    null));
                break;
            case PiAgentSettledEvent:
                await SettleAsync().ConfigureAwait(false);
                break;
            case PiUnknownEvent unknown:
                Journal.Commit(new UnknownRuntimeEvent(unknown.EventType, LimitPreview(unknown.Payload.GetRawText(), 1024)));
                break;
        }
    }

    private void ApplyAssistantDelta(PiAssistantDelta delta)
    {
        var messageId = GetAssistantMessageId();
        switch (delta)
        {
            case PiTextDelta text:
                Journal.Commit(new ContentDeltaEvent(messageId, text.ContentIndex, ContentKind.Text, text.Delta));
                break;
            case PiThinkingDelta thinking:
                Journal.Commit(new ContentDeltaEvent(
                    messageId,
                    thinking.ContentIndex,
                    ContentKind.Thinking,
                    thinking.Delta));
                break;
        }
    }

    private async Task ObserveExtensionCommandAsync(PiProcess process, TurnId turnId)
    {
        try { await process.Connection.ObserveHandledPromptAsync(turnId.Value, _shutdown.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or PiRpcException or TimeoutException or ObjectDisposedException)
        {
            // The normal runtime event pump owns crash/restart recovery.
        }
    }

    private async Task SettleAsync()
    {
        await FinalizeActiveAgentActivitiesAsync(
            AgentActivityState.Interrupted,
            "The parent Pi turn ended before this activity reported completion.").ConfigureAwait(false);
        CommitQueue([], [], QueueDeliveryState.Empty);
        TurnId? turnId;
        TurnMetrics? metrics;
        string? piEntryIdBeforeTurn;
        PiTokenUsage? usage;
        string provider;
        string model;
        (ClientId ClientId, CommandId CommandId)[] receipts;
        lock (_stateLock)
        {
            turnId = _activeTurnId;
            metrics = turnId is null ? null : CreateTurnMetrics();
            piEntryIdBeforeTurn = _activeTurnEntryIdBefore;
            usage = _activeTurnUsage;
            provider = _activeTurnProvider;
            model = _activeTurnModel;
            _activeTurnId = null;
            ClearActiveTurnMetrics();
            _currentAssistantMessageId = null;
            receipts = [.. _settlementReceipts];
            _settlementReceipts.Clear();
        }

        if (turnId is not null)
        {
            var turnCount = CountStartedTurns(Journal.Projection);
            ThreadCheckpoint? checkpoint;
            PiSessionEntries? entries = null;
            try
            {
                entries = _process is null
                    ? null
                    : await _process.Connection.GetEntriesAsync(cancellationToken: _shutdown.Token)
                        .ConfigureAwait(false);
                if (entries is not null)
                {
                    _lastPiEntryId = entries.LeafId;
                }

                checkpoint = await _checkpoints.CaptureTurnAsync(
                    _project,
                    _thread.ThreadId,
                    turnId.Value,
                    turnCount,
                    piEntryIdBeforeTurn,
                    entries?.LeafId,
                    _shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                try
                {
                    checkpoint = await _checkpoints.RecordCaptureErrorAsync(
                        _thread.ThreadId,
                        turnId.Value,
                        turnCount,
                        piEntryIdBeforeTurn,
                        entries?.LeafId,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception persistenceException) when (persistenceException is not OperationCanceledException)
                {
                    checkpoint = null;
                }
            }

            if (checkpoint is not null)
            {
                Journal.Commit(new CheckpointCapturedEvent(checkpoint));
            }

            var completionSequence = await _database.RecordCompletionAsync(_thread.ThreadId, CancellationToken.None).ConfigureAwait(false);
            Journal.Commit(new TurnSettledEvent(turnId.Value, metrics, completionSequence));
            if (usage is not null)
            {
                await _database.AppendUsageAsync(
                    _thread.ThreadId,
                    provider,
                    model,
                    usage.InputTokens,
                    usage.OutputTokens,
                    usage.CacheReadTokens + usage.CacheWriteTokens,
                    usage.TotalTokens,
                    usage.TotalCost,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        else
        {
            Journal.Commit(new RuntimeStateChangedEvent(ThreadRuntimeState.Ready));
        }

        foreach (var receipt in receipts)
        {
            await _database.UpdateReceiptStateAsync(
                receipt.ClientId,
                receipt.CommandId,
                CommandReceiptState.Completed).ConfigureAwait(false);
        }
    }

    private void CaptureTurnUsage(JsonElement message, PiTokenUsage? usage)
    {
        if (usage is null)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_activeTurnId is null)
            {
                return;
            }

            _activeTurnUsage = AddUsage(_activeTurnUsage, usage);
            if (PiUsageReader.HasUsableContext(message, usage))
            {
                _activeTurnContextTokens = usage.ContextTokens;
            }
        }
    }

    private void ApplyCompactionCompleted(PiCompactionCompletedEvent completed)
    {
        long? before = null;
        long? after = null;
        decimal? cost = null;
        string? summary = null;
        if (completed.Result is { } result)
        {
            if (result.TryGetProperty("tokensBefore", out var beforeElement) && beforeElement.TryGetInt64(out var parsedBefore))
            {
                before = parsedBefore;
            }
            if (result.TryGetProperty("estimatedTokensAfter", out var afterElement) && afterElement.TryGetInt64(out var parsedAfter))
            {
                after = parsedAfter;
            }
            if (result.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind == JsonValueKind.String)
            {
                summary = LimitPreview(summaryElement.GetString() ?? string.Empty, 2_000);
            }
            if (result.TryGetProperty("usage", out var usageElement) &&
                usageElement.TryGetProperty("cost", out var costElement) &&
                costElement.TryGetProperty("total", out var totalElement) &&
                totalElement.TryGetDecimal(out var parsedCost))
            {
                cost = parsedCost;
            }
        }
        Journal.Commit(new ContextCompactionChangedEvent(new ContextCompactionProjection(
            completed.Aborted ? ContextCompactionState.Interrupted :
                completed.Result is null ? ContextCompactionState.Failed : ContextCompactionState.Completed,
            completed.Reason,
            before,
            after,
            cost,
            summary,
            completed.ErrorMessage,
            DateTimeOffset.UtcNow)));
    }

    private TurnMetrics CreateTurnMetrics()
    {
        long? elapsedMilliseconds = _activeTurnStartedTimestamp is { } started
            ? Math.Max(0, (long)Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds))
            : null;
        var usage = _activeTurnUsage is null
            ? null
            : new TokenUsage(
                _activeTurnUsage.InputTokens,
                _activeTurnUsage.OutputTokens,
                _activeTurnUsage.CacheReadTokens,
                _activeTurnUsage.CacheWriteTokens,
                _activeTurnUsage.ReasoningTokens,
                _activeTurnUsage.TotalTokens);
        return new TurnMetrics(
            elapsedMilliseconds,
            usage,
            _activeTurnContextTokens,
            _activeTurnContextWindow);
    }

    private void ClearActiveTurnMetrics()
    {
        _activeTurnContextWindow = null;
        _activeTurnContextTokens = null;
        _activeTurnEntryIdBefore = null;
        _activeTurnStartedTimestamp = null;
        _activeTurnUsage = null;
    }

    private static int CountStartedTurns(ThreadProjection projection) =>
        projection.Timeline.OfType<TurnBoundaryTimelineItem>()
            .Count(static boundary => boundary.Boundary == TurnBoundaryKind.Started);

    private static int CountSettledTurns(ThreadProjection projection) =>
        projection.Timeline.OfType<TurnBoundaryTimelineItem>()
            .Count(static boundary => boundary.Boundary == TurnBoundaryKind.Settled);

    private static PiTokenUsage AddUsage(PiTokenUsage? current, PiTokenUsage next) => new(
        AddSaturated(current?.InputTokens ?? 0, next.InputTokens),
        AddSaturated(current?.OutputTokens ?? 0, next.OutputTokens),
        AddSaturated(current?.CacheReadTokens ?? 0, next.CacheReadTokens),
        AddSaturated(current?.CacheWriteTokens ?? 0, next.CacheWriteTokens),
        current?.ReasoningTokens is null && next.ReasoningTokens is null
            ? null
            : AddSaturated(current?.ReasoningTokens ?? 0, next.ReasoningTokens ?? 0),
        AddSaturated(current?.TotalTokens ?? 0, next.TotalTokens),
        current is null ? next.TotalCost : current.TotalCost is { } left && next.TotalCost is { } right
            ? left > decimal.MaxValue - right ? decimal.MaxValue : left + right
            : null);

    private static long AddSaturated(long left, long right) => left > long.MaxValue - right
        ? long.MaxValue
        : left + right;

    private async Task PersistAgentActivitiesAsync(IReadOnlyList<AgentActivityProjection> activities)
    {
        if (activities.Count == 0)
        {
            return;
        }

        var turnCount = CountStartedTurns(Journal.Projection);
        foreach (var activity in activities)
        {
            var @event = new AgentActivityChangedEvent(activity);
            var previous = Journal.Projection.AgentActivities?.FirstOrDefault(item => item.ActivityId == activity.ActivityId);
            if (previous is not null && activity with { UpdatedUtc = previous.UpdatedUtc } == previous) continue;
            if (activity.ControlId is not null && previous is not null && activity with { UpdatedUtc = previous.UpdatedUtc, CurrentActivity = previous.CurrentActivity } == previous)
            {
                // Stream transient progress without duplicating the entire transcript in storage.
                Journal.Commit(@event);
                continue;
            }
            await _database.AppendThreadAgentEventAsync(
                _thread.ThreadId,
                activity.TurnId,
                turnCount,
                @event,
                _shutdown.Token).ConfigureAwait(false);
            Journal.Commit(@event);
        }
    }

    private async Task FinalizeActiveAgentActivitiesAsync(
        AgentActivityState finalState,
        string failureSummary)
    {
        var now = DateTimeOffset.UtcNow;
        var active = (Journal.Projection.AgentActivities ?? [])
            .Where(static activity => activity.State is AgentActivityState.Pending or
                AgentActivityState.Running or AgentActivityState.Waiting)
            .ToArray();
        foreach (var activity in active)
        {
            await PersistAgentActivitiesAsync(
            [
                activity with
                {
                    State = finalState,
                    CurrentActivity = finalState == AgentActivityState.Failed ? "Failed" : "Interrupted",
                    UpdatedUtc = now,
                    CompletedUtc = now,
                    FailureSummary = activity.FailureSummary ?? failureSummary,
                    CanInterrupt = false,
                },
            ]).ConfigureAwait(false);
        }
    }

    private void CommitQueue(
        IReadOnlyList<string> steering,
        IReadOnlyList<string> followUp,
        QueueDeliveryState requestedState)
    {
        var current = Journal.Projection.Queue;
        var messages = steering.Select((text, index) => new QueuedMessageProjection(
                QueuedMessageKind.Steering,
                index + 1,
                LimitPreview(SentMessageReference.Remove(PiPromptFormatter.NormalizePersistedMessage(text)), 4_000)))
            .Concat(followUp.Select((text, index) => new QueuedMessageProjection(
                QueuedMessageKind.FollowUp,
                index + 1,
                LimitPreview(SentMessageReference.Remove(PiPromptFormatter.NormalizePersistedMessage(text)), 4_000))))
            .ToArray();
        var state = messages.Length > 0
            ? QueueDeliveryState.Queued
            : requestedState switch
            {
                QueueDeliveryState.Cleared => QueueDeliveryState.Cleared,
                QueueDeliveryState.Empty => QueueDeliveryState.Empty,
                _ => current?.PendingMessageCount > 0
                    ? QueueDeliveryState.Delivering
                    : QueueDeliveryState.Empty,
            };
        Journal.Commit(new QueueStateChangedEvent(new ThreadQueueProjection(
            messages,
            current?.SteeringMode ?? QueueDeliveryMode.OneAtATime,
            current?.FollowUpMode ?? QueueDeliveryMode.OneAtATime,
            state,
            messages.Length,
            DateTimeOffset.UtcNow)));
    }

    private static ThreadQueueProjection CreateQueueProjection(
        PiSessionState state,
        IReadOnlyList<QueuedMessageProjection> messages) => new(
        state.PendingMessageCount == messages.Count ? messages : [],
        ParseDeliveryMode(state.SteeringMode),
        ParseDeliveryMode(state.FollowUpMode),
        state.PendingMessageCount > 0 ? QueueDeliveryState.Queued : QueueDeliveryState.Empty,
        state.PendingMessageCount,
        DateTimeOffset.UtcNow);

    private static QueueDeliveryMode ParseDeliveryMode(string value) => value == "all"
        ? QueueDeliveryMode.All
        : QueueDeliveryMode.OneAtATime;

    private T RequirePendingInteraction<T>(InteractionId interactionId) where T : TimelineItem
    {
        var interaction = Journal.Projection.Timeline.FirstOrDefault(item => item switch
        {
            ApprovalTimelineItem approval => approval.InteractionId == interactionId,
            QuestionTimelineItem question => question.InteractionId == interactionId,
            _ => false,
        });
        if (interaction is null)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.InteractionNotFound,
                "The interaction is not present in the current thread projection.");
        }

        if (interaction is not T typed)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.InvalidInteractionResponse,
                $"The interaction cannot be handled as {typeof(T).Name}.");
        }

        var state = typed switch
        {
            ApprovalTimelineItem approval => approval.State,
            QuestionTimelineItem question => question.State,
            _ => InteractionState.Canceled,
        };
        if (state != InteractionState.Pending)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.InteractionNotPending,
                $"The interaction is already {state.ToString().ToLowerInvariant()}.");
        }

        return typed;
    }

    private PiProcess RequireInteractionProcess() => _process ?? throw new HostOperationException(
        ProtocolErrorCodes.PiRuntimeCrashed,
        "Pi is no longer available to receive the interaction response.");

    private async Task<PiProcess> RequireQueueProcessAsync(CancellationToken cancellationToken)
    {
        if (_process is null || Journal.Projection.RuntimeState == ThreadRuntimeState.Stopped)
        {
            await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_process is null || Journal.Projection.RuntimeState is not
            (ThreadRuntimeState.Ready or ThreadRuntimeState.Running))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ThreadBusy,
                "The Pi queue is unavailable while the thread is changing runtime state.");
        }

        return _process;
    }

    private PiProcess RequireReadyProcess()
    {
        bool hasActiveTurn;
        lock (_stateLock)
        {
            hasActiveTurn = _activeTurnId is not null;
        }

        if (_process is null || hasActiveTurn || Journal.Projection.RuntimeState != ThreadRuntimeState.Ready)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ThreadBusy,
                "Stop the running turn before changing Pi configuration.");
        }

        return _process;
    }

    private static async Task ApplyPersistedPiConfigurationAsync(
        PiRpcConnection connection,
        ThreadPiConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (configuration.RuntimeModeId is not null)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.PiConfigurationUnsupported,
                $"Stored runtime mode '{configuration.RuntimeModeId}' is not supported by Pi RPC.");
        }

        if (configuration.Model is not null)
        {
            await connection.SetModelAsync(
                configuration.Model.ProviderId,
                configuration.Model.ModelId,
                cancellationToken).ConfigureAwait(false);
        }

        if (configuration.ThinkingLevel is not null)
        {
            var level = ToPiThinkingLevel(configuration.ThinkingLevel.Value);
            var available = await connection.GetAvailableThinkingLevelsAsync(cancellationToken).ConfigureAwait(false);
            if (!available.Contains(level, StringComparer.Ordinal))
            {
                throw new HostOperationException(
                    ProtocolErrorCodes.PiConfigurationUnsupported,
                    $"Stored thinking level '{level}' is no longer supported by the selected Pi model.");
            }

            await connection.SetThinkingLevelAsync(level, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<ThreadPiConfigurationSnapshot> ReadPiConfigurationSnapshotAsync(
        PiRpcConnection connection,
        ThreadPiConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var state = await connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
        var models = await connection.GetAvailableModelsAsync(cancellationToken).ConfigureAwait(false);
        var thinkingLevels = await connection.GetAvailableThinkingLevelsAsync(cancellationToken).ConfigureAwait(false);
        var capabilities = new PiConfigurationCapabilities(
            models.Select(static model => new PiModelCapability(
                model.ProviderId,
                model.ModelId,
                model.DisplayName,
                model.SupportsReasoning,
                model.ContextWindow)).ToArray(),
            thinkingLevels.Select(ParsePiThinkingLevel).ToArray(),
            []);
        return new ThreadPiConfigurationSnapshot(
            configuration,
            capabilities,
            state.Model is null ? null : new PiModelSelection(state.Model.ProviderId, state.Model.ModelId),
            ParsePiThinkingLevel(state.ThinkingLevel),
            null);
    }

    private static async Task TryRestoreActivePiConfigurationAsync(
        PiRpcConnection connection,
        PiSessionState originalState)
    {
        try
        {
            if (originalState.Model is not null)
            {
                await connection.SetModelAsync(
                    originalState.Model.ProviderId,
                    originalState.Model.ModelId,
                    CancellationToken.None).ConfigureAwait(false);
            }

            var available = await connection.GetAvailableThinkingLevelsAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (available.Contains(originalState.ThinkingLevel, StringComparer.Ordinal))
            {
                await connection.SetThinkingLevelAsync(originalState.ThinkingLevel, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Preserve the authoritative validation/persistence failure rather than masking it with rollback failure.
        }
    }

    private static void ValidateConfigurationValues(PiModelSelection? model, string? runtimeModeId)
    {
        if (model is not null &&
            (string.IsNullOrWhiteSpace(model.ProviderId) || string.IsNullOrWhiteSpace(model.ModelId)))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.PiConfigurationInvalid,
                "A Pi model selection requires non-empty provider and model identifiers.");
        }

        if (runtimeModeId is not null && string.IsNullOrWhiteSpace(runtimeModeId))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.PiConfigurationInvalid,
                "A Pi runtime mode identifier cannot be empty.");
        }
    }

    private static bool Matches(PiModelInfo? active, PiModelSelection requested) =>
        active is not null &&
        string.Equals(active.ProviderId, requested.ProviderId, StringComparison.Ordinal) &&
        string.Equals(active.ModelId, requested.ModelId, StringComparison.Ordinal);

    private static PiThinkingLevel ParsePiThinkingLevel(string value) => value switch
    {
        "off" => PiThinkingLevel.Off,
        "minimal" => PiThinkingLevel.Minimal,
        "low" => PiThinkingLevel.Low,
        "medium" => PiThinkingLevel.Medium,
        "high" => PiThinkingLevel.High,
        "xhigh" => PiThinkingLevel.XHigh,
        "max" => PiThinkingLevel.Max,
        _ => throw new PiRpcConnectionException($"Pi reported unknown thinking level '{value}'."),
    };

    private static string ToPiThinkingLevel(PiThinkingLevel value) => value switch
    {
        PiThinkingLevel.Off => "off",
        PiThinkingLevel.Minimal => "minimal",
        PiThinkingLevel.Low => "low",
        PiThinkingLevel.Medium => "medium",
        PiThinkingLevel.High => "high",
        PiThinkingLevel.XHigh => "xhigh",
        PiThinkingLevel.Max => "max",
        _ => throw new HostOperationException(
            ProtocolErrorCodes.PiConfigurationInvalid,
            $"Unknown Pi thinking level '{value}'."),
    };

    private void ResolveInteraction(InteractionResolvedEvent resolved)
    {
        CancellationTokenSource? timeout = null;
        lock (_stateLock)
        {
            if (_interactionTimeouts.Remove(resolved.InteractionId, out var found))
            {
                timeout = found;
            }
        }

        timeout?.Cancel();
        timeout?.Dispose();
        Journal.Commit(resolved);
    }

    private void ScheduleInteractionTimeout(InteractionId interactionId, int? timeoutMilliseconds)
    {
        if (timeoutMilliseconds is not > 0)
        {
            return;
        }

        var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        lock (_stateLock)
        {
            if (_interactionTimeouts.Remove(interactionId, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }

            _interactionTimeouts[interactionId] = timeout;
        }

        _ = ExpireInteractionAsync(interactionId, timeoutMilliseconds.Value, timeout);
    }

    private async Task ExpireInteractionAsync(
        InteractionId interactionId,
        int timeoutMilliseconds,
        CancellationTokenSource timeout)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(timeoutMilliseconds), timeout.Token).ConfigureAwait(false);
            await _interactionGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                var interaction = Journal.Projection.Timeline.FirstOrDefault(item => item switch
                {
                    ApprovalTimelineItem approval => approval.InteractionId == interactionId &&
                        approval.State == InteractionState.Pending,
                    QuestionTimelineItem question => question.InteractionId == interactionId &&
                        question.State == InteractionState.Pending,
                    _ => false,
                });
                if (interaction is not null)
                {
                    Journal.Commit(new InteractionResolvedEvent(interactionId, InteractionState.TimedOut));
                }

                lock (_stateLock)
                {
                    _interactionTimeouts.Remove(interactionId);
                }
            }
            finally
            {
                _interactionGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            timeout.Dispose();
        }
    }

    private string GetAssistantMessageId()
    {
        lock (_stateLock)
        {
            return _currentAssistantMessageId ??= $"assistant-{Guid.NewGuid():N}";
        }
    }

    private async Task TryGenerateAutomaticTitleAsync(string prompt, CancellationToken cancellationToken)
    {
        var current = await _database.GetThreadAsync(_thread.ThreadId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return;
        }
        var descriptor = await _database.EnrichThreadDescriptorAsync(current, cancellationToken).ConfigureAwait(false);
        if (descriptor.TitleKind != ThreadTitleKind.Placeholder)
        {
            return;
        }
        var title = GenerateThreadTitle(prompt);
        var update = await _database.UpdateThreadMetadataAsync(
            current.ThreadId, current.Revision, title, null, null, cancellationToken).ConfigureAwait(false);
        if (!update.WasUpdated || update.Thread is null)
        {
            return;
        }
        await _database.SetThreadTitleKindAsync(current.ThreadId, ThreadTitleKind.Generated, cancellationToken)
            .ConfigureAwait(false);
        _thread = update.Thread;
        if (_process is not null)
        {
            try
            {
                await _process.Connection.SetSessionNameAsync(title, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is PiRpcConnectionException or PiRpcTimeoutException)
            {
                // The first prompt remains accepted and the durable title will be applied on the next runtime start.
            }
        }
    }

    internal static string GenerateThreadTitle(string prompt)
    {
        var normalized = string.Join(' ', prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.StartsWith('/'))
        {
            var separator = normalized.IndexOf(' ');
            normalized = separator >= 0 ? normalized[(separator + 1)..] : normalized.TrimStart('/');
        }
        var sentenceEnd = normalized.IndexOfAny(['.', '!', '?', '\n', '\r']);
        if (sentenceEnd is > 10 and < 80)
        {
            normalized = normalized[..sentenceEnd];
        }
        normalized = normalized.Trim(' ', '.', ',', ':', ';', '-', '—');
        if (normalized.Length == 0)
        {
            return "New thread";
        }
        if (normalized.Length > 72)
        {
            normalized = normalized[..69].TrimEnd() + "…";
        }
        return char.ToUpperInvariant(normalized[0]) + normalized[1..];
    }

    private void ValidateSessionFile(string? sessionFile)
    {
        if (sessionFile is null)
        {
            return;
        }

        var sessionRoot = Path.GetFullPath(_options.SessionRoot) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(sessionFile);
        if (!candidate.StartsWith(sessionRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new PiRpcConnectionException("Pi reported a session file outside the environment session root.");
        }
    }

    private string LimitToolOutput(string value) => LimitPreview(value, _options.ToolOutputCharacterLimit);

    private string LimitToolArguments(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return string.Empty;
        }

        var arguments = value.GetRawText();
        return arguments.Length <= _options.ToolArgumentCharacterLimit
            ? arguments
            : $"{arguments[.._options.ToolArgumentCharacterLimit]}…";
    }

    private static string LimitPreview(string value, int limit) => value.Length <= limit ? value : value[^limit..];

    private static bool IsAssistant(JsonElement message) =>
        message.TryGetProperty("role", out var role) && role.GetString() == "assistant";

    private static string ExtractToolOutput(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Concat(content.EnumerateArray()
            .Where(static block => block.TryGetProperty("type", out var type) && type.GetString() == "text")
            .Select(static block => block.TryGetProperty("text", out var text) ? text.GetString() : null));
    }

    private static ProtocolError ToProtocolError(Exception exception) => exception switch
    {
        PiRpcTimeoutException => new ProtocolError(ProtocolErrorCodes.PiCommandTimedOut, exception.Message, true),
        PiRpcConnectionException => new ProtocolError(ProtocolErrorCodes.PiRuntimeCrashed, exception.Message, true),
        _ => new ProtocolError(ProtocolErrorCodes.PiLaunchFailed, exception.Message, true),
    };
}
