using System.Net;
using System.Security.Cryptography;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class PiSessionFileTransferTests
{
    [Theory]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("corrupt")]
    [InlineData("missing-hash")]
    [InlineData("weak-hash")]
    [InlineData("missing-length")]
    [InlineData("unauthorized")]
    public async Task FailedExportsPreserveExistingDestinationAndRemoveTemporaryFiles(string fault)
    {
        using var directory = new ClientTestDirectory();
        var destination = Path.Combine(directory.Path, "saved.jsonl");
        await File.WriteAllTextAsync(destination, "previous file");
        var expected = "exported session"u8.ToArray();
        var payload = fault switch
        {
            "truncated" => expected[..^1],
            "oversized" => [.. expected, 0],
            "corrupt" => new byte[expected.Length],
            _ => expected,
        };
        using var http = Client((request, _) =>
        {
            Assert.DoesNotContain("saved.jsonl", request.RequestUri!.ToString(), StringComparison.Ordinal);
            var response = Response(payload, expected);
            if (fault == "missing-hash") response.Headers.ETag = null;
            if (fault == "weak-hash") response.Headers.ETag = new(response.Headers.ETag!.Tag, isWeak: true);
            if (fault == "unauthorized") response.StatusCode = HttpStatusCode.Unauthorized;
            if (fault == "missing-length")
            {
                response.Content.Dispose();
                response.Content = new StreamContent(new NonSeekableStream(payload));
            }
            return Task.FromResult(response);
        });
        if (fault == "unauthorized")
            await Assert.ThrowsAsync<HttpRequestException>(() => PiSessionFileTransfer.DownloadAsync(http, ThreadId.New(), destination, PiSessionExportFormat.Jsonl, null, default));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => PiSessionFileTransfer.DownloadAsync(http, ThreadId.New(), destination, PiSessionExportFormat.Jsonl, null, default));
        Assert.Equal("previous file", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledOrDisconnectedDownloadsCanBeRetriedWithoutDamagingTheDestination(bool disconnect)
    {
        using var directory = new ClientTestDirectory();
        var destination = Path.Combine(directory.Path, "export.zip");
        await File.WriteAllTextAsync(destination, "previous export");
        var bytes = new byte[256 * 1024];
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = Client((_, _) =>
        {
            var response = Response([], bytes);
            response.Content.Dispose();
            response.Content = new StreamContent(new InterruptedStream(bytes, paused, disconnect));
            return Task.FromResult(response);
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var download = PiSessionFileTransfer.DownloadAsync(http, ThreadId.New(), destination, PiSessionExportFormat.Bundle, null, cancellation.Token);
        await paused.Task.WaitAsync(cancellation.Token);
        if (disconnect) await Assert.ThrowsAsync<IOException>(() => download);
        else
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        }
        Assert.Equal("previous export", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial"));
        using var retry = Client((_, _) => Task.FromResult(Response(bytes, bytes)));
        var result = await PiSessionFileTransfer.DownloadAsync(retry, ThreadId.New(), destination, PiSessionExportFormat.Bundle, null, default);
        Assert.Equal(bytes.Length, result.Bytes);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public void ImportMetadataRejectsOversizedFilesUnsafeExtensionsAndInvalidIdentities()
    {
        var request = new PiSessionImportRequest(Guid.NewGuid(), ProjectId.New(), ".jsonl", 1, new string('A', 64));
        request.Validate();
        foreach (var invalid in new[]
        {
            request with { ByteLength = 0 },
            request with { ByteLength = PiSessionTransferDefaults.MaximumJsonlBytes + 1 },
            request with { Extension = ".zip", ByteLength = PiSessionTransferDefaults.MaximumTransferBytes + 1 },
            request with { Extension = "../outside.jsonl" },
            request with { Sha256 = new string('Z', 64) },
            request with { OperationId = Guid.Empty },
            request with { ProjectId = default },
        }) Assert.Throws<ArgumentException>(invalid.Validate);
    }

    private static HttpResponseMessage Response(byte[] payload, byte[] expected)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        response.Content.Headers.ContentLength = expected.Length;
        response.Headers.ETag = new($"\"{Convert.ToHexString(SHA256.HashData(expected))}\"");
        return response;
    }

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new Handler(send)) { BaseAddress = new Uri("https://host.invalid/") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class InterruptedStream(byte[] bytes, TaskCompletionSource paused, bool disconnect) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position > 0)
            {
                paused.TrySetResult();
                if (disconnect) throw new IOException("Simulated connection loss.");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
