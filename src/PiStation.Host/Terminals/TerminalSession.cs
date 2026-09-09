using System.Text;
using System.Threading.Channels;
using PiStation.Host.Errors;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Models;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Terminals;

internal sealed class TerminalSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConPtyTerminalProcess? _process;
    private readonly TerminalHistoryStore _historyStore;
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly Channel<byte> _dirty = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task _persistenceTask;
    private readonly Task _lifetimeTask;
    private bool _disposed;

    public TerminalSession(
        TerminalSessionDescriptor descriptor,
        ConPtyTerminalProcess? process,
        HostOptions options,
        TerminalHistoryStore historyStore,
        string history = "")
    {
        _process = process;
        _historyStore = historyStore;
        Journal = new TerminalOutputJournal(descriptor, options, history);
        Journal.Changed += QueuePersistence;
        _persistenceTask = PersistChangesAsync();
        _lifetimeTask = process is null ? Task.CompletedTask : RunAsync(_stopping.Token);
    }

    public TerminalOutputJournal Journal { get; }

    public TerminalSessionDescriptor Descriptor => Journal.Descriptor;
    public int? ProcessId => _process is { HasExited: false } ? _process.ProcessId : null;

    private void QueuePersistence() => _dirty.Writer.TryWrite(0);
    private async Task PersistChangesAsync()
    {
        await foreach (var signal in _dirty.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await Task.Delay(100).ConfigureAwait(false);
            while (_dirty.Reader.TryRead(out _)) { }
            try { await FlushAsync().ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning("Terminal history could not be saved: {0}", error.Message); }
        }
    }

    public async Task FlushAsync()
    {
        await _persistGate.WaitAsync().ConfigureAwait(false);
        try { await _historyStore.SaveAsync(Journal.Snapshot()).ConfigureAwait(false); }
        finally { _persistGate.Release(); }
    }

    public async Task<TerminalSnapshotEnvelope> ClearHistoryAsync()
    {
        var snapshot = Journal.ClearHistory();
        await FlushAsync().ConfigureAwait(false);
        return snapshot;
    }

    public async Task WriteAsync(string data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > TerminalDefaults.MaximumInputCharacters)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.TerminalInvalid,
                $"Terminal input cannot exceed {TerminalDefaults.MaximumInputCharacters} characters.");
        }

        EnsureRunning();
        await _process!.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    public TerminalSessionDescriptor Resize(int columns, int rows)
    {
        EnsureRunning();
        _process!.Resize(columns, rows);
        return Journal.CommitResize(columns, rows);
    }

    public async Task<TerminalSessionDescriptor> StopAsync(CancellationToken cancellationToken)
    {
        if (Descriptor.State == TerminalSessionState.Running)
        {
            await _process!.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        await _lifetimeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Descriptor;
    }

    public IAsyncEnumerable<TerminalEnvelope> SubscribeAsync(
        TerminalCursor? cursor,
        CancellationToken cancellationToken) => Journal.SubscribeAsync(cursor, cancellationToken);

    public async Task<TerminalSessionDescriptor> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _lifetimeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Descriptor;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_process is not null) await _process.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await _lifetimeTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        finally
        {
            if (_process is not null) await _process.DisposeAsync().ConfigureAwait(false);
            if (Descriptor.State == TerminalSessionState.Running)
                Journal.CommitState(TerminalSessionState.Interrupted, errorMessage: "The host stopped. Saved output is available; restart to open a fresh shell.");
            Journal.Changed -= QueuePersistence;
            _dirty.Writer.TryComplete();
            try
            {
                await _persistenceTask.ConfigureAwait(false);
                await FlushAsync().ConfigureAwait(false);
            }
            finally { _persistGate.Dispose(); _stopping.Dispose(); }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var outputTask = PumpOutputAsync(_process!.Output, cancellationToken);
        try
        {
            var exitCode = await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await outputTask.ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Closing ConPTY after process exit can complete its output pipe with a broken-pipe error.
            }

            Journal.CommitState(TerminalSessionState.Exited, exitCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Journal.CommitState(TerminalSessionState.Failed, errorMessage: exception.Message);
        }
        finally
        {
            // Stop closes ConPTY and releases the pending read. Join the pump before
            // the final history save so no late output can arrive after disposal.
            try { await outputTask.ConfigureAwait(false); }
            catch (IOException) { }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        }
    }

    private async Task PumpOutputAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[4096];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        while (true)
        {
            var read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var count = decoder.GetChars(bytes.AsSpan(0, read), chars, flush: false);
            if (count > 0)
            {
                Journal.CommitOutput(new string(chars, 0, count));
            }
        }

        var finalCount = decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, flush: true);
        if (finalCount > 0)
        {
            Journal.CommitOutput(new string(chars, 0, finalCount));
        }
    }

    private void EnsureRunning()
    {
        if (Descriptor.State != TerminalSessionState.Running || _process is null || _process.HasExited)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.TerminalNotRunning,
                $"Terminal session '{Descriptor.TerminalSessionId}' is not running.");
        }
    }
}
