using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PiStation.Protocol.Models;

namespace PiStation.Host.Security;

/// <summary>Shared CLI/host authentication database. Only SHA-256 credential hashes are persisted.</summary>
public sealed partial class RemoteAccessStore : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _database;
    private readonly FileStream _ownership;
    private readonly TimeProvider _time;
    private readonly ITimer _monitor;
    private readonly Dictionary<string, CancellationTokenSource> _live = new(StringComparer.Ordinal);
    private SqliteTransaction? _transaction;
    private bool _disposed;

    public RemoteAccessStore(string databasePath, TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        // Cooperating versions share SQLite; an older exclusive-cache host still blocks us.
        _ownership = new FileStream(databasePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        _database = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Pooling = false, DefaultTimeout = 5 }.ToString());
        try
        {
            _database.Open();
            Execute("PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;");
            using var migration = _database.BeginTransaction(deferred: false);
            _transaction = migration;
            Execute("""
                CREATE TABLE IF NOT EXISTS RemoteDevices (
                    DeviceId TEXT PRIMARY KEY, DeviceName TEXT NOT NULL, CredentialHash TEXT NOT NULL UNIQUE,
                    AccessLevel INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL, Subject TEXT);
                CREATE TABLE IF NOT EXISTS RemoteInvitations (
                    Id TEXT PRIMARY KEY, CredentialHash TEXT NOT NULL UNIQUE, Label TEXT,
                    AccessLevel INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS RemotePending (
                    RequestId TEXT PRIMARY KEY, DeviceName TEXT NOT NULL, CredentialHash TEXT NOT NULL UNIQUE,
                    AccessLevel INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL, RetainUntil INTEGER NOT NULL,
                    VerificationCode TEXT NOT NULL, State TEXT NOT NULL);
                """);
            using (var columns = Command("PRAGMA table_info(RemoteDevices)"))
            {
                bool hasSubject = false;
                using (var reader = columns.ExecuteReader())
                    while (reader.Read()) hasSubject |= reader.GetString(1) == "Subject";
                if (!hasSubject) Execute("ALTER TABLE RemoteDevices ADD COLUMN Subject TEXT");
            }
            InitializeActivity();
            Prune();
            migration.Commit();
            _transaction = null;
            _monitor = _time.CreateTimer(_ => SynchronizeRevocations(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        catch { _database.Dispose(); _ownership.Dispose(); throw; }
    }

    public string CreateInvitation(RemoteAccessLevel accessLevel) => IssueInvitation(accessLevel).Token;

    public IssuedRemotePairing IssueInvitation(RemoteAccessLevel accessLevel = RemoteAccessLevel.Operate,
        TimeSpan? ttl = null, string? label = null)
    {
        ValidateLevel(accessLevel);
        label = ValidateText(label, "label", optional: true);
        var expiry = Expiry(ttl ?? TimeSpan.FromMinutes(5), TimeSpan.FromDays(1));
        return Write(() =>
        {
            if (Count("RemoteInvitations") >= 16) throw new InvalidOperationException("Wait for an existing pairing link to expire.");
            var token = NewSecret();
            var invitation = new RemotePairingInvitation(Guid.NewGuid().ToString("N"), label, accessLevel, expiry);
            Execute("INSERT INTO RemoteInvitations VALUES ($id,$hash,$label,$level,$expiry)",
                ("$id", invitation.Id), ("$hash", Hash(token)), ("$label", label), ("$level", (int)accessLevel), ("$expiry", expiry.ToUnixTimeSeconds()));
            return new IssuedRemotePairing(invitation, token);
        });
    }

    public IReadOnlyList<RemotePairingInvitation> ListInvitations() => Write<IReadOnlyList<RemotePairingInvitation>>(() =>
    {
        using var command = Command("SELECT Id,Label,AccessLevel,ExpiresAt FROM RemoteInvitations ORDER BY ExpiresAt,Id");
        using var reader = command.ExecuteReader();
        var result = new List<RemotePairingInvitation>();
        while (reader.Read()) result.Add(new(reader.GetString(0), NullableString(reader, 1),
            (RemoteAccessLevel)reader.GetInt32(2), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3))));
        return result;
    });

    public bool RevokeInvitation(string id) => Write(() => Execute("DELETE FROM RemoteInvitations WHERE Id=$id", ("$id", id)) != 0);

    public PendingRemoteDevice BeginPairing(PairingRequest request)
    {
        var name = ValidateText(request.DeviceName, "device name")!;
        if (!ValidSecret(request.DeviceCredential) || !ValidSecret(request.InvitationToken))
            throw new ArgumentException("Invalid pairing request.", nameof(request));
        return Write(() =>
        {
            if (Count("RemotePending") >= 32 || Count("RemoteDevices") >= 100)
                throw new InvalidOperationException("The remote device limit has been reached.");
            RemoteAccessLevel level;
            DateTimeOffset invitationExpiry;
            using (var command = Command("SELECT AccessLevel,ExpiresAt FROM RemoteInvitations WHERE CredentialHash=$hash", ("$hash", Hash(request.InvitationToken))))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) throw new UnauthorizedAccessException("The pairing link is invalid, expired, or already used.");
                level = (RemoteAccessLevel)reader.GetInt32(0);
                invitationExpiry = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1));
            }
            var hash = Hash(request.DeviceCredential);
            using (var duplicate = Command("SELECT 1 FROM RemoteDevices WHERE CredentialHash=$hash UNION ALL SELECT 1 FROM RemotePending WHERE CredentialHash=$hash", ("$hash", hash)))
                if (duplicate.ExecuteScalar() is not null) throw new ArgumentException("Use a new device credential.");
            Execute("DELETE FROM RemoteInvitations WHERE CredentialHash=$hash", ("$hash", Hash(request.InvitationToken)));
            var expiry = invitationExpiry.AddSeconds(30);
            var maximum = _time.GetUtcNow().AddMinutes(5).AddSeconds(30);
            if (expiry > maximum) expiry = maximum;
            expiry = DateTimeOffset.FromUnixTimeSeconds(expiry.ToUnixTimeSeconds());
            var id = Guid.NewGuid().ToString("N");
            var pending = new PendingRemoteDevice(id, name, level, expiry, PairingVerification.ComputeCode(id, request.DeviceCredential));
            Execute("INSERT INTO RemotePending VALUES ($id,$name,$hash,$level,$expiry,$expiry,$code,'pending')",
                ("$id", id), ("$name", name), ("$hash", hash), ("$level", (int)level), ("$expiry", expiry.ToUnixTimeSeconds()), ("$code", pending.VerificationCode));
            return pending;
        });
    }

    public string GetPairingState(PairingPollRequest request)
    {
        if (!ValidSecret(request.DeviceCredential)) throw new UnauthorizedAccessException("The pairing request is unavailable.");
        return Write(() =>
        {
            using var command = Command("SELECT State FROM RemotePending WHERE RequestId=$id AND CredentialHash=$hash",
                ("$id", request.RequestId), ("$hash", Hash(request.DeviceCredential)));
            return command.ExecuteScalar() as string ?? throw new UnauthorizedAccessException("The pairing request is unavailable.");
        });
    }

    public IReadOnlyList<PendingRemoteDevice> ListPending() => Write<IReadOnlyList<PendingRemoteDevice>>(() =>
    {
        using var command = Command("SELECT RequestId,DeviceName,AccessLevel,ExpiresAt,VerificationCode FROM RemotePending WHERE State='pending' ORDER BY ExpiresAt,RequestId");
        using var reader = command.ExecuteReader();
        var result = new List<PendingRemoteDevice>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), (RemoteAccessLevel)reader.GetInt32(2),
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)), reader.GetString(4)));
        return result;
    });

    public IReadOnlyList<RemoteDevice> ListDevices() => Write<IReadOnlyList<RemoteDevice>>(() =>
    {
        using var command = Command("SELECT DeviceId,DeviceName,AccessLevel,ExpiresAt,Subject FROM RemoteDevices ORDER BY ExpiresAt,DeviceId");
        using var reader = command.ExecuteReader();
        var result = new List<RemoteDevice>();
        while (reader.Read()) result.Add(WithActivity(ReadDevice(reader)));
        return result;
    });

    public void Approve(string requestId, string? verificationCode = null) => Write(() =>
    {
        string name, hash, code;
        RemoteAccessLevel level;
        using (var command = Command("SELECT DeviceName,CredentialHash,AccessLevel,VerificationCode FROM RemotePending WHERE RequestId=$id AND State='pending'", ("$id", requestId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("The pairing request is no longer pending.");
            name = reader.GetString(0); hash = reader.GetString(1); level = (RemoteAccessLevel)reader.GetInt32(2); code = reader.GetString(3);
        }
        if (verificationCode is not null && verificationCode != code) throw new ArgumentException("The verification code does not match.");
        if (Count("RemoteDevices") >= 100) throw new InvalidOperationException("The remote device limit has been reached.");
        InsertDevice(new(requestId, name, level, Expiry(TimeSpan.FromDays(180), TimeSpan.FromDays(180))), hash);
        Execute("UPDATE RemotePending SET State='approved',RetainUntil=$expiry WHERE RequestId=$id",
            ("$expiry", _time.GetUtcNow().AddSeconds(45).ToUnixTimeSeconds()), ("$id", requestId));
        return true;
    });

    public void Reject(string requestId) => Write(() => Execute("UPDATE RemotePending SET State='rejected' WHERE RequestId=$id AND State='pending'", ("$id", requestId)));

    public IssuedRemoteSession IssueSession(RemoteAccessLevel accessLevel = RemoteAccessLevel.Operate,
        TimeSpan? ttl = null, string? label = null, string? subject = null)
    {
        ValidateLevel(accessLevel);
        label = ValidateText(label, "label", optional: true) ?? "CLI session";
        subject = ValidateText(subject, "subject", optional: true);
        var expiry = Expiry(ttl ?? TimeSpan.FromDays(30), TimeSpan.FromDays(180));
        return Write(() =>
        {
            if (Count("RemoteDevices") >= 100) throw new InvalidOperationException("The remote device limit has been reached.");
            var token = NewSecret();
            var device = new RemoteDevice(Guid.NewGuid().ToString("N"), label, accessLevel, expiry, subject);
            InsertDevice(device, Hash(token));
            return new IssuedRemoteSession(WithActivity(device), token);
        });
    }

    public void Revoke(string deviceId) => RevokeSession(deviceId);
    public bool RevokeSession(string deviceId)
    {
        var removed = Write(() =>
        {
            var deleted = Execute("DELETE FROM RemoteDevices WHERE DeviceId=$id", ("$id", deviceId));
            Execute("UPDATE RemotePending SET State='rejected' WHERE RequestId=$id", ("$id", deviceId));
            return deleted != 0;
        });
        SynchronizeRevocations();
        return removed;
    }

    public RemoteAuthorization? Authenticate(string credential)
    {
        if (!ValidSecret(credential)) return null;
        var hash = Hash(credential);
        lock (_gate)
        {
            if (_disposed) return null;
            using var command = Command("SELECT DeviceId,DeviceName,AccessLevel,ExpiresAt,Subject FROM RemoteDevices WHERE CredentialHash=$hash AND ExpiresAt>$now",
                ("$hash", hash), ("$now", _time.GetUtcNow().ToUnixTimeSeconds()));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            var device = WithActivity(ReadDevice(reader));
            if (!_live.TryGetValue(hash, out var revoked)) _live.Add(hash, revoked = new());
            return new(device, revoked.Token, () => IsCurrent(hash, device.DeviceId), id => TrackConnection(device.DeviceId, id));
        }
    }

    private bool IsCurrent(string hash, string id)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            try
            {
                using var command = Command("SELECT 1 FROM RemoteDevices WHERE CredentialHash=$hash AND DeviceId=$id AND ExpiresAt>$now",
                    ("$hash", hash), ("$id", id), ("$now", _time.GetUtcNow().ToUnixTimeSeconds()));
                return command.ExecuteScalar() is not null;
            }
            catch (SqliteException) { return false; }
        }
    }

    // Per-invocation checks deny immediately; this additionally disconnects idle/streaming clients.
    internal void SynchronizeRevocations()
    {
        List<CancellationTokenSource> removed = [];
        lock (_gate)
        {
            if (_disposed || _live.Count == 0) return;
            var active = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                using var command = Command("SELECT CredentialHash FROM RemoteDevices WHERE ExpiresAt>$now", ("$now", _time.GetUtcNow().ToUnixTimeSeconds()));
                using var reader = command.ExecuteReader();
                while (reader.Read()) active.Add(reader.GetString(0));
            }
            catch (SqliteException) { active.Clear(); } // Fail closed when the authoritative store is unavailable.
            foreach (var hash in _live.Keys.Where(hash => !active.Contains(hash)).ToArray())
            {
                removed.Add(_live[hash]);
                _live.Remove(hash);
            }
        }
        Cancel(removed);
    }

    public void ClearInvitations() => Write(() => Execute("DELETE FROM RemoteInvitations; DELETE FROM RemotePending;"));

    private T Write<T>(Func<T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var transaction = _database.BeginTransaction(deferred: false);
            _transaction = transaction;
            try { Prune(); var result = action(); transaction.Commit(); return result; }
            finally { _transaction = null; }
        }
    }

    private void Prune() => Execute("""
        DELETE FROM RemoteInvitations WHERE ExpiresAt <= $now;
        DELETE FROM RemotePending WHERE RetainUntil <= $now;
        DELETE FROM RemoteDevices WHERE ExpiresAt <= $now;
        """, ("$now", _time.GetUtcNow().ToUnixTimeSeconds()));

    private void InsertDevice(RemoteDevice device, string hash)
    {
        Execute("""
        INSERT INTO RemoteDevices (DeviceId,DeviceName,CredentialHash,AccessLevel,ExpiresAt,Subject)
        VALUES ($id,$name,$hash,$level,$expiry,$subject)
        """, ("$id", device.DeviceId), ("$name", device.DeviceName), ("$hash", hash),
        ("$level", (int)device.AccessLevel), ("$expiry", device.ExpiresAt.ToUnixTimeSeconds()), ("$subject", device.Subject));
        Execute("UPDATE RemoteActivity SET CreatedAt=$now WHERE DeviceId=$id;", ("$id", device.DeviceId), ("$now", _time.GetUtcNow().ToUnixTimeSeconds()));
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _database.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(sql, parameters); return command.ExecuteNonQuery(); }
    private long Count(string table)
    { using var command = Command("SELECT COUNT(*) FROM " + table); return (long)command.ExecuteScalar()!; }
    private static RemoteDevice ReadDevice(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        (RemoteAccessLevel)reader.GetInt32(2), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)), NullableString(reader, 4));
    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private DateTimeOffset Expiry(TimeSpan ttl, TimeSpan maximum)
    {
        if (ttl < TimeSpan.FromSeconds(1) || ttl > maximum) throw new ArgumentOutOfRangeException(nameof(ttl), $"Lifetime must be between one second and {maximum.TotalDays:g} days.");
        return DateTimeOffset.FromUnixTimeSeconds(_time.GetUtcNow().Add(ttl).ToUnixTimeSeconds());
    }
    private static void ValidateLevel(RemoteAccessLevel level)
    { if (!Enum.IsDefined(level)) throw new ArgumentOutOfRangeException(nameof(level)); }
    private static string? ValidateText(string? value, string name, bool optional = false)
    {
        if (optional && value is null) return null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 || value.Any(char.IsControl))
            throw new ArgumentException($"The {name} must contain 1–80 characters without control characters.");
        return value.Trim();
    }
    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static bool ValidSecret(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public void Dispose()
    {
        CancellationTokenSource[] live;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _monitor.Dispose();
            live = _live.Values.ToArray();
            _live.Clear();
            _database.Dispose();
            _ownership.Dispose();
        }
        Cancel(live);
    }
    private static void Cancel(IEnumerable<CancellationTokenSource> sources)
    {
        foreach (var source in sources)
        {
            try { source.Cancel(); }
            catch (AggregateException) { }
            finally { source.Dispose(); }
        }
    }
}

public sealed record RemoteAuthorization(RemoteDevice Device, CancellationToken Revoked, Func<bool>? CheckActive = null,
    Func<string, IDisposable>? TrackConnection = null)
{
    public bool IsActive => !Revoked.IsCancellationRequested && (CheckActive?.Invoke() ?? true);
}
