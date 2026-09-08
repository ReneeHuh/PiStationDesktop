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
    bool ForwardingFailed => false;
    SshSetupFailure? SetupFailure => null;
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
    public bool ForwardingFailed { get { lock (_errorGate) return _forwardOnly && SshSetupDiagnostics.ForwardingDenied(_error.ToString()); } }
    public SshSetupFailure SetupFailure { get { lock (_errorGate) return SshSetupDiagnostics.FromStandardError(_error.ToString(), _forwardOnly); } }
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
    public string FailureMessage => SetupFailure.Message;

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
