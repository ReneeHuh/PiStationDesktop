using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime.Ssh;

public sealed class ManagedSshConnection : IAsyncDisposable
{
    private readonly SshConnectionProfile _profile;
    private readonly Func<ProcessStartInfo, ISshProcess> _spawn;
    private readonly Func<Uri, SshHostInfo, CancellationToken, Task> _probe;
    private readonly IProgress<string>? _progress;
    private readonly Func<SshPasswordRequest, CancellationToken, Task<string?>>? _requestPassword;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private ISshProcess? _control;
    private ISshProcess? _forward;
    private Task? _forwardOutput;
    private Task? _controlOutput;
    private SshHostInfo? _info;
    private bool _disposed;
    private string? _authSecret;
    private SshHostBundle? _bundle;

    public ManagedSshConnection(SshConnectionProfile profile, IProgress<string>? progress = null,
        Func<SshPasswordRequest, CancellationToken, Task<string?>>? requestPassword = null)
        : this(profile, start => new SshProcess(start), ProbeAsync, progress, requestPassword) { }

    internal ManagedSshConnection(SshConnectionProfile profile, Func<ProcessStartInfo, ISshProcess> spawn,
        Func<Uri, SshHostInfo, CancellationToken, Task> probe, IProgress<string>? progress = null,
        Func<SshPasswordRequest, CancellationToken, Task<string?>>? requestPassword = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        _profile = profile;
        _spawn = spawn;
        _probe = probe;
        _progress = progress;
        _requestPassword = requestPassword;
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Server.ExclusiveAddressUse = true;
        reservation.Start();
        Address = new Uri($"https://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}/");
    }

    public Uri Address { get; }
    public SshHostInfo Info => _info ?? throw new InvalidOperationException("The SSH host is not connected.");

    public static Task CheckBundledHostAsync(CancellationToken cancellationToken = default) => SshHostBundle.LoadAsync(cancellationToken);

