using System.Net;
using System.Security.Cryptography;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class AttachmentFileCacheTests
{
    [Fact]
    public async Task RevalidatesCacheRepairsCorruptionAndCoalescesConcurrentDownloads()
    {
        using var directory = new ClientTestDirectory();
        var root = directory.CreateDirectory("client-cache");
        var bytes = "An attachment that exists only on the host"u8.ToArray();
        var attachment = Attachment(bytes);
        var downloads = 0;
        var validations = 0;
        using var http = Client((request, _) =>
        {
            Assert.Equal(attachment.EnvironmentId.Value, Assert.Single(request.Headers.GetValues("X-PiStation-Environment-Id")));
            Assert.DoesNotContain(attachment.ServerPath, request.RequestUri!.ToString(), StringComparison.Ordinal);
            if (request.Headers.IfNoneMatch.Count == 1)
            {
                validations++;
                return Task.FromResult(Response(attachment, [], HttpStatusCode.NotModified));
            }
            downloads++;
            return Task.FromResult(Response(attachment, bytes));
        });
        var paths = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => AttachmentFileCache.GetFileAsync(http, attachment, root, default)));
        Assert.Single(paths.Distinct());
        Assert.Equal(1, downloads);
        Assert.Equal(3, validations);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(paths[0]));
        await File.WriteAllBytesAsync(paths[0], new byte[bytes.Length]);
        Assert.Equal(paths[0], await AttachmentFileCache.GetFileAsync(http, attachment, root, default));
        Assert.Equal(2, downloads);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(paths[0]));
        Assert.Single(Directory.GetFiles(root));
        var other = attachment with { EnvironmentId = EnvironmentId.New() };
        using var otherHttp = Client((_, _) => Task.FromResult(Response(other, bytes)));
        Assert.NotEqual(paths[0], await AttachmentFileCache.GetFileAsync(otherHttp, other, root, default));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task NeverFallsBackToCachedBytesWhenHostDeniesAccess(HttpStatusCode status)
    {
        using var directory = new ClientTestDirectory();
        var root = directory.CreateDirectory("cache");
        var bytes = "private attachment"u8.ToArray();
        var attachment = Attachment(bytes);
        using var success = Client((_, _) => Task.FromResult(Response(attachment, bytes)));
        await AttachmentFileCache.GetFileAsync(success, attachment, root, default);
        using var denied = Client((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => AttachmentFileCache.GetFileAsync(denied, attachment, root, default));
        Assert.Equal(status, error.StatusCode);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("corrupt")]
    [InlineData("wrong-etag")]
    public async Task RejectsInvalidBodiesWithoutPublishingAFile(string fault)
    {
        using var directory = new ClientTestDirectory();
        var root = directory.CreateDirectory("cache");
        var bytes = "expected file content"u8.ToArray();
        var attachment = Attachment(bytes);
        using var http = Client((_, _) =>
        {
            var payload = fault switch
            {
                "truncated" => bytes[..^1],
                "oversized" => [.. bytes, 0],
                "corrupt" => new byte[bytes.Length],
                _ => bytes,
            };
            var response = Response(attachment, []);
            // Test bounded streaming as well as the header checks (unknown length).
            response.Content.Dispose();
            response.Content = new StreamContent(new NonSeekableStream(payload));
            if (fault == "wrong-etag") response.Headers.ETag = new("\"wrong\"");
            return Task.FromResult(response);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => AttachmentFileCache.GetFileAsync(http, attachment, root, default));
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public async Task InterruptedDownloadRemovesTemporaryFileAndCanBeRetried()
    {
        using var directory = new ClientTestDirectory();
        var root = directory.CreateDirectory("cache");
        var bytes = new byte[128 * 1024];
        var attachment = Attachment(bytes);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = Client((_, _) =>
        {
            var response = Response(attachment, []);
            response.Content = new StreamContent(new InterruptedStream(bytes, blocked));
            return Task.FromResult(response);
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var download = AttachmentFileCache.GetFileAsync(http, attachment, root, cancellation.Token);
        await blocked.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Empty(Directory.GetFiles(root));
        using var retry = Client((_, _) => Task.FromResult(Response(attachment, bytes)));
        var file = await AttachmentFileCache.GetFileAsync(retry, attachment, root, default);
        Assert.Equal(bytes.Length, new FileInfo(file).Length);
    }

    [Fact]
    public async Task PrunesExpiredCacheFilesAndAbandonedDownloadsButLeavesOtherFiles()
    {
        using var directory = new ClientTestDirectory();
        var root = directory.CreateDirectory("cache");
        var expired = Path.Combine(root, new string('A', 64) + ".txt");
        var partial = Path.Combine(root, new string('B', 64) + "." + Guid.NewGuid().ToString("N") + ".partial");
        foreach (var path in new[] { expired, partial })
        {
            await File.WriteAllTextAsync(path, "old");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
        }
        var unrelated = Path.Combine(root, "keep.txt");
        await File.WriteAllTextAsync(unrelated, "keep");
        var bytes = "new"u8.ToArray();
        var attachment = Attachment(bytes);
        using var http = Client((_, _) => Task.FromResult(Response(attachment, bytes)));
        var downloaded = await AttachmentFileCache.GetFileAsync(http, attachment, root, default);
        Assert.False(File.Exists(expired));
        Assert.False(File.Exists(partial));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(downloaded));
    }

    private static DraftAttachment Attachment(byte[] bytes) => new(EnvironmentId.New(), ThreadId.New(), DraftId.New(),
        AttachmentId.New(), "remote.txt", "text/plain", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)),
        "/host-only/storage/remote.txt", DateTimeOffset.UtcNow);

    private static HttpResponseMessage Response(DraftAttachment attachment, byte[] bytes, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
        response.Headers.ETag = new($"\"{attachment.Sha256}\"");
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

    private sealed class InterruptedStream(byte[] bytes, TaskCompletionSource blocked) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position > 0)
            {
                blocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
