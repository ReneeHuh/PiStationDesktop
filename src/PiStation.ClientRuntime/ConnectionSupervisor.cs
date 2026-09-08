using System.Net;
using System.Security.Authentication;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime;

/// <summary>Owns transport preparation, validation, retry, and application readiness.</summary>
public sealed class ConnectionSupervisor : IAsyncDisposable
{
    private readonly SemaphoreSlim _control = new(1, 1);
    private readonly object _sync = new();
    private ClientRuntimeOptions _options;
    private readonly Func<CancellationToken, Task>? _synchronize;
    private HubConnection _connection;
    private TaskCompletionSource _ready = Signal();
    private CancellationTokenSource? _session;
    private Task _run = Task.CompletedTask;
    private bool _disposed;
    private int _networkNudge;

    public ConnectionSupervisor(ClientRuntimeOptions options, Func<CancellationToken, Task>? synchronize = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _synchronize = synchronize;
        _connection = CreateConnection(options);
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    private static HubConnection CreateConnection(ClientRuntimeOptions options, Action? certificateRejected = null) => new HubConnectionBuilder()
            .WithUrl(options.HubAddress, connection =>
            {
                connection.Headers["Authorization"] = $"Bearer {options.BearerCredential}";
                connection.HttpMessageHandlerFactory = handler =>
                {
                    if (handler is not HttpClientHandler httpHandler)
                        throw new InvalidOperationException("The HTTP transport cannot enforce remote certificate identity.");
                    httpHandler.AllowAutoRedirect = false;
                    httpHandler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                    {
                        var accepted = options.CertificateFingerprint is { } fingerprint
                            ? RemoteTransport.Matches(certificate, fingerprint) : errors == System.Net.Security.SslPolicyErrors.None;
                        if (!accepted) certificateRejected?.Invoke();
                        return accepted;
                    };
                    return handler;
                };
                if (options.CertificateFingerprint is { } pin)
                    connection.WebSocketConfiguration = socket => socket.RemoteCertificateValidationCallback =
                        (_, certificate, _, _) =>
                        {
                            var accepted = RemoteTransport.Matches(certificate, pin);
                            if (!accepted) certificateRejected?.Invoke();
                            return accepted;
                        };
            })
            .AddJsonProtocol(json => json.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ProtocolJsonContext.Default))
            .Build();