    public ClientRuntimeOptions CreateOptions() => new()
    {
        HubAddress = new(Address, "/environment"), BearerCredential = Info.BearerCredential,
        CertificateFingerprint = Info.CertificateFingerprint, ExpectedEnvironmentId = Info.EnvironmentId,
        ClientId = _profile.ClientId, EnsureTransportAsync = EnsureConnectedAsync,
    };

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        var repairingForward = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_control is { HasExited: false } && _forward is { HasExited: false } && _info is not null)
            {
                try { await _probe(Address, _info, deadline.Token).ConfigureAwait(false); return; }
                catch (HttpRequestException) { }
                catch (TimeoutException) { }
            }
            if (_control is { HasExited: false } && _info is not null &&
                (_info.StartedByConnection || _forward is { HasExited: true }))
            {
                // A dropped forwarding process is not permission to stop a healthy host or
                // its active Pi turn. Retain the SSH control session throughout tunnel repair.
                repairingForward = true;
                _progress?.Report("Restoring the SSH tunnel; keeping the running host…");
                await StopForwardAsync().ConfigureAwait(false);
                await StartForwardAsync(_info, deadline.Token).ConfigureAwait(false);
                _progress?.Report("SSH tunnel restored. The host was not restarted.");
                return;
            }
            _progress?.Report(_info is null ? "Connecting over SSH…" : "Reconnecting SSH and restoring the tunnel…");
            // A reused desktop may have exited while its attach session still waits on stdin.
            // If the forward is alive but health is gone, rediscover instead of retrying a
            // stale port forever. Ending this non-owning control does not stop the host.
            await StopProcessesAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(_profile.ServerPath))
                _bundle ??= await SshHostBundle.LoadAsync(deadline.Token).ConfigureAwait(false);
            var info = await StartControlAsync(deadline.Token).ConfigureAwait(false);
            info.Validate();
            if ((_profile.ExpectedEnvironmentId is { } expected && expected != info.EnvironmentId) ||
                (_info is not null && _info.EnvironmentId != info.EnvironmentId))
                throw new InvalidOperationException("The SSH host environment identity changed. Check the host and data directory; forget and add the connection only if the change was intentional.");
            if (_info is not null && (_info.BearerCredential != info.BearerCredential || _info.CertificateFingerprint != info.CertificateFingerprint))
                throw new InvalidOperationException("The SSH host security identity changed. Close and reopen the connection to obtain its new credentials over SSH.");
            _controlOutput = DrainAsync(_control!.Output);
            _progress?.Report(info.StartedByConnection ? "Started Windows host; opening the encrypted tunnel…" : "Reusing running Windows host; opening the encrypted tunnel…");
            await StartForwardAsync(info, deadline.Token).ConfigureAwait(false);
            _info = info;
            var versionNotice = info.ServerVersion == Protocol.ProductVersion.Current ? string.Empty :
                $" Host version: {info.ServerVersion ?? "unknown"}; desktop: {Protocol.ProductVersion.Current}. Use Update / reconnect for an owned server; update a reused host at its source.";
            _progress?.Report((info.StartedByConnection ? "SSH connected. This connection owns the host." :
                $"SSH connected to the running {info.HostKind}. Its owner controls its lifetime.") + versionNotice);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            await CleanupFailedAttemptAsync(repairingForward).ConfigureAwait(false);
            throw new TimeoutException("The SSH host did not become ready in time. Check the remote executable, Pi installation and port-forwarding permissions.");
        }
        catch { await CleanupFailedAttemptAsync(repairingForward).ConfigureAwait(false); throw; }
        finally { _gate.Release(); }
    }

    private Task<SshHostInfo> StartControlAsync(CancellationToken cancellationToken) => WithAuthenticationAsync(async () =>
    {
        _control = _spawn(SshCommands.Control(_profile, _authSecret));
        try
        {
            if (_bundle is not null)
            {
                try
                {
                    await _control.Input.WriteLineAsync(_bundle.EncodedBootstrap(_profile).AsMemory(), cancellationToken).ConfigureAwait(false);
                    await _control.Input.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (IOException) { throw await FailureAsync(_control, cancellationToken).ConfigureAwait(false); }
            }
            var info = await ReadHandshakeAsync(_control.Output, cancellationToken, async (hash, token) =>
            {
                if (_bundle is null || hash != _bundle.Hash) throw new InvalidOperationException("Unexpected SSH package request.");
                _progress?.Report("Installing the matching Windows host over SSH…");
                await _bundle.SendAsync(_control.Input, token).ConfigureAwait(false);
                _progress?.Report("Host transferred; verifying and starting or reusing the environment…");
            }).ConfigureAwait(false);
            return info ?? throw await FailureAsync(_control, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            var failed = _control; _control = null;
            await failed.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }, cancellationToken);

    private async Task StartForwardAsync(SshHostInfo info, CancellationToken cancellationToken)
    {
        await WithAuthenticationAsync(async () =>
        {
            _forward = _spawn(SshCommands.Forward(_profile, Address.Port, info.Port, _authSecret));
            _forwardOutput = DrainAsync(_forward.Output);
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_control is null) throw new InvalidOperationException("The SSH control session ended.");
                    if (_control.HasExited || _forward.HasExited)
                        throw await FailureAsync(_control.HasExited ? _control : _forward, cancellationToken).ConfigureAwait(false);
                    try { await _probe(Address, info, cancellationToken).ConfigureAwait(false); return true; }
                    catch (HttpRequestException) { }
                    catch (TimeoutException) { }
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                }
            }
            catch { await StopForwardAsync().ConfigureAwait(false); throw; }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> WithAuthenticationAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await action().ConfigureAwait(false); }
            catch (SshAuthenticationException) when (_requestPassword is not null && attempt < 2)
            {
                _authSecret = null;
                _progress?.Report("SSH needs a password or key passphrase…");
                _authSecret = await _requestPassword(new(_profile.Target, attempt + 1), cancellationToken).ConfigureAwait(false);
                if (_authSecret is null) throw new InvalidOperationException("SSH authentication canceled.");
                if (_authSecret.Length > 4096 || _authSecret.Contains('\n', StringComparison.Ordinal) || _authSecret.Contains('\r', StringComparison.Ordinal))
                {
                    _authSecret = null;
                    throw new ArgumentException("The SSH password must be a single line of at most 4096 characters.");
                }
            }
        }
    }

    private static async Task<Exception> FailureAsync(ISshProcess process, CancellationToken cancellationToken)
    {
        await process.WaitForOutputAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return process.AuthenticationFailed ? new SshAuthenticationException(process.FailureMessage) : new InvalidOperationException(process.FailureMessage);
    }

    private Task CleanupFailedAttemptAsync(bool repairingForward) =>
        repairingForward && !_lifetime.IsCancellationRequested && _control is { HasExited: false }
            ? StopForwardAsync() : StopProcessesAsync();

    internal static async Task<SshHostInfo?> ReadHandshakeAsync(TextReader reader, CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? sendPackage = null)
    {
        const string prefix = "PISTATION_SSH ";
        var line = new System.Text.StringBuilder();
        var buffer = new char[1];
        var packageSent = false;
        for (var total = 0; total < 32768; total++)
        {
            if (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) == 0) return null;
            if (buffer[0] == '\n')
            {
                var value = line.ToString().TrimEnd('\r');
                if (value.StartsWith("PISTATION_PACKAGE ", StringComparison.Ordinal))
                {
                    if (sendPackage is null || packageSent) throw new InvalidOperationException("Unexpected or repeated SSH package request.");
                    packageSent = true;
                    await sendPackage(value[18..], cancellationToken).ConfigureAwait(false);
                }
                if (value.StartsWith(prefix, StringComparison.Ordinal))
                    return JsonSerializer.Deserialize(value.AsSpan(prefix.Length), ProtocolJsonContext.Default.SshHostInfo);
                line.Clear();
            }
            else line.Append(buffer[0]);
        }
        throw new InvalidOperationException("The SSH host returned an oversized startup response.");
    }

    private static async Task DrainAsync(TextReader reader)
    {
        var buffer = new char[1024];
        try { while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task ProbeAsync(Uri address, SshHostInfo info, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(RemoteTransport.CreateHandler(info.CertificateFingerprint)) { Timeout = TimeSpan.FromSeconds(3) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", info.BearerCredential);
        try
        {
            using var response = await http.GetAsync(new Uri(address, "/ssh/health"), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("The SSH host rejected its bootstrap credential.");
            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("The SSH tunnel is not ready."); }
    }

    private async Task StopProcessesAsync()
    {
        var control = _control; _control = null;
        try { await StopForwardAsync().ConfigureAwait(false); }
        finally { if (control is not null) await control.DisposeAsync().ConfigureAwait(false); }
        if (_controlOutput is not null) await _controlOutput.ConfigureAwait(false);
        _controlOutput = null;
    }

    private async Task StopForwardAsync()
    {
        var forward = _forward; _forward = null;
        if (forward is not null) await forward.DisposeAsync().ConfigureAwait(false);
        if (_forwardOutput is not null) await _forwardOutput.ConfigureAwait(false);
        _forwardOutput = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            try { await StopProcessesAsync().ConfigureAwait(false); }
            finally { _authSecret = null; }
        }
        finally { _gate.Release(); }
        // Keep synchronization objects valid for late SignalR callbacks observing cancellation.
    }
}
