using System.Text.Json;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime;

internal sealed class ThreadSnapshotCache(TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<ThreadId, Entry> _entries = [];
    private const int MaximumBytes = 32 * 1024 * 1024;
    private int _bytes;

    public ThreadProjection? Take(ThreadId id)
    {
        lock (_gate)
        {
            Prune();
            if (!_entries.Remove(id, out var entry)) return null;
            _bytes -= entry.Bytes;
            return entry.Projection;
        }
    }

    public void Put(ThreadId id, ThreadProjection? projection)
    {
        if (projection is null) return;
        var size = JsonSerializer.SerializeToUtf8Bytes(projection, ProtocolJsonContext.Default.ThreadProjection).Length;
        lock (_gate)
        {
            if (_entries.Remove(id, out var old)) _bytes -= old.Bytes;
            if (size > MaximumBytes) return;
            _entries[id] = new(projection, size, clock.GetUtcNow());
            _bytes += size;
            Prune();
        }
    }

    private void Prune()
    {
        foreach (var item in _entries.OrderBy(e => e.Value.RetainedAt).ToArray())
        {
            if (_entries.Count <= 20 && _bytes <= MaximumBytes && clock.GetUtcNow() - item.Value.RetainedAt < TimeSpan.FromMinutes(10)) break;
            _entries.Remove(item.Key);
            _bytes -= item.Value.Bytes;
        }
    }

    private sealed record Entry(ThreadProjection Projection, int Bytes, DateTimeOffset RetainedAt);
}
