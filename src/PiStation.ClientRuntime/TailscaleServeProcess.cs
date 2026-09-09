using System.Diagnostics;
using PiStation.ClientRuntime.Ssh;

namespace PiStation.ClientRuntime;

internal interface ITailscaleServeProcess : IAsyncDisposable
{
    bool HasExited { get; }
}

// A foreground Serve configuration belongs to this CLI connection. The Windows job
// closes the connection even if the desktop crashes; no persistent Serve reset is needed.
internal sealed class TailscaleServeProcess : ITailscaleServeProcess
{
    private readonly Process _process;
    private readonly SshProcessJob? _job;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenRegistration _stopping;
    private readonly Task<string> _output;
    private readonly Task<string> _errors;
    private int _disposed;

    public TailscaleServeProcess(IReadOnlyList<string> arguments) : this(FindExecutable(), arguments) { }

    internal TailscaleServeProcess(string executable, IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Tailscale sharing requires Windows.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        _process = Process.Start(start) ?? throw new InvalidOperationException("Tailscale could not start.");
        try
        {
            _job = SshProcessJob.Attach(_process);
            _process.StandardInput.Close();
        }
        catch
        {
            Kill(); _job?.Dispose(); _process.Dispose(); _lifetime.Dispose();
            throw;
        }
        _stopping = _lifetime.Token.Register(Kill);
        _output = TailscaleDiscovery.ReadBoundedAsync(_process.StandardOutput, 4 * 1024 * 1024, _lifetime);
        _errors = TailscaleDiscovery.ReadBoundedAsync(_process.StandardError, 32 * 1024, _lifetime);
    }

    public bool HasExited => _lifetime.IsCancellationRequested || _process.HasExited;

    public static Task<string> QueryAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return QueryAsync(FindExecutable(), arguments, cancellationToken);
    }

    internal static async Task<string> QueryAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var process = new TailscaleServeProcess(executable, arguments);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        await Task.WhenAll(process._output, process._errors, process._process.WaitForExitAsync(deadline.Token)).WaitAsync(deadline.Token).ConfigureAwait(false);
        if (process._process.ExitCode != 0) throw new InvalidOperationException("Tailscale Serve status is unavailable. Check the Tailscale Windows app and service.");
        return await process._output.ConfigureAwait(false);
    }

    private static string FindExecutable() => TailscaleDiscovery.FindExecutable()
        ?? throw new InvalidOperationException("Install and connect Tailscale on this computer first.");

    private void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            _job?.Dispose();
            try { await Task.WhenAll(_output, _errors, _process.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or IOException or TimeoutException) { }
        }
        finally { _stopping.Dispose(); _process.Dispose(); _lifetime.Dispose(); }
    }
}
