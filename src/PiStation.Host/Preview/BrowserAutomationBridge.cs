using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Preview;

/// <summary>Connection-owned, expiring access to Pi's host-local browser inbox.</summary>
public sealed class BrowserAutomationBridge : IAsyncDisposable
{
    private readonly string? _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _cleanup;

    public BrowserAutomationBridge(string? root)
    {
        _root = root is null ? null : Path.GetFullPath(root);
        _cleanup = CleanupAsync();
    }

    public async Task<BrowserAutomationLease> OpenAsync(OpenBrowserAutomationRequest request, string principal,
        string connection, CancellationToken disconnected)
    {
        if (_root is null) throw new InvalidOperationException("Browser automation is unavailable on this host.");
        if (request.Access is not (BrowserAutomationAccess.Inspect or BrowserAutomationAccess.Interact))
            throw new ArgumentException("Select inspect or interact access.");
        var thread = request.ThreadId.Value;
        if (string.IsNullOrEmpty(thread) || thread is "." or ".." || thread.Length > 160 ||
            thread.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new ArgumentException("Invalid browser thread identity.");
        await _gate.WaitAsync(disconnected).ConfigureAwait(false);
        try
        {
            _stopping.Token.ThrowIfCancellationRequested();
            Prune();
            if (_sessions.Values.Any(s => s.Thread == thread && s.Connection != connection))
                throw new InvalidOperationException("Another desktop controls this thread's browser. Turn off its browser access first.");
            foreach (var prior in _sessions.Values.Where(s => s.Connection == connection && s.Thread == thread).ToArray()) End(prior, "Browser controller was replaced");
            if (_sessions.Count >= 64) throw new InvalidOperationException("Too many browser controllers are active.");
            var directory = Path.Combine(_root, thread);
            SafeDirectory(directory);
            SafeDirectory(Path.Combine(directory, "requests"));
            SafeDirectory(Path.Combine(directory, "responses"));
            var session = new Session(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), thread, directory, request.Access, principal, connection, disconnected);
            // A claimed command must never be replayed, including after a host crash.
            foreach (var marker in Directory.EnumerateFiles(Path.Combine(directory, "requests"), "*.claimed"))
            {
                var id = Path.GetFileNameWithoutExtension(marker);
                if (Guid.TryParseExact(id, "D", out _)) WriteResult(session, id, new(false, Error: "Browser connection ended; the command was not replayed."));
            }
            Renew(session);
            _sessions.Add(session.Id, session);
            return new(session.Id);
        }
        finally { _gate.Release(); }
    }

