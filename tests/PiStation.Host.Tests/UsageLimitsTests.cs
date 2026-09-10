using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PiStation.Host.Usage;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Tests;

public sealed class UsageLimitsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions FeedJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static UsageLimitsService Service(HostTestDirectory directory, HttpClient? http = null) => new(directory.Path, () => (false, "Paused for fixture"), http);
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private const string Hub = """{"accounts":{"codex-fixture.json":{"provider":"codex","plan":"pro","five_hour":{"used_percent":64,"reset_at":"2026-09-10T14:00:00Z"},"weekly":{"used_percent":3,"hard_limited":true},"seven_day":{"used_percent":50,"known":false}},"other":{"provider":"future-provider","five_hour":{"used_percent":50}}}}""";

    [Fact]
    public void HubMappingHonorsHardLimitsUnknownWindowsAndUnsupportedProviders()
    {
        var accounts = UsageLimitAdapters.CliProxy(Bytes(Hub), Now);
        var codex = Assert.Single(accounts, a => a.Provider == "codex");
        Assert.Equal("pro", codex.Plan); Assert.Equal(Now, codex.CheckedAt);
        Assert.Collection(codex.Windows, w => { Assert.Equal(64, w.UsedPercent); Assert.Equal(300, w.WindowDurationMins); Assert.Equal(Now.AddHours(2), w.ResetsAt); },
            w => { Assert.Equal("secondary", w.Id); Assert.Equal(100, w.UsedPercent); Assert.Equal(10080, w.WindowDurationMins); });
        var unsupported = Assert.Single(accounts, a => a.Provider == "future-provider"); Assert.Equal("unsupported", unsupported.Unavailable); Assert.Empty(unsupported.Windows);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"accounts\":[]}")]
    [InlineData("{\"accounts\":{\"a\":{\"provider\":\"codex\",\"five_hour\":{\"used_percent\":1e1000}}}}")]
    [InlineData("{\"accounts\":{\"a\":{\"provider\":\"codex\",\"five_hour\":{\"used_percent\":10,\"known\":\"true\"}}}}")]
    [InlineData("{\"accounts\":{\"a\":{\"provider\":\"codex\"},\"a\":{\"provider\":\"claude\"}}}")]
    public void MalformedHubPayloadIsRejected(string json) => Assert.Throws<JsonException>(() => UsageLimitAdapters.CliProxy(Bytes(json), Now));

    [Fact]
    public async Task ProtectedSettingsSurviveRestartAndNeverReturnManagementKeys()
    {
        using var directory = new HostTestDirectory();
        UsageLimitsDashboard saved;
        await using (var service = Service(directory))
        {
            saved = service.Save(new(0, null, "Fixture", "http://127.0.0.1:8317", false, "isolated-fixture-secret"));
            Assert.True(Assert.Single(saved.Settings.Sources).HasKey);
            Assert.DoesNotContain("isolated-fixture-secret", JsonSerializer.Serialize(saved, ProtocolJsonContext.Default.UsageLimitsDashboard));
            Assert.DoesNotContain("isolated-fixture-secret", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(directory.GetPath("usage-limit-sources.protected"))));
            Assert.Empty(service.Snapshot(includeSettings: false).Settings.Sources);
            Assert.Throws<InvalidOperationException>(() => service.Save(new(0, null, "Stale", "https://example.invalid", true, "fixture")));
        }
        await using (var service = Service(directory))
        {
            var configuration = Assert.Single(service.Snapshot().Settings.Sources);
            Assert.Equal(saved.Settings.Sources[0], configuration);
            var changed = service.Save(new(1, configuration.Id, "Renamed", configuration.BaseUrl, true));
            Assert.Equal("Renamed", Assert.Single(changed.Settings.Sources).Label);
            Assert.Throws<ArgumentException>(() => service.Save(new(2, configuration.Id, "Move", "https://other.invalid", true)));
            Assert.Empty(service.Remove(new(2, configuration.Id)).Settings.Sources);
        }
        await using var restarted = Service(directory); Assert.Empty(restarted.Snapshot().Settings.Sources);
    }
    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com?token=secret")]
    [InlineData("https://example.com/#fragment")]
    [InlineData("file:///tmp")]
    public async Task UnsafeHubOriginsAreRejected(string origin)
    {
        using var directory = new HostTestDirectory(); await using var service = Service(directory);
        Assert.Throws<ArgumentException>(() => service.Save(new(0, null, "Fixture", origin, true, "key")));
        Assert.False(File.Exists(directory.GetPath("usage-limit-sources.protected")));
    }
    [Fact]
    public async Task FailedProbesRetainBarsAndSafeErrorsWhileSuccessfulSnapshotReplacesWindows()
    {
        using var directory = new HostTestDirectory();
        var response = Hub; var status = HttpStatusCode.OK; var calls = 0;
        using var http = new HttpClient(new Handler((request, token) =>
        {
            calls++; Assert.Equal("Bearer fixture-key", request.Headers.Authorization!.ToString());
            Assert.Equal("/v0/management/quota-scheduler/status", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(response) });
        }));
        await using var service = Service(directory, http);
        service.Save(new(0, null, "Fixture", "https://example.invalid", true, "fixture-key"));
        var good = Assert.Single((await service.RefreshAsync()).Sources, s => s.Kind == "cliproxy");
        status = HttpStatusCode.Unauthorized; response = "fixture-key private upstream body";
        var failed = Assert.Single((await service.RefreshAsync()).Sources, s => s.Kind == "cliproxy");
        Assert.True(failed.Stale); Assert.Equal(good.CheckedAt, failed.CheckedAt); Assert.Equal(good.Accounts, failed.Accounts);
        Assert.Contains("401", failed.Error); Assert.DoesNotContain("fixture-key", failed.Error); Assert.DoesNotContain("upstream", failed.Error);
        status = HttpStatusCode.OK; response = "{invalid";
        Assert.Equal(good.Accounts, Assert.Single((await service.RefreshAsync()).Sources, s => s.Kind == "cliproxy").Accounts);
        response = "{\"accounts\":{}}";
        var cleared = Assert.Single((await service.RefreshAsync()).Sources, s => s.Kind == "cliproxy");
        Assert.Empty(cleared.Accounts); Assert.False(cleared.Stale); Assert.Null(cleared.Error); Assert.Equal(4, calls);
    }
    [Fact]
    public async Task RemovingSourceDuringAProbeDoesNotResurrectIt()
    {
        using var directory = new HostTestDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, token) => { started.SetResult(); await release.Task.WaitAsync(token); return new(HttpStatusCode.OK) { Content = new StringContent(Hub) }; }));
        await using var service = Service(directory, http);
        var saved = service.Save(new(0, null, "Fixture", "http://localhost:8317", true, "fixture"));
        var pending = service.RefreshAsync(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Remove(new(saved.Settings.Revision, saved.Settings.Sources[0].Id)); release.SetResult();
        Assert.DoesNotContain((await pending).Sources, s => s.Kind == "cliproxy"); Assert.Empty(service.Snapshot().Settings.Sources);
    }
    [Fact]
    public async Task CancellationDoesNotPublishAnIncompleteRefresh()
    {
        using var directory = new HostTestDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, token) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); }));
        await using var service = Service(directory, http); service.Save(new(0, null, "Fixture", "https://example.invalid", true, "fixture"));
        using var cancel = new CancellationTokenSource(); var pending = service.RefreshAsync(cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("Not checked yet.", Assert.Single(service.Snapshot().Sources, s => s.Kind == "cliproxy").Error);
    }
    [Fact]
    public async Task OversizedPayloadIsBoundedAndNeverReachesTheClient()
    {
        using var stream = new MemoryStream(new byte[UsageLimitAdapters.MaximumBytes + 1]);
        await Assert.ThrowsAsync<JsonException>(() => UsageLimitAdapters.ReadBoundedAsync(stream, CancellationToken.None));
    }
    [Fact]
    public async Task ResponseBodyIsIncludedInTheTenSecondDeadline()
    {
        using var directory = new HostTestDirectory();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) })));
        await using var service = Service(directory, http);
        service.Save(new(0, null, "Fixture", "https://example.invalid", true, "fixture"));
        var snapshot = await service.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var source = Assert.Single(snapshot.Sources, s => s.Kind == "cliproxy"); Assert.Contains("timed out", source.Error); Assert.True(source.Stale);
    }
    [Fact]
    public async Task ConcurrentRefreshesShareTheCompletedProbe()
    {
        using var directory = new HostTestDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        using var http = new HttpClient(new Handler(async (_, token) => { Interlocked.Increment(ref calls); started.TrySetResult(); await release.Task.WaitAsync(token); return new(HttpStatusCode.OK) { Content = new StringContent(Hub) }; }));
        await using var service = Service(directory, http);
        service.Save(new(0, null, "Fixture", "https://example.invalid", true, "fixture"));
        var first = service.RefreshAsync(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.RefreshAsync(); release.SetResult(); await Task.WhenAll(first, second);
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task ProductionHttpClientDoesNotFollowRedirectsOrReuseHubCookies()
    {
        using var directory = new HostTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var responder = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var headers = new List<string>(); string? line;
                while ((line = await reader.ReadLineAsync(timeout.Token)) is { Length: > 0 }) headers.Add(line);
                Assert.Contains("Authorization: Bearer fixture-key", headers); Assert.DoesNotContain(headers, h => h.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase));
                var response = index == 0 ? "HTTP/1.1 200 OK\r\nContent-Length: 15\r\nSet-Cookie: secret=cookie; Path=/\r\nConnection: close\r\n\r\n{\"accounts\":{}}" :
                    $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/redirect\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Bytes(response), timeout.Token);
            }
        }, timeout.Token);
        await using var service = Service(directory);
        service.Save(new(0, null, "Fixture", $"http://127.0.0.1:{port}", true, "fixture-key"));
        Assert.Null(Assert.Single((await service.RefreshAsync(timeout.Token)).Sources, s => s.Kind == "cliproxy").Error);
        Assert.Contains("HTTP 302", Assert.Single((await service.RefreshAsync(timeout.Token)).Sources, s => s.Kind == "cliproxy").Error);
        await responder;
    }
    [Fact]
    public async Task PiFeedsReportStalenessAndRetainLastGoodReadingOnPartialWrites()
    {
        using var directory = new HostTestDirectory(); await using var service = Service(directory);
        var path = Path.Combine(directory.CreateDirectory("quota-feeds"), "fixture.json");
        var feed = new { version = 1, id = "fixture", label = "Pi fixture", checkedAt = Now, accounts = new[] {
            new { id = "a", provider = "pi-fixture", label = "Account", checkedAt = Now, windows = new[] { new UsageLimitWindow("session", "session", "Session", 42, Now.AddHours(2), 300) } } } };
        // Feed contract uses camelCase regardless of host serializer policy.
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(feed, FeedJson));
        var good = Assert.Single((await service.RefreshAsync()).Sources); Assert.True(good.Stale); Assert.Equal(42, Assert.Single(Assert.Single(good.Accounts).Windows).UsedPercent);
        await File.WriteAllTextAsync(path, "{partial");
        var failed = Assert.Single((await service.RefreshAsync()).Sources); Assert.True(failed.Stale); Assert.Equal(good.Accounts, failed.Accounts); Assert.Contains("retained", failed.Error);
        File.Delete(path); Assert.Contains("Unsupported", Assert.Single((await service.RefreshAsync()).Sources).Error);
    }
    [Fact]
    public async Task UnreadableProtectedSettingsCannotBeSilentlyOverwritten()
    {
        using var directory = new HostTestDirectory();
        var path = directory.GetPath("usage-limit-sources.protected"); await File.WriteAllTextAsync(path, "not encrypted");
        await using var service = Service(directory); Assert.NotNull(service.Snapshot().Settings.Error);
        Assert.Throws<InvalidOperationException>(() => service.Save(new(0, null, "Fixture", "https://example.invalid", true, "fixture")));
        Assert.Equal("not encrypted", await File.ReadAllTextAsync(path));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class BlockingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
}
