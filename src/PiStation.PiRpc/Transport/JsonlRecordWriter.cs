using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiStation.PiRpc.Transport;

public sealed class JsonlRecordWriter : IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8WithoutBom = new(false, true);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly StreamWriter _writer;

    public JsonlRecordWriter(Stream stream, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _writer = new StreamWriter(stream, StrictUtf8WithoutBom, 4096, leaveOpen)
        {
            AutoFlush = false,
            NewLine = "\n",
        };
    }

    public async ValueTask WriteAsync(JsonObject record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var json = record.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
            _writeLock.Dispose();
        }
    }
}
