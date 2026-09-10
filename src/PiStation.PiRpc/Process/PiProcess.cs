using System.Diagnostics;
using System.Text;
using PiStation.PiRpc.Transport;

namespace PiStation.PiRpc.Process;

public sealed class PiProcess : IAsyncDisposable
{
    private readonly CancellationTokenSource _stderrCancellation = new();
    private readonly BoundedTextBuffer _stderr;
    private readonly Task<int?> _exitTask;
    private readonly System.Diagnostics.Process _process;
    private readonly TimeSpan _shutdownTimeout;
    private readonly Task _stderrTask;
    private int _disposeStarted;

    internal PiProcess(
        System.Diagnostics.Process process,
        PiRpcConnection connection,
        TimeSpan shutdownTimeout,
        int standardErrorCharacterLimit)
    {
        _process = process;
        StartedUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        Connection = connection;
        _shutdownTimeout = shutdownTimeout;
        _stderr = new BoundedTextBuffer(standardErrorCharacterLimit);
        _stderrTask = PumpStandardErrorAsync();
        _exitTask = MonitorExitAsync();
    }

    public PiRpcConnection Connection { get; }

    public int Id => _process.Id;
    public long StartedUtcTicks { get; }

    public Task<int?> Exit => _exitTask;

    public string StandardError => _stderr.ToString();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        await Connection.DisposeAsync().ConfigureAwait(false);

        try
        {
            _process.StandardInput.Close();
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            await _exitTask.WaitAsync(_shutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            TryKill();
            await _exitTask.ConfigureAwait(false);
        }

        _stderrCancellation.Cancel();
        try
        {
            await _stderrTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stderrCancellation.Dispose();
        _process.Dispose();
    }

    private async Task<int?> MonitorExitAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        var exitCode = _process.ExitCode;
        if (Volatile.Read(ref _disposeStarted) == 0)
        {
            Connection.ReportProcessExit(exitCode, StandardError);
        }

        return exitCode;
    }

    private async Task PumpStandardErrorAsync()
    {
        var buffer = new char[2048];
        while (true)
        {
            var read = await _process.StandardError.ReadAsync(buffer, _stderrCancellation.Token).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            _stderr.Append(buffer.AsSpan(0, read));
        }
    }

    private void TryKill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class BoundedTextBuffer
    {
        private readonly int _capacity;
        private readonly object _lock = new();
        private readonly StringBuilder _text = new();

        public BoundedTextBuffer(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
            _capacity = capacity;
        }

        public void Append(ReadOnlySpan<char> value)
        {
            lock (_lock)
            {
                if (value.Length >= _capacity)
                {
                    _text.Clear();
                    _text.Append(value[^_capacity..]);
                    return;
                }

                var overflow = _text.Length + value.Length - _capacity;
                if (overflow > 0)
                {
                    _text.Remove(0, overflow);
                }

                _text.Append(value);
            }
        }

        public override string ToString()
        {
            lock (_lock)
            {
                return _text.ToString();
            }
        }
    }
}
