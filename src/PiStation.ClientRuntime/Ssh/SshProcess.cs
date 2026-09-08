using System.Diagnostics;
using System.Text;

namespace PiStation.ClientRuntime.Ssh;

internal interface ISshProcess : IAsyncDisposable
{
    TextReader Output { get; }
    bool HasExited { get; }
    string FailureMessage { get; }
    bool AuthenticationFailed => false;
    bool HostKeyVerificationFailed => false;
    TextWriter Input => throw new NotSupportedException("This SSH process has no input stream.");
    Task WaitForOutputAsync() => Task.CompletedTask;
}

internal sealed class SshProcess : ISshProcess
{
    private readonly Process _process;
    private readonly Task _errors;
    private readonly StringBuilder _error = new();
    private readonly object _errorGate = new();
    private readonly bool _forwardOnly;
    private readonly SshProcessJob? _job;

    public SshProcess(ProcessStartInfo start)
    {
        _forwardOnly = start.ArgumentList.Contains("-N");
        try { _process = Process.Start(start) ?? throw new InvalidOperationException("OpenSSH did not start."); }
        catch (System.ComponentModel.Win32Exception exception)
        { throw new InvalidOperationException("OpenSSH Client (ssh.exe) is unavailable. Install the Windows OpenSSH Client optional feature.", exception); }
        finally { start.Environment.Remove("PISTATION_SSH_AUTH_SECRET"); }
        try
        {
            if (OperatingSystem.IsWindows()) _job = SshProcessJob.Attach(_process);
        }
        catch
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.Dispose();
            throw;
        }
        _errors = DrainErrorsAsync();
    }

    public TextReader Output => _process.StandardOutput;
    public TextWriter Input => _process.StandardInput;
    public bool HasExited => _process.HasExited;
    public Task WaitForOutputAsync() => _errors;
    public bool HostKeyVerificationFailed
    {
        get
        {
            lock (_errorGate)
                return _error.ToString().Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase) ||
                    _error.ToString().Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase);
        }
    }
    public bool AuthenticationFailed
    {
        get
        {
            lock (_errorGate)
                return _error.ToString().Contains("Permission denied (", StringComparison.OrdinalIgnoreCase) ||
                    _error.ToString().Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
                    _error.ToString().Contains("Too many authentication failures", StringComparison.OrdinalIgnoreCase);
        }
    }
    public string FailureMessage
    {
        get
        {
            string message;
            lock (_errorGate) message = _error.ToString();
            // Never surface remote stderr verbatim: commands and remote tools can echo secrets.
            if (HostKeyVerificationFailed)
                return "SSH host key is unknown or changed. Verify the host fingerprint with its administrator and connect using ssh in a terminal first; PiStation will not bypass verification.";
            if (message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                return "SSH authentication failed. Check the username, key/agent, or password, and whether the host permits password authentication.";
            if (message.Contains("Address already in use", StringComparison.OrdinalIgnoreCase) || message.Contains("cannot listen to port", StringComparison.OrdinalIgnoreCase))
                return "The SSH forwarding port is unavailable. Close and reopen this connection to allocate a new local port.";
            if (message.Contains("Could not resolve hostname", StringComparison.OrdinalIgnoreCase))
                return "The SSH host or config alias could not be resolved.";
            if (message.Contains("node", StringComparison.OrdinalIgnoreCase) || message.Contains("Pi installation", StringComparison.OrdinalIgnoreCase))
                return "Pi or Node could not be found on the remote host. Check its non-interactive SSH PATH or supply an explicit Pi executable.";
            if (message.Contains("PISTATION_INSTALL_FAILED", StringComparison.Ordinal))
                return "The bundled SSH host could not be installed. Check free space and write access to the remote account's LocalAppData. The running host and its data were not replaced.";
            return "SSH could not connect or start the host. Check reachability, key authentication, the remote PiStation.Server.exe path, and Pi installation using ssh in a terminal.";
        }
    }

    private async Task DrainErrorsAsync()
    {
        var buffer = new char[1024];
        try
        {
            int read;
            while ((read = await _process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                lock (_errorGate)
                {
                    _error.Append(buffer, 0, read);
                    if (_error.Length > 8192) _error.Remove(0, _error.Length - 8192);
                }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try
                {
                    if (_forwardOnly) _process.Kill(entireProcessTree: true);
                    else
                    {
                        try
                        {
                            await _process.StandardInput.WriteLineAsync("stop".AsMemory(), timeout.Token).ConfigureAwait(false);
                            _process.StandardInput.Close();
                        }
                        catch (IOException) { }
                        catch (InvalidOperationException) { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
                    }
                    await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Only the exact process captured at spawn, never name/PID discovery.
                    if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            await _errors.ConfigureAwait(false);
        }
        finally { _job?.Dispose(); _process.Dispose(); }
    }
}
