using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PiStation.Host.Diagnostics;
using PiStation.Protocol.Models;
using PiStation.Protocol.Platform;

namespace PiStation.Host.Tests;

public sealed class RuntimeHealthTests
{
    [Fact]
    public void ActivityLeasesExpireDisconnectAndResumeAcrossClients()
    {
        var clock = new TestClock();
        var policy = new BackgroundActivityPolicy(clock);
        var power = new PowerState(null, null, null, clock.GetUtcNow());
        Assert.False(policy.Evaluate(new(), power).RunBackgroundRefresh);
        policy.Report("a", new(true, true, true, null, null));
        Assert.True(policy.Evaluate(new(), power).RunDiagnostics);
        policy.Report("b", new(true, true, false, false, false));
        policy.Remove("a");
        Assert.True(policy.Evaluate(new(), power).RunBackgroundRefresh);
        Assert.False(policy.Evaluate(new(), power).RunDiagnostics);
        clock.Now += TimeSpan.FromSeconds(45);
        Assert.Equal(0, policy.Evaluate(new(), power).ActiveClients);
        policy.Report("b", new(true, true, true, false, false));
        Assert.True(policy.Evaluate(new(), power).RunDiagnostics);
        policy.Remove("b");
        Assert.False(policy.Evaluate(new(), power).RunBackgroundRefresh);
    }

    [Theory]
    [InlineData(true, true, false, false, false, false, true)]
    [InlineData(false, true, false, false, false, false, false)]
    [InlineData(true, false, false, false, false, false, false)]
    [InlineData(true, true, true, false, false, false, false)]
    [InlineData(true, true, false, true, false, false, false)]
    [InlineData(true, true, false, false, true, false, false)]
    [InlineData(true, true, false, false, false, true, false)]
    public void BalancedAndBatteryPoliciesHonorSignals(bool visible, bool focused, bool locked, bool hostLow, bool clientLow, bool battery, bool allowed)
    {
        var clock = new TestClock();
        var policy = new BackgroundActivityPolicy(clock);
        policy.Report("a", new(visible, focused, true, battery, clientLow));
        Assert.Equal(allowed, policy.Evaluate(new(PauseWhenOnBattery: true), new(locked, false, hostLow, clock.Now)).RunDiagnostics);
    }

    [Fact]
    public void PerformanceAcceptsVisibleBackgroundWindowsButStalePowerIsUnknown()
    {
        var clock = new TestClock();
        var policy = new BackgroundActivityPolicy(clock);
        policy.Report("a", new(true, false, true, false, false));
        var settings = new RuntimeHealthSettings(BackgroundProfile: "performance");
        Assert.True(policy.Evaluate(settings, new(false, false, false, clock.Now)).RunDiagnostics);
        Assert.False(policy.Evaluate(settings, new(true, false, false, clock.Now)).RunDiagnostics);
        var stale = policy.Evaluate(settings, new(true, true, true, clock.Now.AddMinutes(-2)));
        Assert.True(stale.RunDiagnostics);
        Assert.Null(stale.HostPower.Locked);
    }

    [Fact]
    public void AnotherClientsDemandDoesNotEnableRefreshForAHiddenClient()
    {
        var clock = new TestClock(); var policy = new BackgroundActivityPolicy(clock);
        var settings = new RuntimeHealthSettings(BackgroundProfile: "performance");
        var hidden = new ClientActivityReport(false, false, true, false, false);
        policy.Report("foreground", new(true, true, true, false, false)); policy.Report("hidden", hidden);
        Assert.True(policy.Evaluate(settings, new(false, false, false, clock.Now)).RunDiagnostics);
        Assert.False(BackgroundActivityRules.IsClientEligible(settings, hidden));
    }

