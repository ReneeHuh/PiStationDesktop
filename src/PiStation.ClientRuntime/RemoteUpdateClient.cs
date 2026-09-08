using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.SignalR.Client;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime;

/// <summary>Authenticated maintenance access independent of workspace protocol readiness.</summary>
public sealed class RemoteUpdateClient : IAsyncDisposable
{
    private const string Prefix = "updates/v1/";
    private readonly ClientRuntimeOptions _options;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping;
    private EnvironmentId? _identity;
    private HubConnection? _legacy;
    private bool _certificateRejected;
    private bool _disposed;

    public RemoteUpdateClient(ClientRuntimeOptions options, CancellationToken stopping = default)
    {
        options.Validate();
        _options = options;
        _identity = options.ExpectedEnvironmentId;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _http = new HttpClient(RemoteTransport.CreateHandler(options.CertificateFingerprint, () => _certificateRejected = true))
            { BaseAddress = new(options.HubAddress, "/"), Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new("Bearer", options.BearerCredential);
    }

    public Task<RemoteUpdateDescriptor> GetDescriptorAsync(CancellationToken token = default) =>
        RunAsync((descriptor, _, _) => Task.FromResult(descriptor), token);

    public Task<RemoteUpdateReceipt[]> GetHistoryAsync(CancellationToken token = default) => RunAsync(async (_, legacy, ct) =>
        legacy ? await _legacy!.InvokeAsync<RemoteUpdateReceipt[]>("GetRemoteUpdateHistory", ct).ConfigureAwait(false)
            : await SendAsync(HttpMethod.Get, Prefix + "history", null, ProtocolJsonContext.Default.RemoteUpdateReceiptArray, ct).ConfigureAwait(false), token);

    public Task<RemoteUpdateReceipt?> GetReceiptAsync(Guid id, CancellationToken token = default) => RunAsync<RemoteUpdateReceipt?>(async (_, legacy, ct) =>
    {
        if (legacy)
        {
            // Legacy receipt RPCs were not device-scoped; history is scoped on those hosts.
            return (await _legacy!.InvokeAsync<RemoteUpdateReceipt[]>("GetRemoteUpdateHistory", ct).ConfigureAwait(false)).SingleOrDefault(r => r.RequestId == id);
        }
        using var request = Request(HttpMethod.Get, Prefix + id.ToString("D"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await CheckAsync(response, ct).ConfigureAwait(false);
        return VerifyReceipt(await ReadAsync(response, ProtocolJsonContext.Default.RemoteUpdateReceipt, ct).ConfigureAwait(false), id);
    }, token);

    public Task<RemoteUpdateReceipt> CommitAsync(CommitRemoteUpdateRequest commit, CancellationToken token = default) => RunAsync(async (_, legacy, ct) =>
        VerifyReceipt(legacy ? await _legacy!.InvokeAsync<RemoteUpdateReceipt>("CommitRemoteUpdate", commit, ct).ConfigureAwait(false)
            : await SendAsync(HttpMethod.Post, Prefix + commit.RequestId.ToString("D") + "/commit",
                Content(commit, ProtocolJsonContext.Default.CommitRemoteUpdateRequest), ProtocolJsonContext.Default.RemoteUpdateReceipt, ct).ConfigureAwait(false), commit.RequestId), token);

    public Task<RemoteUpdateReceipt> CancelAsync(Guid id, CancellationToken token = default) => RunAsync(async (_, legacy, ct) =>
        VerifyReceipt(legacy ? await _legacy!.InvokeAsync<RemoteUpdateReceipt>("CancelRemoteUpdate", id, ct).ConfigureAwait(false)
            : await SendAsync(HttpMethod.Post, Prefix + id.ToString("D") + "/cancel", null, ProtocolJsonContext.Default.RemoteUpdateReceipt, ct).ConfigureAwait(false), id), token);

    public async Task<RemoteUpdateReceipt> StageAsync(string packagePath, Guid id, IProgress<long>? progress = null, CancellationToken token = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("An update request identity is required.", nameof(id));
        await using var file = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (file.Length is <= 0 or > 512L * 1024 * 1024) throw new InvalidDataException("Update packages must be between 1 byte and 512 MiB.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
        file.Position = 0;
        var prepare = new PrepareRemoteUpdateRequest(id, Path.GetFileName(packagePath), file.Length, hash);
        return await RunAsync(async (_, legacy, ct) =>
        {
            var receipt = VerifyReceipt(legacy ? await _legacy!.InvokeAsync<RemoteUpdateReceipt>("PrepareRemoteUpdate", prepare, ct).ConfigureAwait(false)
                : await SendAsync(HttpMethod.Post, Prefix + "prepare", Content(prepare, ProtocolJsonContext.Default.PrepareRemoteUpdateRequest),
                    ProtocolJsonContext.Default.RemoteUpdateReceipt, ct).ConfigureAwait(false), id);
            if (receipt.State != RemoteUpdateState.Uploading) return receipt;
            return VerifyReceipt(await SendAsync(HttpMethod.Post, (legacy ? "updates/" : Prefix) + id.ToString("D") + "/package",
                new EnvironmentClient.UpdateUploadContent(file, progress), ProtocolJsonContext.Default.RemoteUpdateReceipt, ct).ConfigureAwait(false), id);
        }, token, TimeSpan.FromMinutes(15)).ConfigureAwait(false);
    }

    private async Task<T> RunAsync<T>(Func<RemoteUpdateDescriptor, bool, CancellationToken, Task<T>> action, CancellationToken token, TimeSpan? timeout = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _stopping.Token);
        lifetime.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        var ct = lifetime.Token;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _certificateRejected = false;
            if (_options.EnsureTransportAsync is { } ensure) await ensure(ct).ConfigureAwait(false);
            using var response = await _http.GetAsync(Prefix + "descriptor", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            RemoteUpdateDescriptor descriptor;
            var legacy = response.StatusCode == HttpStatusCode.NotFound;
            if (legacy)
            {
                // Existing hosts already expose these maintenance RPCs. Isolate their
                // connection: never subscribe to a catalog or enable workspace commands.
                _legacy ??= ConnectionSupervisor.CreateConnection(_options, () => _certificateRejected = true);
                if (_legacy.State == HubConnectionState.Disconnected) await _legacy.StartAsync(ct).ConfigureAwait(false);
                var identity = await _legacy.InvokeAsync<EnvironmentDescriptor>("GetEnvironmentDescriptor", ct).ConfigureAwait(false);
                Bind(identity.EnvironmentId);
                if (identity.Capabilities.Contains("remote.access", StringComparer.Ordinal) && !identity.Capabilities.Contains("thread.operate", StringComparer.Ordinal))
                    throw new UnauthorizedAccessException("This device needs operate access to manage host updates.");
                descriptor = await _legacy.InvokeAsync<RemoteUpdateDescriptor>("GetRemoteUpdateDescriptor", ct).ConfigureAwait(false);
            }
            else
            {
                await CheckAsync(response, ct).ConfigureAwait(false);
                var maintenance = await ReadAsync(response, ProtocolJsonContext.Default.RemoteUpdateMaintenanceDescriptor, ct).ConfigureAwait(false);
                Bind(maintenance.EnvironmentId);
                if (maintenance.ApiVersion != RemoteUpdateMaintenanceDescriptor.CurrentApiVersion)
                    throw new NotSupportedException("This host uses a different update recovery API. Update the desktop or manage the host locally.");
                descriptor = maintenance.Update;
            }
            return await action(descriptor, legacy, ct).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var legacyDisconnected = _legacy is { State: HubConnectionState.Disconnected };
            if (_legacy is { } legacy) { _legacy = null; await legacy.DisposeAsync().ConfigureAwait(false); }
            if (_certificateRejected) throw new ConnectionValidationException(ConnectionFailure.Certificate, "The host certificate changed or expired. Verify its identity before updating.");
            if (error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
                throw new ConnectionValidationException(ConnectionFailure.Authentication, "Remote access expired or was revoked. Pair again before updating.");
            if (error is OperationCanceledException && !token.IsCancellationRequested && !_stopping.IsCancellationRequested)
                throw new TimeoutException("The update connection timed out. Check update status before retrying an upload or activation.", error);
            if (legacyDisconnected && error is not ConnectionValidationException && error is IOException or InvalidOperationException)
                throw new HttpRequestException("The host update connection closed. Check its status after the host restarts.", error);
            throw;
        }
        finally { _gate.Release(); }
    }

    private void Bind(EnvironmentId identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Value) || _identity is { } expected && identity != expected)
            throw new ConnectionValidationException(ConnectionFailure.Identity, "This endpoint belongs to a different environment. Verify the saved address before updating.");
        _identity = identity;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-PiStation-Environment-Id", _identity!.Value.Value);
        return request;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, JsonTypeInfo<T> type, CancellationToken token)
    {
        using var request = Request(method, path);
        request.Content = content;
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        await CheckAsync(response, token).ConfigureAwait(false);
        return await ReadAsync(response, type, token).ConfigureAwait(false);
    }

    private static ByteArrayContent Content<T>(T value, JsonTypeInfo<T> type)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, type));
        content.Headers.ContentType = new("application/json");
        return content;
    }

    private static RemoteUpdateReceipt VerifyReceipt(RemoteUpdateReceipt receipt, Guid id) => receipt.RequestId == id
        ? receipt : throw new InvalidDataException("The host returned a different update request identity.");

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken token)
    {
        const int limit = 128 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The host update response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            if (result.Length + count > limit) throw new InvalidDataException("The host update response is too large.");
            result.Write(buffer, 0, count);
        }
        return JsonSerializer.Deserialize(result.ToArray(), type) ?? throw new InvalidDataException("The host returned an empty update response.");
    }

    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new ConnectionValidationException(ConnectionFailure.Authentication, "Remote access expired or was revoked. Pair again before updating.");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("This device is not permitted to manage this host update.");
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            if (response.Content.Headers.ContentLength == 0)
                throw new ConnectionValidationException(ConnectionFailure.Identity, "The host environment identity changed. Verify the saved address before updating.");
            var error = await ReadAsync(response, ProtocolJsonContext.Default.ProtocolError, token).ConfigureAwait(false);
            throw new InvalidOperationException(error.Message);
        }
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_legacy is not null) await _legacy.DisposeAsync().ConfigureAwait(false);
            _http.Dispose();
        }
        finally { _gate.Release(); }
    }
}
