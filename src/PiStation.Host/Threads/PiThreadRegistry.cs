using System.Collections.Concurrent;
using PiStation.Host.Errors;
using PiStation.Host.Git;
using PiStation.Host.Persistence;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Projections;

namespace PiStation.Host.Threads;

public sealed class PiThreadRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<ThreadId, Lazy<Task<PiThreadController>>> _controllers = [];
    private readonly CancellationTokenSource _reaperShutdown = new();
    private readonly Task _reaper;
    private readonly HostDatabase _database;
    private readonly HostEnvironmentRecord _environment;
    private readonly HostOptions _options;
    private readonly IPiProcessFactory _processFactory;
    private readonly WorkspaceCheckpointService _checkpoints;

    public PiThreadRegistry(
        HostEnvironmentRecord environment,
        HostDatabase database,
        IPiProcessFactory processFactory,
        WorkspaceCheckpointService checkpoints,
        HostOptions options)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _reaper = ReapIdleRuntimesAsync();
    }

    public Task<PiThreadController> GetAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default)
    {
        var lazy = _controllers.GetOrAdd(
            threadId,
            id => new Lazy<Task<PiThreadController>>(
                () => CreateAsync(id, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value.WaitAsync(cancellationToken);
    }

    public bool TryGetRuntimeState(ThreadId threadId, out ThreadRuntimeState state)
    {
        if (_controllers.TryGetValue(threadId, out var lazy) &&
            lazy.IsValueCreated &&
            lazy.Value.IsCompletedSuccessfully)
        {
            state = lazy.Value.Result.Journal.Projection.RuntimeState;
            return true;
        }

        state = ThreadRuntimeState.Stopped;
        return false;
    }

    public bool TryGetController(ThreadId threadId, out PiThreadController? controller)
    {
        if (_controllers.TryGetValue(threadId, out var lazy) &&
            lazy.IsValueCreated &&
            lazy.Value.IsCompletedSuccessfully)
        {
            controller = lazy.Value.Result;
            return true;
        }

        controller = null;
        return false;
    }

    public int ActiveCount => _controllers.Values.Count(static controller =>
        controller.IsValueCreated && controller.Value.IsCompletedSuccessfully &&
        controller.Value.Result.Journal.Projection.RuntimeState is
            ThreadRuntimeState.Starting or ThreadRuntimeState.Hydrating or
            ThreadRuntimeState.Ready or ThreadRuntimeState.Running or ThreadRuntimeState.Stopping);
    public bool HasActiveWork => _controllers.Values.Any(lazy => lazy.IsValueCreated &&
        (!lazy.Value.IsCompleted || lazy.Value.IsCompletedSuccessfully && lazy.Value.Result.Journal.Projection.RuntimeState is
            ThreadRuntimeState.Starting or ThreadRuntimeState.Hydrating or ThreadRuntimeState.Running or ThreadRuntimeState.Stopping));

    public async Task StopAndForgetAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default)
    {
        if (!_controllers.TryRemove(threadId, out var lazy) || !lazy.IsValueCreated)
        {
            return;
        }

        var controller = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        await controller.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _reaperShutdown.Cancel();
        await _reaper.ConfigureAwait(false);
        _reaperShutdown.Dispose();
        foreach (var controller in _controllers.Values)
        {
            if (!controller.IsValueCreated)
            {
                continue;
            }

            await (await controller.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        }

        _controllers.Clear();
    }

    private async Task ReapIdleRuntimesAsync()
    {
        using var timer = new PeriodicTimer(_options.IdleRuntimeSweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_reaperShutdown.Token).ConfigureAwait(false))
            {
                foreach (var lazy in _controllers.Values)
                {
                    if (!lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully) continue;
                    try { await lazy.Value.Result.StopIfIdleAsync(DateTimeOffset.UtcNow, _options.IdleRuntimeTimeout, _reaperShutdown.Token).ConfigureAwait(false); }
                    catch (ObjectDisposedException) { }
                }
            }
        }
        catch (OperationCanceledException) when (_reaperShutdown.IsCancellationRequested) { }
    }

    private async Task<PiThreadController> CreateAsync(ThreadId threadId, CancellationToken cancellationToken)
    {
        var thread = await _database.GetThreadAsync(threadId, cancellationToken).ConfigureAwait(false)
            ?? throw new HostOperationException(
                ProtocolErrorCodes.ThreadNotFound,
                $"Thread '{threadId}' was not found.");
        var project = await _database.GetProjectAsync(thread.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new HostOperationException(
                ProtocolErrorCodes.ThreadNotFound,
                $"Project for thread '{threadId}' was not found.");
        return new PiThreadController(
            _environment,
            project,
            thread,
            _database,
            _processFactory,
            _checkpoints,
            _options);
    }
}
