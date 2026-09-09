namespace PiStation.ClientRuntime.Tests;

public sealed class TailscaleServeTests
{
    private static readonly TailscaleMachine Self = new("Fixture", "fixture.example.ts.net", "100.64.1.2", true);
    private static readonly string[] StatusCommand = ["serve", "status", "--json"];
    private static readonly string[] ForwardCommand = ["serve", "--tcp=8443", "tcp://127.0.0.1:12345"];
    private const string Forward = """{"Foreground":{"owned":{"TCP":{"8443":{"TCPForward":"127.0.0.1:12345"}}}}}""";

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"TCP\":{\"443\":{\"HTTPS\":true}}}")]
    [InlineData("{\"AllowFunnel\":{\"host.example.ts.net:8443\":false}}")]
    public void UnusedPortDoesNotDisturbOtherMappings(string json) => TailscaleServeConfiguration.EnsurePortAvailable(json, 8443);

    [Theory]
    [InlineData("{\"TCP\":{\"8443\":{\"HTTPS\":true}}}")]
    [InlineData("{\"TCP\":{\"8443\":null}}")]
    [InlineData("{\"Web\":{\"host.example.ts.net:8443\":{}}}")]
    [InlineData("{\"AllowFunnel\":{\"host.example.ts.net:8443\":true}}")]
    [InlineData(Forward)]
    public void ExistingForegroundBackgroundWebAndFunnelPortsAreProtected(string json) =>
        Assert.Throws<InvalidOperationException>(() => TailscaleServeConfiguration.EnsurePortAvailable(json, 8443));

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"TCP\":[]}")]
    [InlineData("{\"Foreground\":[{}]}")]
    [InlineData("{\"AllowFunnel\":{\"host.example.ts.net:8443\":\"true\"}}")]
    public void MalformedConfigurationFailsClosed(string json) =>
        Assert.Throws<System.Text.Json.JsonException>(() => TailscaleServeConfiguration.EnsurePortAvailable(json, 8443));

    [Fact]
    public void ReadinessRequiresOneExactUnterminatedPrivateForward()
    {
        Assert.True(TailscaleServeConfiguration.HasForward(Forward, 8443, 12345));
        Assert.False(TailscaleServeConfiguration.HasForward(Forward, 8443, 12346));
        Assert.False(TailscaleServeConfiguration.HasForward("""{"TCP":{"8443":{"TCPForward":"127.0.0.1:12345"}}}""", 8443, 12345));
        Assert.False(TailscaleServeConfiguration.HasForward(Forward.Replace("\"TCPForward\"", "\"TerminateTLS\":\"fixture.example.ts.net\",\"TCPForward\"", StringComparison.Ordinal), 8443, 12345));
        Assert.False(TailscaleServeConfiguration.HasForward(Forward.Replace("\"Foreground\"", "\"AllowFunnel\":{\"fixture.example.ts.net:8443\":true},\"Foreground\"", StringComparison.Ordinal), 8443, 12345));
        Assert.False(TailscaleServeConfiguration.HasForward(Forward.Replace("\"Foreground\"", "\"TCP\":{\"8443\":{\"TCPForward\":\"127.0.0.1:12345\"}},\"Foreground\"", StringComparison.Ordinal), 8443, 12345));
    }

    [Fact]
    public async Task StartsForegroundForwardAndVerifiesBeforeReturning()
    {
        var process = new FakeProcess();
        var queries = 0;
        var probes = 0;
        await using (var session = await TailscaleServeSession.StartAsync(Self, 12345, 8443,
            (args, _) =>
            {
                Assert.Equal(StatusCommand, args);
                return Task.FromResult(++queries == 1 ? "{}" : Forward);
            }, args =>
            {
                Assert.Equal(ForwardCommand, args);
                return process;
            }, (address, _) =>
            {
                Assert.Equal(new Uri("https://fixture.example.ts.net:8443/"), address);
                probes++;
                return Task.CompletedTask;
            }, CancellationToken.None))
        {
            Assert.True(session.IsRunning);
            Assert.False(process.Disposed);
            Assert.Equal(1, probes);
        }
        Assert.True(process.Disposed);
        Assert.Equal(2, queries); // Stop owns the foreground process; it never issues a global reset/off.
    }

    [Fact]
    public async Task PortConflictNeverStartsAProcess()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => TailscaleServeSession.StartAsync(Self, 12345, 8443,
            (_, _) => Task.FromResult(Forward), _ => throw new Xunit.Sdk.XunitException("Must not start."),
            (_, _) => throw new Xunit.Sdk.XunitException("Must not probe."), CancellationToken.None));
    }

    [Fact]
    public async Task CancellationAfterPreflightNeverStartsSharing()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TailscaleServeSession.StartAsync(Self, 12345, 8443,
            async (_, _) => { await cancellation.CancelAsync(); return "{}"; },
            _ => throw new Xunit.Sdk.XunitException("Canceled sharing must not start."),
            (_, _) => throw new Xunit.Sdk.XunitException("Must not probe."), cancellation.Token));
    }

    [Fact]
    public async Task FailedIdentityProbeClosesOnlyTheOwnedProcess()
    {
        var process = new FakeProcess();
        var queries = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => TailscaleServeSession.StartAsync(Self, 12345, 8443,
            (_, _) => Task.FromResult(++queries == 1 ? "{}" : Forward), _ => process,
            (_, _) => throw new InvalidDataException("Wrong host identity."), CancellationToken.None));
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task CancellationDuringProbeClosesTheForegroundLease()
    {
        var process = new FakeProcess();
        var queries = 0;
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TailscaleServeSession.StartAsync(Self, 12345, 8443,
            (_, _) => Task.FromResult(++queries == 1 ? "{}" : Forward), _ => process,
            async (_, token) => { await cancel.CancelAsync(); token.ThrowIfCancellationRequested(); }, cancel.Token));
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task ExitedProcessCannotReportVerifiedSharing()
    {
        var process = new FakeProcess();
        var queries = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => TailscaleServeSession.StartAsync(Self, 12345, 8443,
            (_, _) => Task.FromResult(++queries == 1 ? "{}" : Forward), _ => process,
            (_, _) => { process.HasExited = true; return Task.CompletedTask; }, CancellationToken.None));
        Assert.True(process.Disposed);
    }

    private sealed class FakeProcess : ITailscaleServeProcess
    {
        public bool HasExited { get; set; }
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; HasExited = true; return ValueTask.CompletedTask; }
    }
}
