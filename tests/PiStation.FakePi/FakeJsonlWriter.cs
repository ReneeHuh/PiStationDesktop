using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiStation.FakePi;

internal sealed class FakeJsonlWriter(Stream output) : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task WriteAsync(
        JsonObject record,
        bool splitInsideMultibyteText = false,
        bool terminate = true,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(record, SerializerOptions);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (splitInsideMultibyteText && payload.Length > 8)
            {
                var split = FindMultibyteSplit(payload);
                await output.WriteAsync(payload.AsMemory(0, split), cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                await Task.Yield();
                await output.WriteAsync(payload.AsMemory(split), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            if (terminate)
            {
                await output.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task WriteRawAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    private static int FindMultibyteSplit(byte[] payload)
    {
        for (var index = 1; index < payload.Length; index++)
        {
            if ((payload[index] & 0xC0) == 0x80)
            {
                return index;
            }
        }

        return payload.Length / 2;
    }
}
