using System.Text;
using System.Text.Json.Nodes;
using PiStation.PiRpc.Diagnostics;
using PiStation.PiRpc.Transport;

namespace PiStation.PiRpc.Tests;

public sealed class JsonlTransportTests
{
    [Fact]
    public async Task ReaderHandlesRecordsAndUtf8SplitAcrossReads()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"text\":\"👽\"}\n{\"ok\":true}\r\n");
        await using var stream = new ChunkedMemoryStream(bytes, 1);
        var records = new List<string>();

        await new JsonlRecordReader().ReadAsync(
            stream,
            (record, _) =>
            {
                records.Add(record);
                return ValueTask.CompletedTask;
            });

        Assert.Equal(["{\"text\":\"👽\"}", "{\"ok\":true}"], records);
    }

    [Theory]
    [InlineData("{\"bad\":\"raw\rreturn\"}\n", "raw carriage return")]
    [InlineData("{\"unterminated\":true}", "unterminated")]
    public async Task ReaderRejectsInvalidFraming(string input, string expectedMessage)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));

        var exception = await Assert.ThrowsAsync<JsonlProtocolException>(() =>
            new JsonlRecordReader().ReadAsync(stream, static (_, _) => ValueTask.CompletedTask));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReaderRejectsMalformedUtf8()
    {
        await using var stream = new MemoryStream([0xC3, 0x28, (byte)'\n']);

        var exception = await Assert.ThrowsAsync<JsonlProtocolException>(() =>
            new JsonlRecordReader().ReadAsync(stream, static (_, _) => ValueTask.CompletedTask));

        Assert.Contains("UTF-8", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriterUsesBomlessUtf8AndLfOnly()
    {
        await using var stream = new MemoryStream();
        await using (var writer = new JsonlRecordWriter(stream))
        {
            await writer.WriteAsync(new JsonObject { ["message"] = "hello 👽" });
        }

        var bytes = stream.ToArray();
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.DoesNotContain((byte)'\r', bytes);
        var record = JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimEnd('\n'));
        Assert.Equal("hello 👽", record?["message"]?.GetValue<string>());
    }

    private sealed class ChunkedMemoryStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }
}
