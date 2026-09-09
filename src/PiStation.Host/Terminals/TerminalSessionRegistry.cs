using System.Collections.Concurrent;
using PiStation.Host.Errors;
using PiStation.Host.Persistence;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Streaming;
using PiStation.Host.Workspaces;

namespace PiStation.Host.Terminals;

internal sealed class TerminalSessionRegistry : IAsyncDisposable
{
    private readonly HostDatabase _database;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HostOptions _options;
    private readonly ConcurrentDictionary<TerminalSessionId, TerminalSession> _sessions = new();
    private bool _disposed;
    private bool _initialized;
    private readonly TerminalHistoryStore _historyStore;
    private readonly Func<IReadOnlyList<TerminalProcessEntry>> _captureProcesses;
    private readonly CancellationTokenSource _pollStopping = new();
    private Task _pollTask = Task.CompletedTask;
    private const int MaximumRetainedInactiveSessions = 128;
    public bool HasActiveWork => _sessions.Values.Any(session => session.Descriptor.State == TerminalSessionState.Running);
    private readonly ThreadWorkspaceResolver _workspaceResolver;

    public int ActiveCount => _sessions.Values.Count(static session =>
        session.Descriptor.State == TerminalSessionState.Running);

    public TerminalSessionRegistry(
        HostDatabase database,
        HostOptions options,
        ThreadWorkspaceResolver? workspaceResolver = null,
        Func<IReadOnlyList<TerminalProcessEntry>>? captureProcesses = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _workspaceResolver = workspaceResolver ?? new ThreadWorkspaceResolver(database);
        _historyStore = new(options.CanonicalDataRoot);
        _captureProcesses = captureProcesses ?? TerminalProcessInspector.Capture;
    }

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized) return;
            foreach (var file in _historyStore.Files().OrderByDescending(File.GetLastWriteTimeUtc))
            {
                try
                {
                    var saved = await _historyStore.ReadAsync(file, token).ConfigureAwait(false);
                    if (saved is null) continue;
                    var descriptor = saved.Descriptor;
                    // Initialization can be retried after cancellation without replacing
                    // sessions already restored (and leaking their persistence workers).
                    if (_sessions.ContainsKey(descriptor.TerminalSessionId)) continue;
                    var project = await _database.GetProjectAsync(descriptor.ProjectId, token).ConfigureAwait(false);
                    var thread = descriptor.ThreadId is { } threadId ? await _database.GetThreadAsync(threadId, token).ConfigureAwait(false) : null;
                    if (project is null || descriptor.ThreadId is not null && thread?.ProjectId != project.ProjectId ||
                        _sessions.Count >= MaximumRetainedInactiveSessions)
                    { _historyStore.Delete(descriptor.TerminalSessionId); continue; }
                    if (!Enum.IsDefined(descriptor.State) || !Enum.IsDefined(descriptor.ShellKind)) continue;
                    descriptor = descriptor with
                    {
                        State = descriptor.State == TerminalSessionState.Running ? TerminalSessionState.Interrupted : descriptor.State,
                        ErrorMessage = descriptor.State == TerminalSessionState.Running ? "The host stopped. Saved output is available; restart to open a fresh shell." : descriptor.ErrorMessage,
                        Epoch = Guid.NewGuid().ToString("N"), HasRunningSubprocess = false, ForegroundCommand = null,
                    };
                    _sessions[descriptor.TerminalSessionId] = new(descriptor, null, _options, _historyStore, saved.BufferedOutput);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
                { System.Diagnostics.Trace.TraceWarning("Saved terminal could not be restored: {0}", error.Message); }
            }
            _initialized = true;
            _pollTask = RunActivityPollAsync(_pollStopping.Token);
        }
        finally { _gate.Release(); }
    }

    private async Task RunActivityPollAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try { PollActivity(); }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
                { System.Diagnostics.Trace.TraceWarning("Terminal activity is temporarily unavailable: {0}", error.Message); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    internal void PollActivity()
    {
        var sessions = _sessions.Values.Select(session => (Session: session, Pid: session.ProcessId)).Where(item => item.Pid is not null).ToArray();
        if (sessions.Length == 0) return;
        var snapshot = _captureProcesses();
        foreach (var (session, pid) in sessions)
            if (session.ProcessId == pid && TerminalProcessInspector.Inspect(snapshot, pid!.Value) is { } activity)
                session.Journal.CommitActivity(activity);
    }

    public async Task<TerminalSessionDescriptor> StartAsync(
        StartTerminalSessionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        ValidateDimensions(request.Columns, request.Rows);
        if (!Enum.IsDefined(request.ShellKind))
        {
            throw new HostOperationException(ProtocolErrorCodes.TerminalInvalid, "The terminal shell is invalid.");
        }

        var workspace = await _workspaceResolver.ResolveAsync(
            request.ProjectId,
            request.ThreadId,
            cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var projectSessions = _sessions.Values
                .Where(session => session.Descriptor.ProjectId == request.ProjectId &&
                                  session.Descriptor.ThreadId == request.ThreadId)
                .ToArray();
            if (projectSessions.Count(session => session.Descriptor.State == TerminalSessionState.Running) >=
                _options.MaximumTerminalSessionsPerProject)
            {
                throw new HostOperationException(
                    ProtocolErrorCodes.TerminalLimitExceeded,
                    $"A project can have at most {_options.MaximumTerminalSessionsPerProject} running terminals.");
            }

            var ordinal = projectSessions.Length + 1;
            ConPtyTerminalProcess process;
            try
            {
                process = ConPtyTerminalProcess.Start(
                    workspace.WorkspaceRoot,
                    request.ShellKind,
                    request.Columns,
                    request.Rows);
            }
            catch (Exception exception) when (exception is not HostOperationException)
            {
                throw new HostOperationException(
                    ProtocolErrorCodes.TerminalUnavailable,
                    $"The terminal could not start: {exception.Message}");
            }

            var shellDisplayName = request.ShellKind == TerminalShellKind.PowerShell
                ? "PowerShell"
                : "Command Prompt";
            var descriptor = new TerminalSessionDescriptor(
                TerminalSessionId.New(),
                request.ProjectId,
                $"{shellDisplayName} {ordinal}",
                request.ShellKind,
                shellDisplayName,
                TerminalSessionState.Running,
                request.Columns,
                request.Rows,
                null,
                null,
                DateTimeOffset.UtcNow,
                Sequence.Initial,
                request.ThreadId,
                workspace.WorkspaceRoot,
                Epoch: Guid.NewGuid().ToString("N"));
            var session = new TerminalSession(descriptor, process, _options, _historyStore);
            if (!_sessions.TryAdd(descriptor.TerminalSessionId, session))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("The generated terminal session identity already exists.");
            }

            try { await session.FlushAsync().ConfigureAwait(false); }
            catch
            {
                _sessions.TryRemove(descriptor.TerminalSessionId, out _);
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            foreach (var inactive in _sessions.Values.Where(item => item.Descriptor.State != TerminalSessionState.Running)
                .OrderByDescending(item => item.Descriptor.CreatedUtc).Skip(MaximumRetainedInactiveSessions).ToArray())
            {
                if (_sessions.TryRemove(inactive.Descriptor.TerminalSessionId, out _))
                {
                    await inactive.DisposeAsync().ConfigureAwait(false);
                    _historyStore.Delete(inactive.Descriptor.TerminalSessionId);
                }
            }
            return descriptor;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TerminalSessionDescriptor>> ListAsync(
        ProjectId projectId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (await _database.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ProjectNotFound,
                $"Project '{projectId}' was not found.");
        }

        return _sessions.Values
            .Select(static session => session.Descriptor)
            .Where(descriptor => descriptor.ProjectId == projectId)
            .OrderBy(descriptor => descriptor.CreatedUtc)
            .ThenBy(descriptor => descriptor.TerminalSessionId.Value, StringComparer.Ordinal)
            .ToArray();
    }

    public Task WriteAsync(WriteTerminalInputRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Get(request.TerminalSessionId).WriteAsync(request.Data, cancellationToken);
    }

    public Task<TerminalSessionDescriptor> WaitForExitAsync(
        TerminalSessionId terminalSessionId,
        CancellationToken cancellationToken = default) =>
        Get(terminalSessionId).WaitForExitAsync(cancellationToken);

    public Task<TerminalSessionDescriptor> ResizeAsync(
        ResizeTerminalSessionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDimensions(request.Columns, request.Rows);
        return Task.FromResult(Get(request.TerminalSessionId).Resize(request.Columns, request.Rows));
    }

    public Task<TerminalSessionDescriptor> StopAsync(
        StopTerminalSessionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Get(request.TerminalSessionId).StopAsync(cancellationToken);
    }

    public async Task CloseAsync(CloseTerminalSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_sessions.TryRemove(request.TerminalSessionId, out var session))
                throw NotFound(request.TerminalSessionId);
            try { await session.DisposeAsync().ConfigureAwait(false); }
            finally { _historyStore.Delete(request.TerminalSessionId); }
        }
        finally { _gate.Release(); }
    }

    public async Task<TerminalSnapshotEnvelope> ClearHistoryAsync(ClearTerminalHistoryRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Get(request.TerminalSessionId).ClearHistoryAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public IAsyncEnumerable<TerminalEnvelope> SubscribeAsync(
        TerminalSessionId terminalSessionId,
        TerminalCursor? cursor,
        CancellationToken cancellationToken) => Get(terminalSessionId).SubscribeAsync(cursor, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await _pollStopping.CancelAsync().ConfigureAwait(false);
            await _pollTask.ConfigureAwait(false);
            List<Exception> errors = [];
            foreach (var session in _sessions.Values)
            {
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { errors.Add(error); }
            }
            _sessions.Clear();
            _pollStopping.Dispose();
            if (errors.Count > 0) throw new AggregateException("Terminal shutdown could not save all histories.", errors);
        }
        finally { _gate.Release(); }
    }

    private TerminalSession Get(TerminalSessionId terminalSessionId) =>
        _sessions.TryGetValue(terminalSessionId, out var session)
            ? session
            : throw NotFound(terminalSessionId);

    private static HostOperationException NotFound(TerminalSessionId terminalSessionId) => new(
        ProtocolErrorCodes.TerminalNotFound,
        $"Terminal session '{terminalSessionId}' was not found.");

    private static void ValidateDimensions(int columns, int rows)
    {
        if (columns is < TerminalDefaults.MinimumColumns or > TerminalDefaults.MaximumColumns ||
            rows is < TerminalDefaults.MinimumRows or > TerminalDefaults.MaximumRows)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.TerminalInvalid,
                $"Terminal dimensions must be {TerminalDefaults.MinimumColumns}-{TerminalDefaults.MaximumColumns} " +
                $"columns by {TerminalDefaults.MinimumRows}-{TerminalDefaults.MaximumRows} rows.");
        }
    }
}
