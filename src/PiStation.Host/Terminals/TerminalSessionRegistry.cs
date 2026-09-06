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
    private readonly ThreadWorkspaceResolver _workspaceResolver;

    public int ActiveCount => _sessions.Values.Count(static session =>
        session.Descriptor.State == TerminalSessionState.Running);

    public TerminalSessionRegistry(
        HostDatabase database,
        HostOptions options,
        ThreadWorkspaceResolver? workspaceResolver = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _workspaceResolver = workspaceResolver ?? new ThreadWorkspaceResolver(database);
    }

    public async Task<TerminalSessionDescriptor> StartAsync(
        StartTerminalSessionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
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
                workspace.WorkspaceRoot);
            var session = new TerminalSession(descriptor, process, _options);
            if (!_sessions.TryAdd(descriptor.TerminalSessionId, session))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("The generated terminal session identity already exists.");
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
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryRemove(request.TerminalSessionId, out var session))
        {
            throw NotFound(request.TerminalSessionId);
        }

        await session.DisposeAsync().ConfigureAwait(false);
    }

    public IAsyncEnumerable<TerminalEnvelope> SubscribeAsync(
        TerminalSessionId terminalSessionId,
        TerminalCursor? cursor,
        CancellationToken cancellationToken) => Get(terminalSessionId).SubscribeAsync(cursor, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
        _gate.Dispose();
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