    [Fact]
    public async Task SettingsPersistRejectStaleRevisionsAndDoNotEnableExportByDefault()
    {
        using var directory = new HostTestDirectory();
        RuntimeHealthSettings saved;
        await using (var health = new RuntimeHealthService(directory.Path, () => []))
        {
            Assert.Null(health.Snapshot(false).Background.Settings.OtlpEndpoint);
            saved = health.Save(new(BackgroundProfile: "battery-saver", PauseWhenOnBattery: true, TracingEnabled: true));
            Assert.Throws<InvalidOperationException>(() => health.Save(new()));
            Assert.Throws<ArgumentException>(() => health.Save(saved with { OtlpEndpoint = "http://example.com/v1/traces" }));
            Assert.Throws<ArgumentException>(() => health.Save(saved with { OtlpEndpoint = "https://user:secret@example.com" }));
            Assert.Throws<ArgumentException>(() => health.Save(saved with { TraceSampleRate = double.NaN }));
        }
        await using var reloaded = new RuntimeHealthService(directory.Path, () => []);
        Assert.Equal(saved, reloaded.Snapshot(false).Background.Settings);
    }

    [Fact]
    public async Task TracesAreBoundedMetricsAggregateAndClearPreservesSettings()
    {
        using var directory = new HostTestDirectory();
        await using var health = new RuntimeHealthService(directory.Path, () => []);
        var settings = health.Save(new(TracingEnabled: true));
        for (var i = 0; i < 600; i++) { using var scope = health.Begin("TestOperation"); if (i % 2 == 0) scope.Outcome = "error"; }
        var snapshot = health.Snapshot(false);
        Assert.Equal(512, snapshot.Traces.Count);
        var metric = Assert.Single(snapshot.Metrics);
        Assert.Equal(600, metric.Count); Assert.Equal(300, metric.Failures);
        Assert.All(snapshot.Traces, trace => { Assert.Equal(32, trace.TraceId.Length); Assert.Equal(16, trace.SpanId.Length); Assert.True(trace.DurationMilliseconds >= 0); });
        health.Clear();
        Assert.Empty(health.Snapshot(false).Traces); Assert.Empty(health.Snapshot(false).Metrics);
        Assert.Equal(settings, health.Snapshot(false).Background.Settings);
        health.Save(settings with { TraceSampleRate = 0, MetricsEnabled = false });
        using (health.Begin("Disabled")) { }
        Assert.Empty(health.Snapshot(false).Traces); Assert.Empty(health.Snapshot(false).Metrics);
    }

    [Fact]
    public async Task OwnedProcessesAreIdentifiedAndStaleOrForeignTargetsAreRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var owned = Process.Start(new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60" }, UseShellExecute = false, CreateNoWindow = true })!;
        using var foreign = Process.Start(new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60" }, UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            var root = new OwnedProcessRoot(owned.Id, owned.StartTime.ToUniversalTime().Ticks, "pi", "test-thread");
            var monitor = new ProcessResourceMonitor(() => [root]);
            var first = monitor.Capture();
            Assert.DoesNotContain(first, process => process.ProcessId == foreign.Id);
            var row = Assert.Single(first, process => process.ProcessId == owned.Id);
            Assert.Equal(Environment.ProcessId, row.ParentProcessId); Assert.Equal(1, row.Depth); Assert.Null(row.CpuPercent);
            Assert.NotNull(Assert.Single(monitor.Capture(), process => process.ProcessId == owned.Id).CpuPercent);
            Assert.False(monitor.Terminate(new(Environment.ProcessId, 0)).Succeeded);
            Assert.False(monitor.Terminate(new(owned.Id, root.StartedUtcTicks + 1)).Succeeded);
            Assert.False(monitor.Terminate(new(foreign.Id, foreign.StartTime.ToUniversalTime().Ticks)).Succeeded);
            Assert.False(owned.HasExited); Assert.False(foreign.HasExited);
            Assert.True(monitor.Terminate(new(owned.Id, root.StartedUtcTicks)).Succeeded);
            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(foreign.HasExited);
            Assert.False(monitor.Terminate(new(owned.Id, root.StartedUtcTicks)).Succeeded);
        }
        finally { if (!owned.HasExited) owned.Kill(); if (!foreign.HasExited) foreign.Kill(); }
    }

