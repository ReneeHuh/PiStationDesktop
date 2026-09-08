using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime;

public sealed partial class EnvironmentClient
{
    public Task<RemoteUpdateDescriptor> GetRemoteUpdateDescriptorAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<RemoteUpdateDescriptor>("GetRemoteUpdateDescriptor", cancellationToken);

    public Task<RemoteUpdateReceipt?> GetRemoteUpdateReceiptAsync(Guid id, CancellationToken cancellationToken = default) =>
        InvokeAsync<RemoteUpdateReceipt?>("GetRemoteUpdateReceipt", id, cancellationToken);

    public Task<RemoteUpdateReceipt[]> GetRemoteUpdateHistoryAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<RemoteUpdateReceipt[]>("GetRemoteUpdateHistory", cancellationToken);

    public Task<RemoteUpdateReceipt> CommitRemoteUpdateAsync(CommitRemoteUpdateRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<RemoteUpdateReceipt>("CommitRemoteUpdate", request, cancellationToken);

    public Task<RemoteUpdateReceipt> CancelRemoteUpdateAsync(Guid id, CancellationToken cancellationToken = default) =>
        InvokeAsync<RemoteUpdateReceipt>("CancelRemoteUpdate", id, cancellationToken);

    /// <summary>Stages one package. Activation is a separate, explicit command. A lost response is resolved using its durable request ID.</summary>
    public async Task<RemoteUpdateReceipt> StageRemoteUpdateAsync(string packagePath, Guid requestId,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        await using var file = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (file.Length is <= 0 or > 512L * 1024 * 1024) throw new InvalidDataException("Update packages must be between 1 byte and 512 MiB.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        file.Position = 0;
        var prepared = await InvokeAsync<RemoteUpdateReceipt>("PrepareRemoteUpdate",
            new PrepareRemoteUpdateRequest(requestId, Path.GetFileName(packagePath), file.Length, hash), cancellationToken).ConfigureAwait(false);
        if (prepared.State != RemoteUpdateState.Uploading) return prepared;
        // Hold a dedicated transport for this upload so editing the endpoint cannot dispose it midway.
        using var http = new HttpClient(RemoteTransport.CreateHandler(_options.CertificateFingerprint))
            { BaseAddress = new Uri(_options.HubAddress, "/"), Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new("Bearer", _options.BearerCredential);
        using var content = new UpdateUploadContent(file, progress);
        using var response = await http.PostAsync($"updates/{requestId:D}/package", content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(body, ProtocolJsonContext.Default.RemoteUpdateReceipt, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The host returned an empty update receipt.");
    }

    private sealed class UpdateUploadContent(FileStream source, IProgress<long>? progress) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = source.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => CopyAsync(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => CopyAsync(stream, cancellationToken);
        private async Task CopyAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[65536];
            long sent = 0;
            while (true)
            {
                var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                progress?.Report(sent += count);
            }
        }
    }
}
