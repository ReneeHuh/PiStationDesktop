using System.Buffers;
using System.Text;
using PiStation.PiRpc.Diagnostics;

namespace PiStation.PiRpc.Transport;

public sealed class JsonlRecordReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly int _maximumRecordBytes;

    public JsonlRecordReader(int maximumRecordBytes = 16 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRecordBytes);
        _maximumRecordBytes = maximumRecordBytes;
    }

    public async Task ReadAsync(
        Stream stream,
        Func<string, CancellationToken, ValueTask> onRecord,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onRecord);

        var readBuffer = ArrayPool<byte>.Shared.Rent(8192);
        var record = new ArrayBufferWriter<byte>(1024);

        try
        {
            while (true)
            {
                var bytesRead = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                var segmentStart = 0;

                for (var index = 0; index < bytesRead; index++)
                {
                    if (readBuffer[index] != (byte)'\n')
                    {
                        continue;
                    }

                    Append(record, readBuffer.AsSpan(segmentStart, index - segmentStart));
                    await EmitRecordAsync(record, onRecord, cancellationToken).ConfigureAwait(false);
                    segmentStart = index + 1;
                }

                Append(record, readBuffer.AsSpan(segmentStart, bytesRead - segmentStart));
            }

            if (record.WrittenCount != 0)
            {
                throw new JsonlProtocolException("Pi stdout ended with an unterminated JSONL record.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private void Append(ArrayBufferWriter<byte> record, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        if (record.WrittenCount > _maximumRecordBytes - bytes.Length)
        {
            throw new JsonlProtocolException($"Pi JSONL record exceeded {_maximumRecordBytes} bytes.");
        }

        bytes.CopyTo(record.GetSpan(bytes.Length));
        record.Advance(bytes.Length);
    }

    private static async ValueTask EmitRecordAsync(
        ArrayBufferWriter<byte> record,
        Func<string, CancellationToken, ValueTask> onRecord,
        CancellationToken cancellationToken)
    {
        var payload = record.WrittenMemory;
        if (!payload.IsEmpty && payload.Span[^1] == (byte)'\r')
        {
            payload = payload[..^1];
        }

        if (payload.Span.Contains((byte)'\r'))
        {
            throw new JsonlProtocolException("Pi JSONL contained a raw carriage return outside the CRLF delimiter.");
        }

        string line;
        try
        {
            line = StrictUtf8.GetString(payload.Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new JsonlProtocolException("Pi JSONL contained malformed UTF-8.", exception);
        }

        record.Clear();
        await onRecord(line, cancellationToken).ConfigureAwait(false);
    }
}