    [Theory]
    [InlineData(200, "{}", "Exported 1 spans")]
    [InlineData(200, "{\"partialSuccess\":{\"rejectedSpans\":\"1\",\"errorMessage\":\"private collector detail\"}}", "partially rejected")]
    [InlineData(503, "private collector detail", "HTTP 503")]
    [InlineData(200, "not-json private collector detail", "JsonReaderException")]
    public async Task OtlpExportUsesBoundedMetadataOnlyAndReportsDelivery(int status, string response, string expected)
    {
        using var directory = new HostTestDirectory();
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/"); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var health = new RuntimeHealthService(directory.Path, () => []);
        health.Save(new(TracingEnabled: true, OtlpEndpoint: $"http://localhost:{port}"));
        using (health.Begin("ReadThreadHistory")) { }
        var send = health.FlushExportAsync(timeout.Token);
        var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
        Assert.Equal("/v1/traces", context.Request.Url!.AbsolutePath);
        Assert.Equal("application/json", context.Request.ContentType);
        using var document = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: timeout.Token);
        var span = document.RootElement.GetProperty("resourceSpans")[0].GetProperty("scopeSpans")[0].GetProperty("spans")[0];
        Assert.Equal("ReadThreadHistory", span.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.String, span.GetProperty("startTimeUnixNano").ValueKind);
        Assert.Equal(32, span.GetProperty("traceId").GetString()!.Length);
        var bytes = Encoding.UTF8.GetBytes(response);
        context.Response.StatusCode = status; context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, timeout.Token); context.Response.Close();
        await send;
        Assert.Contains(expected, health.Snapshot(false).ExportStatus);
        Assert.DoesNotContain("private collector detail", health.Snapshot(false).ExportStatus);
    }

    [Fact]
    public void NativePowerReadingHasAValidTimestampAndPortableUnknowns()
    {
        var before = DateTimeOffset.UtcNow;
        var power = WindowsPowerState.Read();
        Assert.InRange(power.CapturedUtc, before, DateTimeOffset.UtcNow);
        if (!OperatingSystem.IsWindows()) { Assert.Null(power.Locked); Assert.Null(power.OnBattery); Assert.Null(power.LowPower); }
    }

    [Fact]
    public async Task MetricsExportIsSeparateFromTraceSamplingAndUsesCurrentCollectionGauges()
    {
        using var directory = new HostTestDirectory();
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://localhost:{port}/"); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var health = new RuntimeHealthService(directory.Path, () => []);
        health.Save(new(OtlpMetricsEndpoint: $"http://localhost:{port}"));
        using (health.Begin("Operation")) { }
        var send = health.FlushMetricsAsync(timeout.Token);
        var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
        Assert.Equal("/v1/metrics", context.Request.Url!.AbsolutePath);
        using var document = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: timeout.Token);
        var metrics = document.RootElement.GetProperty("resourceMetrics")[0].GetProperty("scopeMetrics")[0].GetProperty("metrics");
        Assert.Equal(4, metrics.GetArrayLength());
        Assert.Equal(1, metrics[0].GetProperty("gauge").GetProperty("dataPoints")[0].GetProperty("asDouble").GetDouble());
        context.Response.StatusCode = 200; context.Response.ContentLength64 = 0; context.Response.Close(); await send;
        Assert.Contains("Exported metrics for 1 operations", health.Snapshot(false).ExportStatus);
        Assert.Empty(health.Snapshot(false).Traces);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollectorThatSendsHeadersThenStallsCannotBlockTheMonitor(bool metrics)
    {
        using var directory = new HostTestDirectory();
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://localhost:{port}/"); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var health = new RuntimeHealthService(directory.Path, () => []);
        var endpoint = $"http://localhost:{port}";
        health.Save(new(TracingEnabled: true, OtlpEndpoint: metrics ? null : endpoint, OtlpMetricsEndpoint: metrics ? endpoint : null));
        using (health.Begin("Operation")) { }
        var send = metrics ? health.FlushMetricsAsync(timeout.Token) : health.FlushExportAsync(timeout.Token);
        var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
        context.Response.SendChunked = true;
        await context.Response.OutputStream.WriteAsync(new byte[] { (byte)'{' }, timeout.Token);
        await context.Response.OutputStream.FlushAsync(timeout.Token);
        // Keep the body open: the export's deadline must cover reads after
        // ResponseHeadersRead, independently of HttpClient's header timeout.
        await send.WaitAsync(TimeSpan.FromSeconds(8));
        context.Response.Abort();
        Assert.Contains("delivery failed", health.Snapshot(false).ExportStatus);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
