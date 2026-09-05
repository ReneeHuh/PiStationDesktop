using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace PiStation.FakePi;

internal static class FakeJsonlReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async IAsyncEnumerable<string> ReadAllAsync(
        Stream input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var readBuffer = new byte[1024];
        var record = new ArrayBufferWriter<byte>();

        while (true)
        {
            var count = await input.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                yield break;
            }

            var segmentStart = 0;
            for (var index = 0; index < count; index++)
            {
                if (readBuffer[index] != (byte)'\n')
                {
                    continue;
                }

                Append(record, readBuffer.AsSpan(segmentStart, index - segmentStart));
                var payload = record.WrittenSpan;
                if (!payload.IsEmpty && payload[^1] == (byte)'\r')
                {
                    payload = payload[..^1];
                }

                yield return StrictUtf8.GetString(payload);
                record.Clear();
                segmentStart = index + 1;
            }

            Append(record, readBuffer.AsSpan(segmentStart, count - segmentStart));
        }
    }

    private static void Append(ArrayBufferWriter<byte> destination, ReadOnlySpan<byte> value)
    {
        value.CopyTo(destination.GetSpan(value.Length));
        destination.Advance(value.Length);
    }
}
