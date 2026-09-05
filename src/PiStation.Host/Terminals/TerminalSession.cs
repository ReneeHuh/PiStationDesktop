using System.Text;
using PiStation.Host.Errors;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Models;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Terminals;

internal sealed class TerminalSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConPtyTerminalProcess _process;
    private readonly Task _lifetimeTask;
    private bool _disposed;

    public TerminalSession(
        TerminalSessionDescriptor descriptor,
        ConPtyTerminalProcess process,
        HostOptions options)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        Journal = new TerminalOutputJournal(descriptor, options);
        _lifetimeTask = RunAsync(_stopping.Token);
    }

    public TerminalOutputJournal Journal { get; }

    public TerminalSessionDescriptor Descriptor => Journal.Descriptor;

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
        await _process.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    public TerminalSessionDescriptor Resize(int columns, int rows)
    {
        EnsureRunning();
        _process.Resize(columns, rows);
        return Journal.CommitResize(columns, rows);
    }

    public async Task<TerminalSessionDescriptor> StopAsync(CancellationToken cancellationToken)
    {
        if (Descriptor.State == TerminalSessionState.Running)
        {
            await _process.StopAsync(cancellationToken).ConfigureAwait(false);
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
            await _process.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await _lifetimeTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        finally
        {
            await _process.DisposeAsync().ConfigureAwait(false);
            _stopping.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var outputTask = PumpOutputAsync(_process.Output, cancellationToken);
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
        if (Descriptor.State != TerminalSessionState.Running || _process.HasExited)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.TerminalNotRunning,
                $"Terminal session '{Descriptor.TerminalSessionId}' is not running.");
        }
    }
}
