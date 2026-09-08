using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using PiStation.Protocol.Models;

namespace PiStation.Host.Updates;

public sealed class RemoteUpdateCoordinator : IDisposable
{
    public const long MaximumPackageBytes = 512L * 1024 * 1024;
    private readonly string _dataRoot;
    private readonly string _root;
    private readonly Func<bool> _busy;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _upload = new(1, 1);
    private IRemoteUpdateOwner? _owner;
    private Task? _activation;
    private CancellationTokenSource? _activationCancellation;
    private bool _disposed;
    private bool _draining;
    private int _operations;
    public bool IsDraining { get { lock (_gate) return _draining; } }

    internal OperationLease? EnterOperation(bool allowDuringDrain)
    {
        lock (_gate)
        {
            if (_draining)
            {
                if (!allowDuringDrain) throw new InvalidOperationException("The host is restarting for an update. New operations are paused.");
                return null;
            }
            _operations++;
            return new(this);
        }
    }

    internal sealed class OperationLease(RemoteUpdateCoordinator owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._gate) owner._operations--;
        }
    }

    public RemoteUpdateCoordinator(string dataRoot, Func<bool> busy)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _root = Path.Combine(_dataRoot, "remote-updates");
        _busy = busy;
        if (Directory.Exists(_root))
            foreach (var directory in Directory.EnumerateDirectories(_root))
                if (Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) && Read(id) is { } previous &&
                    previous.Receipt.State is RemoteUpdateState.Uploading or RemoteUpdateState.WaitingForIdle)
                    Write(previous with { Receipt = previous.Receipt with { State = RemoteUpdateState.Failed,
                        Message = "The host restarted before upload or activation completed. Review and submit a new request.", UpdatedAt = DateTimeOffset.UtcNow } });
    }

    public bool Enabled
    {
        get => File.Exists(Path.Combine(_dataRoot, "allow-remote-updates"));
        set
        {
            Directory.CreateDirectory(_dataRoot);
            var path = Path.Combine(_dataRoot, "allow-remote-updates");
            if (value) File.WriteAllText(path, "Approved operate devices may supply host update packages.");
            else if (File.Exists(path)) File.Delete(path);
        }
    }
    public void SetOwner(IRemoteUpdateOwner owner) { lock (_gate) _owner = owner; }
    public RemoteUpdateDescriptor Descriptor => new(_owner?.Kind ?? "external", _owner?.CurrentVersion ?? "unknown",
        _owner is not null, Enabled, _owner?.PackageKind ?? string.Empty,
        _owner?.Trust ?? "This launcher does not support remote activation.", _busy());

    public RemoteUpdateReceipt Prepare(PrepareRemoteUpdateRequest request, string principal)
    {
        if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Sha256) || string.IsNullOrWhiteSpace(request.FileName) || request.Length is <= 0 or > MaximumPackageBytes ||
            request.Sha256.Length != 64 || !request.Sha256.All(char.IsAsciiHexDigit) ||
            Path.GetFileName(request.FileName) != request.FileName || request.FileName.Length > 120)
            throw new ArgumentException("Choose a valid bounded host update package.", nameof(request));
        lock (_gate)
        {
            var owner = RequireOwner();
            if (!Path.GetExtension(request.FileName).Equals(owner.PackageKind, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"This host accepts {owner.PackageKind} packages.", nameof(request));
            var existing = Read(request.RequestId);
            if (existing is not null)
            {
                if (existing.Request != request || existing.Principal != principal) throw new InvalidOperationException("This update request ID is already in use.");
                return existing.Receipt;
            }
            if (_activation is { IsCompleted: false }) throw new InvalidOperationException("A host update is already being activated.");
            Directory.CreateDirectory(_root);
            // Bound staged package storage. Finished receipts remain inspectable until the host owner removes them.
            if (Directory.EnumerateDirectories(_root).Count() >= 32) throw new InvalidOperationException("The host update history is full. The host owner must remove old update packages.");
            var receipt = new RemoteUpdateReceipt(request.RequestId, RemoteUpdateState.Uploading, UpdatedAt: DateTimeOffset.UtcNow);
            Write(new(request, principal, receipt));
            return receipt;
        }
    }

    public RemoteUpdateReceipt? GetReceipt(Guid id) { lock (_gate) return Read(id)?.Receipt; }

    public RemoteUpdateReceipt[] GetHistory(string principal)
    {
        lock (_gate)
        {
            if (!Directory.Exists(_root)) return [];
            return Directory.EnumerateDirectories(_root).Select(Path.GetFileName)
                .Where(name => Guid.TryParseExact(name, "N", out _)).Select(name => Read(Guid.ParseExact(name!, "N")))
                .Where(update => update?.Principal == principal).Select(update => update!.Receipt)
                .OrderByDescending(receipt => receipt.UpdatedAt).Take(32).ToArray();
        }
    }

    public async Task UploadAsync(HttpContext context)
    {
        if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
            bodyLimit.MaxRequestBodySize = MaximumPackageBytes;
        if (!Guid.TryParse(context.Request.RouteValues["request"]?.ToString(), out var id))
        { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        var principal = PiStation.Host.Preview.PreviewLeaseRegistry.Principal(context);
        await _upload.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            StoredUpdate update;
            IRemoteUpdateOwner owner;
            lock (_gate)
            {
                owner = RequireOwner();
                update = Read(id) ?? throw new InvalidOperationException("Prepare the update before uploading.");
                if (update.Principal != principal) throw new UnauthorizedAccessException("The update belongs to another device.");
                if (update.Receipt.State != RemoteUpdateState.Uploading) throw new InvalidOperationException("The upload request has already finished.");
            }
            if (context.Request.ContentLength != update.Request.Length) throw new InvalidDataException("The update length does not match the prepared package.");
            var path = PackagePath(update);
            var temporary = path + ".upload";
            try
            {
                long received = 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    var buffer = new byte[65536];
                    while (true)
                    {
                        var count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false);
                        if (count == 0) break;
                        received += count;
                        if (received > update.Request.Length) throw new InvalidDataException("The package exceeded its declared size.");
                        hash.AppendData(buffer, 0, count);
                        await output.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted).ConfigureAwait(false);
                    }
                }
                if (received != update.Request.Length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(update.Request.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The uploaded package did not match its declared hash and length.");
                File.Move(temporary, path, overwrite: false);
                var version = await owner.ValidateAsync(path, RuntimeDirectory(id), context.RequestAborted).ConfigureAwait(false);
                lock (_gate)
                {
                    var current = Read(id)!;
                    if (current.Receipt.State != RemoteUpdateState.Uploading) throw new OperationCanceledException("The upload was canceled.");
                    RequireOwner();
                    update = current with { Receipt = current.Receipt with { State = RemoteUpdateState.Ready, TargetVersion = version, ReceivedBytes = received, UpdatedAt = DateTimeOffset.UtcNow } };
                    Write(update);
                }
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.Body, update.Receipt, RemoteUpdateJsonContext.Default.RemoteUpdateReceipt, context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    var current = Read(id);
                    if (current?.Receipt.State == RemoteUpdateState.Uploading)
                        Write(current with { Receipt = current.Receipt with { State = RemoteUpdateState.Failed, Message = SafeFailure(exception), UpdatedAt = DateTimeOffset.UtcNow } });
                }
                throw;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (UnauthorizedAccessException) { context.Response.StatusCode = StatusCodes.Status403Forbidden; }
        catch (Exception) when (!context.Response.HasStarted) { context.Response.StatusCode = StatusCodes.Status400BadRequest; }
        finally { _upload.Release(); }
    }

    public RemoteUpdateReceipt Commit(CommitRemoteUpdateRequest request, string principal)
    {
        lock (_gate)
        {
            var owner = RequireOwner();
            var update = Read(request.RequestId) ?? throw new InvalidOperationException("The update package is missing.");
            if (update.Principal != principal) throw new UnauthorizedAccessException("The update belongs to another device.");
            if (update.Receipt.State is RemoteUpdateState.WaitingForIdle or RemoteUpdateState.Restarting or RemoteUpdateState.Succeeded) return update.Receipt;
            if (update.Receipt.State != RemoteUpdateState.Ready) throw new InvalidOperationException("Upload and validate the package before activating it.");
            if (_activation is { IsCompleted: false }) throw new InvalidOperationException("Another update is being activated.");
            var waiting = update with { Receipt = update.Receipt with { State = RemoteUpdateState.WaitingForIdle, UpdatedAt = DateTimeOffset.UtcNow } };
            Write(waiting);
            _activationCancellation?.Dispose();
            _activationCancellation = new();
            var token = _activationCancellation.Token;
            _activation = Task.Run(() => ActivateAsync(owner, waiting, request.InterruptActiveWork, token));
            return waiting.Receipt;
        }
    }

    public RemoteUpdateReceipt Cancel(Guid id, string principal)
    {
        lock (_gate)
        {
            var update = Read(id) ?? throw new InvalidOperationException("The update request is missing.");
            if (update.Principal != principal) throw new UnauthorizedAccessException("The update belongs to another device.");
            if (update.Receipt.State is RemoteUpdateState.Restarting or RemoteUpdateState.Succeeded) return update.Receipt;
            if (update.Receipt.State == RemoteUpdateState.WaitingForIdle) _activationCancellation?.Cancel();
            var canceled = update with { Receipt = update.Receipt with { State = RemoteUpdateState.Canceled, UpdatedAt = DateTimeOffset.UtcNow } };
            Write(canceled);
            return canceled.Receipt;
        }
    }

    private async Task ActivateAsync(IRemoteUpdateOwner owner, StoredUpdate update, bool interrupt, CancellationToken cancellationToken)
    {
        try
        {
            // Permit the commit receipt to reach the client before an owner starts shutting down.
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RequireOwner();
                    // Admission and the idle decision share a lock: an already accepted
                    // operation cannot slip between the idle check and shutdown.
                    if (interrupt || _operations == 0 && !_busy())
                    {
                        _draining = true;
                        update = update with { Receipt = update.Receipt with { State = RemoteUpdateState.Restarting, UpdatedAt = DateTimeOffset.UtcNow } };
                        Write(update);
                        break;
                    }
                }
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            await owner.ActivateAsync(new(update.Request.RequestId, _dataRoot, PackagePath(update), RuntimeDirectory(update.Request.RequestId),
                update.Receipt.TargetVersion!, ReceiptPath(update.Request.RequestId), interrupt), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _draining = false;
                Write(update with { Receipt = update.Receipt with { State = RemoteUpdateState.Failed, Message = SafeFailure(exception), UpdatedAt = DateTimeOffset.UtcNow } });
            }
        }
    }

    private IRemoteUpdateOwner RequireOwner()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enabled) throw new InvalidOperationException("The host owner has not enabled remote updates.");
        return _owner ?? throw new InvalidOperationException("This host must run under an update-capable owner.");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // An accepted activation belongs to its owner and must survive host teardown.
            if (!_draining) _activationCancellation?.Cancel();
        }
    }
    private string ReceiptPath(Guid id) => Path.Combine(_root, id.ToString("N"), "receipt.json");
    private string RuntimeDirectory(Guid id) => Path.Combine(_root, id.ToString("N"), "runtime");
    private string PackagePath(StoredUpdate update) => Path.Combine(_root, update.Request.RequestId.ToString("N"), "package" + Path.GetExtension(update.Request.FileName));
    private StoredUpdate? Read(Guid id)
    {
        var path = ReceiptPath(id);
        return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), RemoteUpdateJsonContext.Default.StoredUpdate) : null;
    }
    private void Write(StoredUpdate update) => WriteReceiptFile(ReceiptPath(update.Request.RequestId), update);
    internal static void WriteReceiptFile(string path, StoredUpdate update)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(update, RemoteUpdateJsonContext.Default.StoredUpdate));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void CompleteActivation(string receiptPath, bool succeeded, string message)
    {
        var stored = JsonSerializer.Deserialize(File.ReadAllBytes(receiptPath), RemoteUpdateJsonContext.Default.StoredUpdate) ?? throw new InvalidDataException("The update receipt is invalid.");
        WriteReceiptFile(receiptPath, stored with { Receipt = stored.Receipt with { State = succeeded ? RemoteUpdateState.Succeeded : RemoteUpdateState.Failed, Message = message, UpdatedAt = DateTimeOffset.UtcNow } });
    }

    /// <summary>Called by a standalone launcher holding its exclusive owner lock, before starting a child.</summary>
    public static void FailInterruptedActivations(string dataRoot)
    {
        var root = Path.Combine(Path.GetFullPath(dataRoot), "remote-updates");
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            var path = Path.Combine(directory, "receipt.json");
            if (!File.Exists(path)) continue;
            var stored = JsonSerializer.Deserialize(File.ReadAllBytes(path), RemoteUpdateJsonContext.Default.StoredUpdate);
            if (stored?.Receipt.State == RemoteUpdateState.Restarting)
                CompleteActivation(path, false, "The owner restarted before update activation was confirmed. Review the running version before retrying.");
        }
    }
    private static string SafeFailure(Exception exception) => exception switch
    {
        OperationCanceledException => "The update was canceled before activation.",
        InvalidDataException => "Package integrity, format, platform, or version validation failed.",
        UnauthorizedAccessException => "The package or update request was not trusted.",
        _ => "The host update failed. Inspect the owner diagnostics before retrying.",
    };
}

internal sealed record StoredUpdate(PrepareRemoteUpdateRequest Request, string Principal, RemoteUpdateReceipt Receipt);
[JsonSerializable(typeof(StoredUpdate))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RemoteUpdateReceipt))]
[JsonSerializable(typeof(StagedRemoteUpdate))]
internal sealed partial class RemoteUpdateJsonContext : JsonSerializerContext;
