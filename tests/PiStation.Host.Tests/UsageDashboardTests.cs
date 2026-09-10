using System.Net;
using System.Text.Json;
using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Host.Usage;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Tests;

public sealed class UsageDashboardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly UsageQuery Query = new(Now.AddDays(-1), Now.AddDays(1));
    private const string Prices = """{"m":{"litellm_provider":"p","mode":"chat","input_cost_per_token":0.01,"output_cost_per_token":0.02,"cache_read_input_token_cost":0.001,"cache_creation_input_token_cost":0.015}}""";
    private static HostOptions Options(HostTestDirectory directory) => directory.CreateOptions() with
    { LaunchConfiguration = new(EnvironmentVariables: new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = directory.CreateDirectory("isolated-agent") }) };
    private static string Message(int index = 0, string provider = "p", string model = "m", decimal? cost = null) => JsonSerializer.Serialize(new
    {
        role = "assistant", provider, model, timestamp = Now.AddSeconds(index).ToUnixTimeMilliseconds(),
        content = new[] { new { type = "text", text = "answer " + index } },
        usage = new { input = 100, output = 20, cacheRead = 30, cacheWrite = 10, totalTokens = 160, reasoning = 5, cost = cost is null ? null : new { total = cost } },
    });
    private static string Header(string id) => JsonSerializer.Serialize(new { type = "session", version = 3, id, cwd = "fixture" }) + "\n";
    private static string Entry(int index = 0, string provider = "p", string model = "m", decimal? cost = null) =>
        "{\"type\":\"message\",\"id\":\"e" + index + "\",\"message\":" + Message(index, provider, model, cost) + "}\n";
    private static async Task Write(string path, string content) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, content); }
    private sealed class PricingHandler(string content) : HttpMessageHandler
    {
        public string Content { get; set; } = content;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(UsagePricing.PriceUrl, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Content) });
        }
    }
    [Fact]
    public async Task CopiesForksAndLiveLedgerAreCountedOnceWithPerMessageProviderAndPricing()
    {
        using var directory = new HostTestDirectory(); var options = Options(directory);
        await using var db = new HostDatabase(options); await db.InitializeAsync();
        var projects = new ProjectService(db); var project = await projects.AddAsync(new(directory.CreateDirectory("project")));
        var thread = await projects.CreateThreadAsync(new(project.ProjectId));
        await Write(Path.Combine(options.SessionRoot, "a.jsonl"), Header(thread.PiSessionId) + Entry() + Entry(1, "other", "unknown"));
        await Write(Path.Combine(options.SessionRoot, "fork.jsonl"), Header("fork") + Entry());
        using var message = JsonDocument.Parse(Message());
        await db.SaveUsageRecordAsync(thread.ThreadId, UsageRecord.Read(message.RootElement, thread.PiSessionId, "live")!, default);
        await db.AppendUsageAsync(thread.ThreadId, "p", "m", 100, 20, 40, 160, 99);
        using var http = new HttpClient(new PricingHandler(Prices)); using var usage = new UsageService(options, db, http);
        var result = await usage.QueryAsync(Query, refreshPricing: true);
        Assert.Equal(320, result.Totals.TotalTokens); Assert.Equal(2, result.Totals.Records);
        Assert.Equal(2, result.Scan.DuplicateRecords); Assert.Equal(1, result.Totals.UnpricedRecords);
        Assert.Equal(1.58m, result.Totals.KnownCostUsd); Assert.Equal(.27m, result.Totals.CacheSavingsUsd);
        Assert.Equal(2, result.Breakdown.Count); Assert.Contains(result.Scan.Warnings, w => w.Contains("superseded"));
        var filtered = await usage.QueryAsync(Query with { Provider = "p" });
        Assert.Equal(160, filtered.Totals.TotalTokens); Assert.Equal(2, filtered.Scan.CachedFiles);
    }
    [Fact]
    public async Task PersistedCacheReusesUnchangedFilesAndRescanReplacesRewrittenOrDeletedHistory()
    {
        using var directory = new HostTestDirectory(); var options = Options(directory);
        await using var db = new HostDatabase(options); await db.InitializeAsync();
        var path = Path.Combine(options.SessionRoot, "a.jsonl"); await Write(path, Header("a") + Entry());
        using (var first = new UsageService(options, db)) Assert.Equal(160, (await first.QueryAsync(Query)).Totals.TotalTokens);
        using var second = new UsageService(options, db);
        Assert.Equal(1, (await second.QueryAsync(Query)).Scan.CachedFiles);
        var stamp = File.GetLastWriteTimeUtc(path);
        await Write(path, Header("a") + Entry(1)); File.SetLastWriteTimeUtc(path, stamp);
        var rescan = await second.QueryAsync(Query, rescan: true); Assert.Equal(0, rescan.Scan.CachedFiles); Assert.Equal(160, rescan.Totals.TotalTokens);
        File.Delete(path); Assert.Equal(0, (await second.QueryAsync(Query)).Totals.Records);
    }
    [Fact]
    public async Task PartialLastLineIsRetriedAndMalformedCompleteLinesDoNotHideGoodMessages()
    {
        using var directory = new HostTestDirectory(); var options = Options(directory);
        await using var db = new HostDatabase(options); await db.InitializeAsync();
        var path = Path.Combine(options.SessionRoot, "a.jsonl");
        await Write(path, Header("a") + "bad json\n" + Entry(4).Replace("\"reasoning\":5", "\"reasoning\":\"bad\"", StringComparison.Ordinal) + Entry() + Entry(1).TrimEnd('\n'));
        using var usage = new UsageService(options, db);
        var first = await usage.QueryAsync(Query); Assert.Equal(1, first.Totals.Records); Assert.Equal(3, first.Scan.MalformedLines);
        await File.AppendAllTextAsync(path, "\n");
        var second = await usage.QueryAsync(Query); Assert.Equal(2, second.Totals.Records); Assert.Equal(2, second.Scan.MalformedLines);
    }
    [Fact]
    public async Task ChildHistoryResumeCopiesAndRepeatedActivitySummariesAreNotAddedTwice()
    {
        using var directory = new HostTestDirectory(); var options = Options(directory);
        await using var db = new HostDatabase(options); await db.InitializeAsync();
        var projects = new ProjectService(db); var project = await projects.AddAsync(new(directory.CreateDirectory("project")));
        var thread = await projects.CreateThreadAsync(new(project.ProjectId));
        var control = Guid.NewGuid().ToString("N");
        await Write(Path.Combine(options.CanonicalDataRoot, "agents", thread.ThreadId.Value, control, "child.jsonl"), Header("child") + Entry());
        await Write(Path.Combine(options.CanonicalDataRoot, "agents", thread.ThreadId.Value, "resumed", "child.jsonl"), Header("child") + Entry() + Entry(1));
        var child = Child("child", control: control, session: "child");
        var external = Child("external", source: "activity", control: Guid.NewGuid().ToString("N"));
        foreach (var item in new[] { child, child, external, external, external with { ActivityId = "repeated-report", UpdatedUtc = Now.AddSeconds(1) }, Child("unknown"), Child("workflow") with { Kind = AgentActivityKind.Workflow } })
            await db.AppendThreadAgentEventAsync(thread.ThreadId, null, 0, new(item));
        using var usage = new UsageService(options, db); var result = await usage.QueryAsync(Query);
        Assert.Equal(3, result.Totals.Records); Assert.Equal(3, result.Totals.ChildRecords);
        Assert.Equal(480, result.Totals.TotalTokens); Assert.Equal(1, result.Scan.UnreconciledChildren);
    }
    private static AgentActivityProjection Child(string id, string? control = null, string? session = null, string? source = null) => new(id,
        null, null, AgentActivityKind.Agent, AgentActivityState.Completed, id, "", "", Now, Now, Now, 0,
        new(100, 20, 30, 10, 5, 160), "p/m", null, null, null, null, null, false,
        ControlId: control, UsageSessionId: session, UsageSource: source);
    [Fact]
    public void NewChildUsageWithMissingCostDoesNotReuseAnOlderCumulativePrice()
    {
        var previous = Child("tool") with { UsageCost = .12m };
        using var result = JsonDocument.Parse("""{"details":{"mode":"single","results":[{"usageSource":"activity","usage":{"input":200,"output":20,"cacheRead":0,"cacheWrite":0,"cost":null}}]}}""");
        var updated = Assert.Single(Threads.PiAgentActivityProjector.Update("tool", result.RootElement, [previous], true, false, null, Now.AddSeconds(1)));
        Assert.Equal(200, updated.Usage!.InputTokens); Assert.Null(updated.UsageCost);
    }
    [Fact]
    public async Task LegacyAggregatesAreReconciledBySavedFileEvenWhenSessionHeaderUsesAnotherIdentity()
    {
        using var directory = new HostTestDirectory(); var options = Options(directory);
        await using var db = new HostDatabase(options); await db.InitializeAsync();
        var projects = new ProjectService(db); var project = await projects.AddAsync(new(directory.CreateDirectory("project")));
        var thread = await projects.CreateThreadAsync(new(project.ProjectId));
        var file = Path.Combine(options.SessionRoot, "saved.jsonl"); await Write(file, Header("different-header-id") + Entry());
        await db.UpdateThreadSessionFileAsync(thread.ThreadId, file);
        await db.AppendUsageAsync(thread.ThreadId, "p", "m", 100, 20, 40, 160, .1m);
        using var usage = new UsageService(options, db);
        var report = await usage.QueryAsync(Query);
        Assert.Equal(160, report.Totals.TotalTokens); Assert.Equal(0, report.Scan.LegacyRecords);
    }
    [Fact]
    public async Task ReportedCostWinsAndFailedRefreshRetainsCachedPricesAcrossRestart()
    {
        using var directory = new HostTestDirectory(); var options = Options(directory);
        await using var db = new HostDatabase(options); await db.InitializeAsync();
        await Write(Path.Combine(options.SessionRoot, "a.jsonl"), Header("a") + Entry(cost: 9) + Entry(1));
        var handler = new PricingHandler(Prices); using var http = new HttpClient(handler);
        using (var usage = new UsageService(options, db, http))
        {
            var result = await usage.QueryAsync(Query, refreshPricing: true); Assert.Equal(10.58m, result.Totals.KnownCostUsd); Assert.Equal(1, result.Totals.ReportedCostRecords);
            handler.Content = "broken"; var failed = await usage.QueryAsync(Query, refreshPricing: true);
            Assert.True(failed.Pricing.Stale); Assert.Equal(10.58m, failed.Totals.KnownCostUsd);
        }
        using var restarted = new UsageService(options, db, http); Assert.Equal(10.58m, (await restarted.QueryAsync(Query)).Totals.KnownCostUsd);
    }
    [Fact]
    public void MissingCacheRatesUseInputPriceAndProvidersDoNotBorrowAnotherProvidersRate()
    {
        var table = UsagePricing.Parse(System.Text.Encoding.UTF8.GetBytes("""{"m":{"litellm_provider":"p","input_cost_per_token":1,"output_cost_per_token":2}}"""));
        Assert.Equal(new UsageRate(1, 2, 1, 1), table["p/m"]); Assert.False(table.ContainsKey("other/m"));
        var conflicting = UsagePricing.Parse(System.Text.Encoding.UTF8.GetBytes("""{"m":{"litellm_provider":"p","input_cost_per_token":1,"output_cost_per_token":2},"p/m":{"input_cost_per_token":3,"output_cost_per_token":4}}"""));
        Assert.False(conflicting.ContainsKey("p/m"));
    }
    [Fact]
    public async Task CustomDirectoryIsReadAndTemporaryHostsIgnoreExternalHistoryAndDoNotPersistCaches()
    {
        using var directory = new HostTestDirectory(); var options = Options(directory) with { TemporaryHistory = true };
        await using var db = new HostDatabase(options); await db.InitializeAsync();
        var external = directory.CreateDirectory("external"); await Write(Path.Combine(external, "a.jsonl"), Header("a") + Entry());
        using var usage = new UsageService(options, db); var result = await usage.QueryAsync(Query with { HistoryDirectory = external });
        Assert.Empty(result.Breakdown); Assert.False(File.Exists(Path.Combine(options.CanonicalDataRoot, "usage-scan-v1.json")));
        var normalOptions = Options(directory) with { ApplicationDataRoot = directory.CreateDirectory("normal-data") };
        await using var normalDb = new HostDatabase(normalOptions); await normalDb.InitializeAsync();
        using var normal = new UsageService(normalOptions, normalDb); Assert.Equal(1, (await normal.QueryAsync(Query with { HistoryDirectory = external })).Totals.Records);
    }
    [Fact]
    public async Task CancellationLeavesDurableScanCacheIntact()
    {
        using var directory = new HostTestDirectory(); var options = Options(directory);
        await using var db = new HostDatabase(options); await db.InitializeAsync(); using var usage = new UsageService(options, db);
        await usage.QueryAsync(Query); var path = Path.Combine(options.CanonicalDataRoot, "usage-scan-v1.json"); var before = await File.ReadAllTextAsync(path);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => usage.QueryAsync(Query, rescan: true, token: canceled.Token));
        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }
    [Fact]
    public async Task CorruptScanCacheFallsBackToFreshScan()
    {
        using var directory = new HostTestDirectory(); var options = Options(directory);
        await using var db = new HostDatabase(options); await db.InitializeAsync();
        await Write(Path.Combine(options.CanonicalDataRoot, "usage-scan-v1.json"), "not-json");
        await Write(Path.Combine(options.SessionRoot, "a.jsonl"), Header("a") + Entry());
        using var usage = new UsageService(options, db); Assert.Equal(1, (await usage.QueryAsync(Query)).Totals.Records);
    }
    [Fact]
    public void HourlyBucketsRetainRepeatedDstHoursAndEndBoundaryIsExclusive()
    {
        var start = new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);
        using var pricing = new UsagePricing(null);
        var records = Enumerable.Range(0, 3).Select(i => new UsageRecord(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "a", start.AddHours(i), "p", "m", 1, 0, 0, 0, 0, 1, null));
        var query = new UsageQuery(start, start.AddHours(2), "America/New_York", Hourly: true);
        var result = UsageService.Aggregate(query, records, new(Now, 0, 0, 0, 0, 0, 0, 0, [], []), pricing);
        Assert.Equal(2, result.Totals.Records); Assert.Equal(2, result.Buckets.Count);
        Assert.Contains(result.Buckets, b => b.Label.EndsWith("-04:00", StringComparison.Ordinal)); Assert.Contains(result.Buckets, b => b.Label.EndsWith("-05:00", StringComparison.Ordinal));
    }
    [Fact]
    public void InvalidAndUnboundedQueriesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => UsageService.Validate(Query with { ToUtc = Query.FromUtc }));
        Assert.Throws<ArgumentException>(() => UsageService.Validate(Query with { ToUtc = Query.FromUtc.AddDays(367) }));
        Assert.Throws<ArgumentException>(() => UsageService.Validate(Query with { HistoryDirectory = "relative" }));
        Assert.Throws<TimeZoneNotFoundException>(() => UsageService.Validate(Query with { TimeZoneId = "invalid-zone" }));
    }
    [Fact]
    public void HistoryDiscoveryHonorsConfiguredAndExplicitlyClearedAgentDirectories()
    {
        Assert.Equal("inherited", UsageService.ResolveAgentDirectory(null, "inherited", "home"));
        Assert.Equal("configured", UsageService.ResolveAgentDirectory(new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = "configured" }, "inherited", "home"));
        Assert.Equal(Path.Combine("home", ".pi", "agent"), UsageService.ResolveAgentDirectory(new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = null }, "inherited", "home"));
    }
}
