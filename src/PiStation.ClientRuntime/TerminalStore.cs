using System.Text;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Streaming;

namespace PiStation.ClientRuntime;

public sealed class TerminalChangedEventArgs(
    TerminalSessionDescriptor? descriptor,
    string output,
    string? appendedOutput = null,
    bool outputWasReset = false) : EventArgs
{
    public TerminalSessionDescriptor? Descriptor { get; } = descriptor;

    public string Output { get; } = output;

    public string? AppendedOutput { get; } = appendedOutput;

    public bool OutputWasReset { get; } = outputWasReset;
}

public sealed class TerminalStore(TerminalSessionId terminalSessionId)
{
    private const int OutputCharacterLimit = 1024 * 1024;
    private readonly object _gate = new();
    private readonly StringBuilder _output = new();
    private TerminalSessionDescriptor? _descriptor;
    private bool _isSynchronized;

    public bool IsSynchronized { get { lock (_gate) return _isSynchronized; } }

    internal void SetSynchronized(bool value)
    {
        lock (_gate) _isSynchronized = value;
    }

    public event EventHandler<TerminalChangedEventArgs>? Changed;

    public TerminalSessionId TerminalSessionId { get; } = terminalSessionId;

    public TerminalSessionDescriptor? Descriptor
    {
        get
        {
            lock (_gate)
            {
                return _descriptor;
            }
        }
    }

    public string Output
    {
        get
        {
            lock (_gate)
            {
                return _output.ToString();
            }
        }
    }

    public TerminalCursor? Cursor
    {
        get
        {
            lock (_gate)
            {
                return _descriptor is null ? null : new TerminalCursor(_descriptor.Sequence, _descriptor.Epoch);
            }
        }
    }

    public ProjectionApplyResult Apply(TerminalEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.TerminalSessionId != TerminalSessionId)
        {
            throw new ArgumentException("The envelope belongs to another terminal session.", nameof(envelope));
        }

        TerminalChangedEventArgs? changed = null;
        ProjectionApplyResult result;
        lock (_gate)
        {
            result = envelope switch
            {
                TerminalResyncRequiredEnvelope => ProjectionApplyResult.ResyncRequired,
                TerminalSnapshotEnvelope snapshot => ApplySnapshot(snapshot, out changed),
                TerminalOutputEnvelope output => ApplyOutput(output, out changed),
                TerminalStateEnvelope state => ApplyState(state, out changed),
                _ => ProjectionApplyResult.Ignored,
            };
        }

        if (changed is not null)
        {
            Changed?.Invoke(this, changed);
        }

        return result;
    }

    public void Reset()
    {
        var changed = false;
        lock (_gate)
        {
            changed = _descriptor is not null || _output.Length > 0;
            _descriptor = null;
            _output.Clear();
        }

        if (changed)
        {
            Changed?.Invoke(this, new TerminalChangedEventArgs(null, string.Empty));
        }
    }

    private ProjectionApplyResult ApplySnapshot(
        TerminalSnapshotEnvelope snapshot,
        out TerminalChangedEventArgs? changed)
    {
        changed = null;
        if (_descriptor is not null && _descriptor.Epoch == snapshot.Descriptor.Epoch && _descriptor.Sequence >= snapshot.Sequence)
        {
            return ProjectionApplyResult.Ignored;
        }

        _descriptor = snapshot.Descriptor;
        _output.Clear();
        AppendBounded(snapshot.BufferedOutput);
        changed = CreateChanged(outputWasReset: true);
        return ProjectionApplyResult.Applied;
    }

    private ProjectionApplyResult ApplyOutput(
        TerminalOutputEnvelope output,
        out TerminalChangedEventArgs? changed)
    {
        changed = null;
        if (_descriptor is null)
        {
            return ProjectionApplyResult.ResyncRequired;
        }

        if (output.Sequence <= _descriptor.Sequence)
        {
            return ProjectionApplyResult.Ignored;
        }

        if (output.Sequence != _descriptor.Sequence.Next())
        {
            return ProjectionApplyResult.ResyncRequired;
        }

        _descriptor = _descriptor with { Sequence = output.Sequence };
        AppendBounded(output.Text);
        changed = CreateChanged(appendedOutput: output.Text);
        return ProjectionApplyResult.Applied;
    }

    private ProjectionApplyResult ApplyState(
        TerminalStateEnvelope state,
        out TerminalChangedEventArgs? changed)
    {
        changed = null;
        if (_descriptor is null || _descriptor.Epoch != state.Descriptor.Epoch)
        {
            return ProjectionApplyResult.ResyncRequired;
        }

        if (state.Sequence <= _descriptor.Sequence)
        {
            return ProjectionApplyResult.Ignored;
        }

        if (state.Sequence != _descriptor.Sequence.Next())
        {
            return ProjectionApplyResult.ResyncRequired;
        }

        _descriptor = state.Descriptor;
        changed = CreateChanged();
        return ProjectionApplyResult.Applied;
    }

    private void AppendBounded(string text)
    {
        _output.Append(text);
        if (_output.Length > OutputCharacterLimit)
        {
            _output.Remove(0, _output.Length - OutputCharacterLimit);
        }
    }

    private TerminalChangedEventArgs CreateChanged(
        string? appendedOutput = null,
        bool outputWasReset = false) => new(
        _descriptor,
        _output.ToString(),
        appendedOutput,
        outputWasReset);
}
