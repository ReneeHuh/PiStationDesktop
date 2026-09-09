using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Terminals;

internal sealed class TerminalOutputJournal
{
    private readonly int _byteLimit;
    private readonly LinkedList<JournalEntry> _entries = [];
    private readonly int _eventLimit;
    private readonly object _gate = new();
    private readonly int _outputCharacterLimit;
    private readonly TerminalHistory _output;
    private readonly TerminalHistorySanitizer _sanitizer = new();
    private readonly int _subscriberCapacity;
    private readonly Dictionary<long, Channel<TerminalEnvelope>> _subscribers = [];
    private int _retainedBytes;
    private long _subscriberId;
    private TerminalSessionDescriptor _descriptor;

    public event Action? Changed;

    public TerminalOutputJournal(TerminalSessionDescriptor descriptor, HostOptions options, string history = "")
    {
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        ArgumentNullException.ThrowIfNull(options);
        _eventLimit = options.JournalEventLimit;
        _byteLimit = options.JournalByteLimit;
        _subscriberCapacity = options.SubscriberCapacity;
        _outputCharacterLimit = options.TerminalOutputCharacterLimit;
        _output = new(_outputCharacterLimit);
        _output.Append(_sanitizer.Append(history));
        _sanitizer.Clear();
    }

    public TerminalSessionDescriptor Descriptor
    {
        get
        {
            lock (_gate)
            {
                return _descriptor;
            }
        }
    }

    public void CommitOutput(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_gate)
        {
            var sequence = _descriptor.Sequence.Next();
            _descriptor = _descriptor with { Sequence = sequence };
            _output.Append(_sanitizer.Append(text));

            AddAndPublish(new TerminalOutputEnvelope(_descriptor.TerminalSessionId, sequence, text));
        }
    }

    public TerminalSessionDescriptor CommitState(
        TerminalSessionState state,
        int? exitCode = null,
        string? errorMessage = null)
    {
        lock (_gate)
        {
            _descriptor = _descriptor with
            {
                State = state,
                ExitCode = exitCode,
                ErrorMessage = errorMessage,
                HasRunningSubprocess = state == TerminalSessionState.Running ? _descriptor.HasRunningSubprocess : false,
                ForegroundCommand = state == TerminalSessionState.Running ? _descriptor.ForegroundCommand : null,
                Sequence = _descriptor.Sequence.Next(),
            };
            AddAndPublish(new TerminalStateEnvelope(_descriptor));
            return _descriptor;
        }
    }

    public TerminalSessionDescriptor CommitResize(int columns, int rows)
    {
        lock (_gate)
        {
            _descriptor = _descriptor with
            {
                Columns = columns,
                Rows = rows,
                Sequence = _descriptor.Sequence.Next(),
            };
            AddAndPublish(new TerminalStateEnvelope(_descriptor));
            return _descriptor;
        }
    }

    public async IAsyncEnumerable<TerminalEnvelope> SubscribeAsync(
        TerminalCursor? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = CreateChannel();
        long subscriberId;
        TerminalEnvelope[] initial;
        TerminalSynchronizedEnvelope synchronized;
        lock (_gate)
        {
            subscriberId = ++_subscriberId;
            initial = GetMissing(cursor);
            if (initial.Length > _subscriberCapacity) initial = [CreateSnapshot()];
            synchronized = new(_descriptor.TerminalSessionId, _descriptor.Sequence, _descriptor.Epoch);
            _subscribers.Add(subscriberId, channel);
        }

        try
        {
            foreach (var envelope in initial) yield return envelope;
            yield return synchronized;
            await foreach (var envelope in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return envelope;
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(subscriberId);
            }
        }
    }

    private Channel<TerminalEnvelope> CreateChannel() => Channel.CreateBounded<TerminalEnvelope>(
        new BoundedChannelOptions(_subscriberCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private TerminalEnvelope[] GetMissing(TerminalCursor? cursor)
    {
        if (cursor is null || cursor.Epoch != _descriptor.Epoch || cursor.Sequence > _descriptor.Sequence)
        {
            return [CreateSnapshot()];
        }

        if (cursor.Sequence == _descriptor.Sequence)
        {
            return [];
        }

        var firstRetained = _entries.First?.Value.Envelope.Sequence;
        if (firstRetained is null || cursor.Sequence.Value + 1 < firstRetained.Value.Value)
        {
            return [CreateSnapshot()];
        }

        var missing = _entries
            .Where(entry => entry.Envelope.Sequence > cursor.Sequence)
            .Select(static entry => entry.Envelope)
            .ToArray();
        return missing.Length <= _subscriberCapacity ? missing : [CreateSnapshot()];
    }

    public TerminalSnapshotEnvelope Snapshot() { lock (_gate) return CreateSnapshot(); }
    private TerminalSnapshotEnvelope CreateSnapshot() => new(_descriptor, _output.ToString());

    public TerminalSnapshotEnvelope ClearHistory()
    {
        lock (_gate)
        {
            _output.Clear(); _sanitizer.Clear();
            _entries.Clear(); _retainedBytes = 0;
            _descriptor = _descriptor with { Sequence = _descriptor.Sequence.Next() };
            var snapshot = CreateSnapshot();
            AddAndPublish(snapshot);
            return snapshot;
        }
    }

    public void CommitActivity(TerminalActivity activity)
    {
        lock (_gate)
        {
            if (_descriptor.State != TerminalSessionState.Running ||
                (_descriptor.HasRunningSubprocess == activity.HasChildren && _descriptor.ForegroundCommand == activity.Command)) return;
            _descriptor = _descriptor with { HasRunningSubprocess = activity.HasChildren,
                ForegroundCommand = activity.Command, Sequence = _descriptor.Sequence.Next() };
            AddAndPublish(new TerminalStateEnvelope(_descriptor));
        }
    }

    private void AddAndPublish(TerminalEnvelope envelope)
    {
        Changed?.Invoke();
        var encodedBytes = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            ProtocolJsonContext.Default.TerminalEnvelope).Length;
        _entries.AddLast(new JournalEntry(envelope, encodedBytes));
        _retainedBytes += encodedBytes;
        while (_entries.Count > _eventLimit || _retainedBytes > _byteLimit)
        {
            var first = _entries.First;
            if (first is null)
            {
                break;
            }

            _retainedBytes -= first.Value.EncodedBytes;
            _entries.RemoveFirst();
        }

        List<long>? slowSubscribers = null;
        foreach (var subscriber in _subscribers)
        {
            if (subscriber.Value.Writer.TryWrite(envelope))
            {
                continue;
            }

            slowSubscribers ??= [];
            slowSubscribers.Add(subscriber.Key);
            subscriber.Value.Writer.TryComplete();
        }

        if (slowSubscribers is null)
        {
            return;
        }

        foreach (var id in slowSubscribers)
        {
            _subscribers.Remove(id);
        }
    }

    private sealed record JournalEntry(TerminalEnvelope Envelope, int EncodedBytes);
}
