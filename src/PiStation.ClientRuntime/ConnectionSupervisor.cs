using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime;

public sealed class ConnectionSupervisor : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly HubConnection _connection;
    private TaskCompletionSource _connected = CreateConnectedSignal();
    private bool _disposed;

    public ConnectionSupervisor(ClientRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _connection = new HubConnectionBuilder()
            .WithUrl(options.HubAddress, connection =>
                connection.Headers["Authorization"] = $"Bearer {options.BearerCredential}")
            .AddJsonProtocol(json =>
                json.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ProtocolJsonContext.Default))
            .WithAutomaticReconnect(options.ReconnectDelays.ToArray())
            .Build();
        _connection.Reconnecting += OnReconnectingAsync;
        _connection.Reconnected += OnReconnectedAsync;
        _connection.Closed += OnClosedAsync;
    }

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public event EventHandler? Reconnected;

    public EnvironmentConnectionState State { get; private set; } = EnvironmentConnectionState.Disconnected;

    internal HubConnection Connection => _connection;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection.State == HubConnectionState.Connected)
            {
                return;
            }

            if (_connection.State == HubConnectionState.Reconnecting)
            {
                await _connection.StopAsync(cancellationToken).ConfigureAwait(false);
            }

            SetState(EnvironmentConnectionState.Connecting);
            try
            {
                await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
                SetState(EnvironmentConnectionState.Authenticating);
            }
            catch (Exception exception) when (LooksLikeAuthenticationFailure(exception))
            {
                SetState(EnvironmentConnectionState.AuthenticationRequired, exception);
                throw;
            }
            catch (Exception exception)
            {
                SetState(EnvironmentConnectionState.Disconnected, exception);
                throw;
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _connection.StopAsync(cancellationToken).ConfigureAwait(false);
        SetState(EnvironmentConnectionState.Disconnected);
    }

    internal Task WaitUntilConnectedAsync(CancellationToken cancellationToken) =>
        _connection.State == HubConnectionState.Connected
            ? Task.CompletedTask
            : _connected.Task.WaitAsync(cancellationToken);

    internal void MarkSynchronizing() => SetState(EnvironmentConnectionState.Synchronizing);

    internal void MarkConnected()
    {
        SetState(EnvironmentConnectionState.Connected);
        _connected.TrySetResult();
    }

    internal void MarkIncompatible(Exception exception) =>
        SetState(EnvironmentConnectionState.Incompatible, exception);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Reconnecting -= OnReconnectingAsync;
        _connection.Reconnected -= OnReconnectedAsync;
        _connection.Closed -= OnClosedAsync;
        await _connection.DisposeAsync().ConfigureAwait(false);
        _connectionGate.Dispose();
    }

    private Task OnReconnectingAsync(Exception? exception)
    {
        _connected = CreateConnectedSignal();
        SetState(EnvironmentConnectionState.Retrying, exception);
        return Task.CompletedTask;
    }

    private Task OnReconnectedAsync(string? connectionId)
    {
        SetState(EnvironmentConnectionState.Synchronizing);
        Reconnected?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private Task OnClosedAsync(Exception? exception)
    {
        _connected = CreateConnectedSignal();
        SetState(EnvironmentConnectionState.Disconnected, exception);
        return Task.CompletedTask;
    }

    private void SetState(EnvironmentConnectionState state, Exception? error = null)
    {
        if (State == state && error is null)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state, error));
    }

    private static TaskCompletionSource CreateConnectedSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool LooksLikeAuthenticationFailure(Exception exception) =>
        exception.Message.Contains("401", StringComparison.Ordinal) ||
        exception.Message.Contains("403", StringComparison.Ordinal) ||
        exception.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase);
}
