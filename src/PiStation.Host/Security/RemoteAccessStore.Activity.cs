using PiStation.Protocol.Models;

namespace PiStation.Host.Security;

public sealed partial class RemoteAccessStore
{
    private void InitializeActivity() => Execute("""
        CREATE TABLE IF NOT EXISTS RemoteActivity (
            DeviceId TEXT PRIMARY KEY, CreatedAt INTEGER, LastSeenAt INTEGER, LastConnectedAt INTEGER, LastDisconnectedAt INTEGER);
        CREATE TABLE IF NOT EXISTS RemoteConnections (
            ConnectionId TEXT PRIMARY KEY, DeviceId TEXT NOT NULL, LeaseUntil INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS IX_RemoteConnections_Device ON RemoteConnections(DeviceId);
        CREATE TRIGGER IF NOT EXISTS RemoteActivity_Created AFTER INSERT ON RemoteDevices BEGIN
            INSERT OR IGNORE INTO RemoteActivity(DeviceId, CreatedAt) VALUES(NEW.DeviceId, unixepoch());
        END;
        CREATE TRIGGER IF NOT EXISTS RemoteActivity_Deleted AFTER DELETE ON RemoteDevices BEGIN
            DELETE FROM RemoteActivity WHERE DeviceId=OLD.DeviceId;
            DELETE FROM RemoteConnections WHERE DeviceId=OLD.DeviceId;
        END;
        """);

    private RemoteDevice WithActivity(RemoteDevice device)
    {
        using var command = Command("""
            SELECT CreatedAt, LastSeenAt, LastConnectedAt, LastDisconnectedAt,
                (SELECT COUNT(*) FROM RemoteConnections WHERE DeviceId=$id AND LeaseUntil>$now)
            FROM RemoteActivity WHERE DeviceId=$id;
            """, ("$id", device.DeviceId), ("$now", _time.GetUtcNow().ToUnixTimeSeconds()));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return device;
        DateTimeOffset? Time(int index) => reader.IsDBNull(index) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(index));
        return device with { CreatedAt = Time(0), LastSeenAt = Time(1), LastConnectedAt = Time(2),
            LastDisconnectedAt = Time(3), ActiveConnections = reader.GetInt32(4) };
    }

    private ActivityLease TrackConnection(string deviceId, string connectionId)
    {
        Write(() =>
        {
            var now = _time.GetUtcNow().ToUnixTimeSeconds();
            Execute("INSERT OR IGNORE INTO RemoteActivity(DeviceId) VALUES($id);", ("$id", deviceId));
            Execute("UPDATE RemoteActivity SET LastConnectedAt=$now, LastSeenAt=$now WHERE DeviceId=$id;", ("$id", deviceId), ("$now", now));
            return Execute("INSERT INTO RemoteConnections(ConnectionId,DeviceId,LeaseUntil) VALUES($connection,$id,$until);",
                ("$connection", connectionId), ("$id", deviceId), ("$until", now + 60));
        });
        return new ActivityLease(this, deviceId, connectionId);
    }

    private void Heartbeat(string deviceId, string connectionId)
    {
        lock (_gate)
        {
            if (_disposed) return;
            Write(() =>
            {
                var now = _time.GetUtcNow().ToUnixTimeSeconds();
                Execute("DELETE FROM RemoteConnections WHERE LeaseUntil<=$now;", ("$now", now));
                var changed = Execute("""
                    UPDATE RemoteConnections SET LeaseUntil=$until WHERE ConnectionId=$connection
                    AND EXISTS(SELECT 1 FROM RemoteDevices WHERE DeviceId=$id AND ExpiresAt>$now);
                    """, ("$connection", connectionId), ("$id", deviceId), ("$now", now), ("$until", now + 60));
                if (changed > 0) Execute("UPDATE RemoteActivity SET LastSeenAt=$now WHERE DeviceId=$id;", ("$id", deviceId), ("$now", now));
                return changed;
            });
        }
    }

    private void EndConnection(string deviceId, string connectionId)
    {
        lock (_gate)
        {
            if (_disposed) return;
            Write(() =>
            {
                Execute("DELETE FROM RemoteConnections WHERE ConnectionId=$connection;", ("$connection", connectionId));
                return Execute("UPDATE RemoteActivity SET LastDisconnectedAt=$now WHERE DeviceId=$id;",
                    ("$id", deviceId), ("$now", _time.GetUtcNow().ToUnixTimeSeconds()));
            });
        }
    }

    private sealed class ActivityLease : IDisposable
    {
        private readonly RemoteAccessStore _store;
        private readonly string _device;
        private readonly string _connection;
        private readonly ITimer _timer;
        private int _disposed;
        public ActivityLease(RemoteAccessStore store, string device, string connection)
        {
            _store = store; _device = device; _connection = connection;
            _timer = store._time.CreateTimer(_ =>
            {
                try { if (Volatile.Read(ref _disposed) == 0) store.Heartbeat(device, connection); }
                catch (Microsoft.Data.Sqlite.SqliteException) { /* Expiring lease fails offline if persistence is unavailable. */ }
            }, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _timer.Dispose();
            _store.EndConnection(_device, _connection);
        }
    }
}
