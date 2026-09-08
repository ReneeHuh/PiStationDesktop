using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Serialization;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Threads;

public sealed class ThreadEventJournal
{
    private readonly int _byteLimit;
    private readonly LinkedList<JournalEntry> _entries = [];
    private readonly int _eventLimit;
    private readonly object _lock = new();
    private readonly int _subscriberCapacity;
    private readonly Dictionary<long, Channel<ThreadEnvelope>> _subscribers = [];
    private int _retainedBytes;
    private long _subscriberId;
    private ThreadProjection _projection;

    public ThreadEventJournal(ThreadProjection projection, HostOptions options)
    {
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        ArgumentNullException.ThrowIfNull(options);
        _eventLimit = options.JournalEventLimit;
        _byteLimit = options.JournalByteLimit;
        _subscriberCapacity = options.SubscriberCapacity;
    }

    public ThreadProjection Projection
    {
        get
        {
            lock (_lock)
            {
                return _projection;
            }
        }
    }

    public ThreadEventEnvelope Commit(ThreadEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        lock (_lock)
        {
            var sequence = _projection.Sequence.Next();
            _projection = ThreadProjectionReducer.Apply(_projection, @event) with { Sequence = sequence };
            var envelope = new ThreadEventEnvelope(
                _projection.EnvironmentId,
                _projection.ThreadId,
                _projection.ProjectionEpoch,
                sequence,
                @event);
            var encodedBytes = JsonSerializer.SerializeToUtf8Bytes<ThreadEnvelope>(
                envelope,
                ProtocolJsonContext.Default.ThreadEnvelope).Length;
            _entries.AddLast(new JournalEntry(envelope, encodedBytes));
            _retainedBytes += encodedBytes;
            Prune();
            Publish(envelope);
            return envelope;
        }
    }

    public void ReplaceProjection(ThreadProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        lock (_lock)
        {
            _projection = projection;
            _entries.Clear();
            _retainedBytes = 0;
            Publish(new ThreadSnapshotEnvelope(_projection));
        }
    }

    public async IAsyncEnumerable<ThreadEnvelope> SubscribeAsync(
        ThreadCursor? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<ThreadEnvelope>(new BoundedChannelOptions(_subscriberCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        long subscriberId;
        ThreadEnvelope[] initial;
        ThreadSynchronizedEnvelope synchronized;
        lock (_lock)
        {
            subscriberId = ++_subscriberId;
            initial = GetMissing(cursor);
            synchronized = new(_projection.EnvironmentId, _projection.ThreadId, _projection.ProjectionEpoch, _projection.Sequence);
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
            lock (_lock)
            {
                _subscribers.Remove(subscriberId);
            }
        }
    }

    private ThreadEnvelope[] GetMissing(ThreadCursor? cursor)
    {
        if (cursor is null || cursor.ProjectionEpoch != _projection.ProjectionEpoch || cursor.Sequence > _projection.Sequence)
        {
            return [new ThreadSnapshotEnvelope(_projection)];
        }

        if (cursor.Sequence == _projection.Sequence)
        {
            return [];
        }

        var firstRetained = _entries.First?.Value.Envelope.Sequence;
        if (firstRetained is null || cursor.Sequence.Value + 1 < firstRetained.Value.Value)
        {
            return [new ThreadSnapshotEnvelope(_projection)];
        }

        var missing = _entries
            .Where(entry => entry.Envelope.Sequence > cursor.Sequence)
            .Select(static entry => (ThreadEnvelope)entry.Envelope)
            .ToArray();
        return missing.Length <= _subscriberCapacity ? missing : [new ThreadSnapshotEnvelope(_projection)];
    }

    private Channel<ThreadEnvelope> CreateSnapshotChannel()
    {
        var channel = Channel.CreateBounded<ThreadEnvelope>(new BoundedChannelOptions(_subscriberCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        channel.Writer.TryWrite(new ThreadSnapshotEnvelope(_projection));
        return channel;
    }

    private void Publish(ThreadEnvelope envelope)
    {
        List<long>? slowSubscribers = null;
        foreach (var subscriber in _subscribers)
        {
            if (subscriber.Value.Writer.TryWrite(envelope))
            {
                continue;
            }

            slowSubscribers ??= [];
            slowSubscribers.Add(subscriber.Key);
            subscriber.Value.Writer.TryWrite(new ThreadResyncRequiredEnvelope(
                _projection.EnvironmentId,
                _projection.ThreadId,
                _projection.ProjectionEpoch,
                _projection.Sequence,
                "Subscriber could not keep up with the thread stream."));
            subscriber.Value.Writer.TryComplete();
        }

        if (slowSubscribers is null)
        {
            return;
        }

        foreach (var subscriberId in slowSubscribers)
        {
            _subscribers.Remove(subscriberId);
        }
    }

    private void Prune()
    {
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
    }

    private sealed record JournalEntry(ThreadEventEnvelope Envelope, int EncodedBytes);
}