    public async Task CloseThreadAsync(ThreadId threadId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var session in _sessions.Values.Where(session => session.Thread == threadId.Value).ToArray()) End(session, "The browser thread was deleted");
        }
        finally { _gate.Release(); }
    }

    public async Task<BrowserAutomationPoll> PollAsync(string id, string principal, string connection, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Prune();
            var session = Require(id, principal, connection);
            Renew(session);
            if (session.Active is { } active)
            {
                if (!Pending(session, active))
                {
                    WriteResult(session, active.Id, new(false, Error: "Browser request expired or was cancelled."));
                    session.Active = null;
                }
                // Never redeliver an already claimed command, even if a poll response was lost.
                return new(null, session.Active?.Id);
            }
            var requests = Path.Combine(session.Directory, "requests");
            SafeDirectory(requests);
            foreach (var path in Directory.EnumerateFiles(requests, "*.json").Take(64))
            {
                BrowserAutomationRequest? request;
                try
                {
                    SafeFile(path);
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    if (file.Length is <= 0 or > BrowserAutomationLimits.MaximumRequestBytes) { File.Delete(path); continue; }
                    var bytes = new byte[checked((int)file.Length)];
                    file.ReadExactly(bytes);
                    request = JsonSerializer.Deserialize(bytes, ProtocolJsonContext.Default.BrowserAutomationRequest);
                }
                catch (FileNotFoundException) { continue; }
                catch (JsonException) { File.Delete(path); continue; }
                if (request is null || !Guid.TryParseExact(request.Id, "D", out _) || Path.GetFileNameWithoutExtension(path) != request.Id)
                { File.Delete(path); continue; }
                if (!Pending(session, request) || request.Input.ValueKind != JsonValueKind.Object ||
                    !BrowserAutomationLimits.IsOperation(request.Operation) ||
                    BrowserAutomationLimits.RequiresInteraction(request.Operation) && session.Access != BrowserAutomationAccess.Interact)
                { WriteResult(session, request.Id, new(false, Error: "Browser request expired, is invalid, or is not permitted.")); continue; }
                var marker = Path.ChangeExtension(path, ".claimed");
                if (File.Exists(marker)) { WriteResult(session, request.Id, new(false, Error: "Browser command was already claimed and will not be replayed.")); continue; }
                using (var claim = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None)) claim.Flush(flushToDisk: true);
                session.Active = request;
                return new(request, request.Id);
            }
            return new(null, null);
        }
        finally { _gate.Release(); }
    }

    public async Task CompleteAsync(string id, string requestId, BrowserAutomationResult result, string principal, string connection, CancellationToken token)
    {
        ValidateResult(result);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Prune();
            var session = Require(id, principal, connection);
            if (session.Active is not { } active || active.Id != requestId || !Pending(session, active))
                throw new InvalidOperationException("The browser request is no longer active.");
            if (result.ScreenshotPng is not null && active.Operation != "screenshot") throw new ArgumentException("Only screenshots may return image bytes.");
            WriteResult(session, requestId, result);
            session.Active = null;
        }
        finally { _gate.Release(); }
    }

    public async Task CloseAsync(string id, string principal, string connection)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_sessions.TryGetValue(id, out var session) && session.Principal == principal && session.Connection == connection) End(session, "Browser access was closed by its desktop"); }
        finally { _gate.Release(); }
    }

    private Session Require(string id, string principal, string connection) =>
        _sessions.TryGetValue(id, out var session) && session.Principal == principal && session.Connection == connection
            ? session : throw new UnauthorizedAccessException("The browser controller has ended. Reconnect or enable browser access again.");

    private static bool Pending(Session session, BrowserAutomationRequest request) =>
        request.ControllerId == session.Id && request.CreatedUtc >= DateTimeOffset.UtcNow.AddSeconds(-30) && request.CreatedUtc <= DateTimeOffset.UtcNow.AddSeconds(5) &&
        File.Exists(Path.Combine(session.Directory, "requests", request.Id + ".json"));

    private static void Renew(Session session)
    {
        session.Expires = DateTimeOffset.UtcNow.AddSeconds(5);
        if (session.PermissionWritten > DateTimeOffset.UtcNow.AddSeconds(-1)) return;
        SafeDirectory(session.Directory);
        AtomicWrite(Path.Combine(session.Directory, "permission.json"), JsonSerializer.SerializeToUtf8Bytes(new
        { mode = session.Access == BrowserAutomationAccess.Interact ? "interact" : "inspect", controllerId = session.Id, expiresUtc = DateTimeOffset.UtcNow.AddSeconds(5) }));
        session.PermissionWritten = DateTimeOffset.UtcNow;
        if (session.LastCleanup <= DateTimeOffset.UtcNow.AddSeconds(-10))
        {
            var responses = Path.Combine(session.Directory, "responses");
            SafeDirectory(responses);
            foreach (var path in Directory.EnumerateFiles(responses).Where(p => p.EndsWith(".json", StringComparison.Ordinal) || p.EndsWith(".tmp", StringComparison.Ordinal)).Take(256))
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddMinutes(-1)) File.Delete(path);
            session.LastCleanup = DateTimeOffset.UtcNow;
        }
    }

    private static void WriteResult(Session session, string id, BrowserAutomationResult result)
    {
        SafeDirectory(Path.Combine(session.Directory, "requests"));
        SafeDirectory(Path.Combine(session.Directory, "responses"));
        AtomicWrite(Path.Combine(session.Directory, "responses", id + ".json"), JsonSerializer.SerializeToUtf8Bytes(result, ProtocolJsonContext.Default.BrowserAutomationResult));
        File.Delete(Path.Combine(session.Directory, "requests", id + ".json"));
        File.Delete(Path.Combine(session.Directory, "requests", id + ".claimed"));
    }

    private static void AtomicWrite(string path, byte[] data)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, data); File.Move(temporary, path, overwrite: true); }
        finally { File.Delete(temporary); }
    }

    private static void SafeDirectory(string path)
    {
        // Check existing ancestors before creating anything through a junction.
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Browser storage cannot use symbolic links.");
        }
        Directory.CreateDirectory(path);
    }

    private static void SafeFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Browser requests cannot use symbolic links.");
    }

    public static void ValidateResult(BrowserAutomationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Error?.Length > 2048 || result.Data is { } data && Encoding.UTF8.GetByteCount(data.GetRawText()) > BrowserAutomationLimits.MaximumDataBytes)
            throw new ArgumentException("Browser result is too large.");
        if (result.ScreenshotPng is { } png && (png.Length > BrowserAutomationLimits.MaximumScreenshotBytes || png.Length < 8 ||
            !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })))
            throw new ArgumentException("Browser screenshot must be a PNG up to 4 MiB.");
    }

    private void Prune()
    {
        foreach (var session in _sessions.Values.Where(s => s.Disconnected.IsCancellationRequested || s.Expires <= DateTimeOffset.UtcNow).ToArray())
            End(session, session.Disconnected.IsCancellationRequested ? "Browser connection disconnected" : "Browser controller heartbeat expired");
    }

    private void End(Session session, string reason = "Browser host stopped")
    {
        _sessions.Remove(session.Id);
        SafeDirectory(session.Directory);
        File.Delete(Path.Combine(session.Directory, "permission.json"));
        if (session.Active is { } active) WriteResult(session, active.Id, new(false, Error: reason + "; the command was not replayed."));
    }

    private async Task CleanupAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                await _gate.WaitAsync(_stopping.Token).ConfigureAwait(false);
                try { Prune(); }
                catch (IOException) { /* The lease is already invalid; retry filesystem cleanup on future use. */ }
                catch (UnauthorizedAccessException) { }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _cleanup.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { foreach (var session in _sessions.Values.ToArray()) End(session); }
        finally { _gate.Release(); }
    }

    private sealed class Session(string id, string thread, string directory, BrowserAutomationAccess access, string principal, string connection, CancellationToken disconnected)
    {
        public string Id { get; } = id;
        public string Thread { get; } = thread;
        public string Directory { get; } = directory;
        public BrowserAutomationAccess Access { get; } = access;
        public string Principal { get; } = principal;
        public string Connection { get; } = connection;
        public CancellationToken Disconnected { get; } = disconnected;
        public DateTimeOffset Expires { get; set; }
        public DateTimeOffset LastCleanup { get; set; }
        public DateTimeOffset PermissionWritten { get; set; }
        public BrowserAutomationRequest? Active { get; set; }
    }
}