    internal async Task ReconfigureAsync(ClientRuntimeOptions options, CancellationToken cancellationToken)
    {
        options.Validate();
        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopSessionAsync().ConfigureAwait(false);
            await DisposeTransportAsync().ConfigureAwait(false);
            _options = options;
            _connection = CreateConnection(options);
            SetState(EnvironmentConnectionState.Disconnected);
        }
        finally { _control.Release(); }
    }

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
    public EnvironmentConnectionState State { get; private set; } = EnvironmentConnectionState.Disconnected;
    public ConnectionDiagnostics Diagnostics { get; private set; } = new(EnvironmentConnectionState.Disconnected);
    internal HubConnection Connection => _connection;

    private void OnNetworkChanged(object? sender, EventArgs args) => NotifyNetworkRestored();

    public void NotifyNetworkRestored()
    {
        if (Interlocked.Exchange(ref _networkNudge, 1) != 0) return;
        _ = NudgeAsync();
    }

    private async Task NudgeAsync()
    {
        try
        {
            await Task.Delay(250).ConfigureAwait(false);
            await _control.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || _session is null || _session.IsCancellationRequested || _run.IsCompleted) return;
                if (State == EnvironmentConnectionState.Connected)
                {
                    using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        await _connection.InvokeAsync<PiStation.Protocol.Models.EnvironmentDescriptor>("GetEnvironmentDescriptor", probe.Token).ConfigureAwait(false);
                        return;
                    }
                    catch (Exception) { }
                }
                await StopSessionAsync().ConfigureAwait(false);
                var session = _session = new CancellationTokenSource();
                var first = Signal();
                _run = Task.Run(() => RunAsync(first, session.Token));
                _ = ObserveAttemptAsync(first.Task);
            }
            finally { _control.Release(); }
        }
        finally { Interlocked.Exchange(ref _networkNudge, 0); }
    }

    private static async Task ObserveAttemptAsync(Task task)
    {
        try { await task.ConfigureAwait(false); } catch (Exception) { }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Task firstAttempt;
        CancellationTokenSource session;
        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State == EnvironmentConnectionState.Connected && _connection.State == HubConnectionState.Connected) return;
            await StopSessionAsync().ConfigureAwait(false);
            session = _session = new CancellationTokenSource();
            var first = Signal();
            firstAttempt = first.Task;
            _run = Task.Run(() => RunAsync(first, session.Token), CancellationToken.None);
        }
        finally { _control.Release(); }
        using var registration = cancellationToken.Register(() => { try { session.Cancel(); } catch (ObjectDisposedException) { } });
        await firstAttempt.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopSessionAsync().ConfigureAwait(false);
            SetState(EnvironmentConnectionState.Disconnected);
        }
        finally { _control.Release(); }
    }

    internal Task WaitUntilConnectedAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _ready.Task.WaitAsync(cancellationToken);
        }
    }

    private async Task RunAsync(TaskCompletionSource first, CancellationToken cancellationToken)
    {
        var attempt = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Exception? failure = null;
                var certificateRejected = false;
                var connectedAt = DateTimeOffset.MinValue;
                try
                {
                    SetState(attempt == 0 ? EnvironmentConnectionState.Connecting : EnvironmentConnectionState.Retrying, attempt: attempt);
                    if (_options.EnsureTransportAsync is { } prepare) await prepare(cancellationToken).ConfigureAwait(false);
                    await DisposeTransportAsync().ConfigureAwait(false);
                    var connection = CreateConnection(_options, () => certificateRejected = true);
                    var closed = ClosedSignal();
                    connection.Closed += exception => OnClosedAsync(connection, closed, exception);
                    _connection = connection;
                    await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
                    SetState(EnvironmentConnectionState.Authenticating, attempt: attempt);
                    if (_synchronize is not null)
                    {
                        SetState(EnvironmentConnectionState.Synchronizing, attempt: attempt);
                        await _synchronize(cancellationToken).ConfigureAwait(false);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (closed.Task.IsCompleted || _connection.State != HubConnectionState.Connected)
                        throw new IOException("The connection closed during synchronization.");
                    connectedAt = _options.TimeProvider.GetUtcNow();
                    SetState(EnvironmentConnectionState.Connected, lastConnected: connectedAt);
                    lock (_sync) _ready.TrySetResult();
                    first.TrySetResult();
                    failure = await closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception exception) { failure = exception; }
                finally
                {
                    InvalidateReadiness();
                    await StopTransportAsync().ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var category = certificateRejected ? ConnectionFailure.Certificate : Classify(failure);
                if (category is ConnectionFailure.Authentication or ConnectionFailure.Certificate or
                    ConnectionFailure.Identity or ConnectionFailure.Protocol)
                {
                    SetState(category == ConnectionFailure.Authentication ? EnvironmentConnectionState.AuthenticationRequired :
                        category == ConnectionFailure.Protocol ? EnvironmentConnectionState.Incompatible : EnvironmentConnectionState.TrustRequired,
                        failure, category, attempt);
                    first.TrySetException(failure ?? new IOException("Connection validation failed."));
                    return;
                }

                if (connectedAt != DateTimeOffset.MinValue && _options.TimeProvider.GetUtcNow() - connectedAt >= TimeSpan.FromSeconds(30)) attempt = 0;
                var delay = _options.ReconnectDelays[Math.Min(attempt, _options.ReconnectDelays.Count - 1)];
                if (_options.RetryJitter > 0 && delay > TimeSpan.Zero)
                    delay *= 1 + (Random.Shared.NextDouble() * 2 - 1) * _options.RetryJitter;
                attempt++;
                SetState(EnvironmentConnectionState.Retrying, failure, category, attempt, _options.TimeProvider.GetUtcNow() + delay);
                first.TrySetException(failure ?? new IOException("The host closed the connection."));
                await Task.Delay(delay, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            first.TrySetCanceled(cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(true));
            if (cancellationToken.IsCancellationRequested) SetState(EnvironmentConnectionState.Disconnected);
        }
    }

    private void InvalidateReadiness()
    {
        lock (_sync)
        {
            // Pending waiters survive every retry. Only a completed generation needs a new signal.
            if (_ready.Task.IsCompleted) _ready = Signal();
        }
    }

    private Task OnClosedAsync(HubConnection connection, TaskCompletionSource<Exception?> closed, Exception? exception)
    {
        if (ReferenceEquals(_connection, connection)) InvalidateReadiness();
        closed.TrySetResult(exception);
        return Task.CompletedTask;
    }

    private static bool IsTransportShutdownFailure(Exception error) => error is
        IOException or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException or
        HttpRequestException or OperationCanceledException;

    private async Task StopTransportAsync()
    {
        try { await _connection.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (IsTransportShutdownFailure(error))
        {
            // A reset socket is already closed. Its receive error must not terminate
            // the recovery loop or make an explicit disconnect fail.
        }
    }

    private async Task DisposeTransportAsync()
    {
        try { await _connection.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (IsTransportShutdownFailure(error)) { }
    }

    private async Task StopSessionAsync()
    {
        if (_session is not { } session) return;
        await session.CancelAsync().ConfigureAwait(false);
        await _run.ConfigureAwait(false);
        session.Dispose();
        _session = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _control.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            await StopSessionAsync().ConfigureAwait(false);
            lock (_sync) _ready.TrySetCanceled();
            await DisposeTransportAsync().ConfigureAwait(false);
        }
        finally { _control.Release(); }
    }

    private void SetState(EnvironmentConnectionState state, Exception? error = null,
        ConnectionFailure failure = ConnectionFailure.None, int attempt = 0, DateTimeOffset? nextRetry = null, DateTimeOffset? lastConnected = null)
    {
        State = state;
        Diagnostics = new(state, failure, attempt, nextRetry, lastConnected ?? Diagnostics.LastConnected);
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state, error, Diagnostics));
    }

    internal static ConnectionFailure Classify(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ConnectionValidationException validation) return validation.Failure;
            if (current is Ssh.SshAuthenticationException) return ConnectionFailure.Authentication;
            if (current is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }) return ConnectionFailure.Authentication;
            // TLS EOF/handshake interruptions also use AuthenticationException. Only an
            // actual certificate validation failure may permanently block recovery.
            if (current is TimeoutException) return ConnectionFailure.Timeout;
        }
        return ConnectionFailure.Network;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<Exception?> ClosedSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
