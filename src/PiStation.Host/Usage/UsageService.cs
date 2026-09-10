using System.Globalization;
using System.Text;
using System.Text.Json;
using PiStation.Host.Persistence;
using PiStation.Protocol.Models;

namespace PiStation.Host.Usage;

internal sealed class UsageService : IDisposable
{
    private readonly HostOptions _options;
    private readonly HostDatabase _database;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly UsagePricing _pricing;
    private Dictionary<string, UsageFileCache> _files = new(PathComparer);
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private string CachePath => Path.Combine(_options.CanonicalDataRoot, "usage-scan-v1.json");
    internal UsageService(HostOptions options, HostDatabase database, HttpClient? http = null)
    {
        _options = options; _database = database;
        _pricing = new(options.TemporaryHistory ? null : Path.Combine(options.CanonicalDataRoot, "usage-pricing-v1.json"), http);
        try
        {
            if (!options.TemporaryHistory && File.Exists(CachePath) && new FileInfo(CachePath).Length <= 128 * 1024 * 1024 &&
                JsonSerializer.Deserialize(File.ReadAllBytes(CachePath), UsageJsonContext.Default.UsageDiskCache) is { Version: 1, Files: not null } cache &&
                cache.Files.Count <= 10000 && cache.Files.Values.All(f => f is not null && f.Records is not null && f.Records.All(r => r is not null && r.Key is not null)))
                _files = new(cache.Files, PathComparer);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { }
    }
    public async Task<UsageDashboard> QueryAsync(UsageQuery query, bool rescan = false, bool refreshPricing = false, CancellationToken token = default)
    {
        Validate(query);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        token = deadline.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (refreshPricing) await _pricing.RefreshAsync(token).ConfigureAwait(false);
            var roots = Roots(query.HistoryDirectory).Distinct(PathComparer).ToArray();
            var next = new Dictionary<string, UsageFileCache>(PathComparer);
            var warnings = new List<string>(); var skipped = 0; var cached = 0; var malformed = 0;
            var records = new Dictionary<string, UsageRecord>(StringComparer.Ordinal); var duplicate = 0;
            var sessionIds = new HashSet<string>(StringComparer.Ordinal);
            var childControls = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) { warnings.Add("History directory is unavailable: " + root); continue; }
                try
                {
                    foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true,
                        MaxRecursionDepth = 8, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System }))
                    {
                        token.ThrowIfCancellationRequested();
                        if (next.ContainsKey(path)) continue;
                        if (next.Count + skipped >= 10000 || records.Count >= 500000) { warnings.Add("Scan limit reached (10,000 files / 500,000 messages). Narrow the history directory."); skipped++; break; }
                        try
                        {
                            var info = new FileInfo(path); var child = IsUnder(path, Path.Combine(_options.CanonicalDataRoot, "agents"));
                            if (info.Length > 128 * 1024 * 1024) { skipped++; continue; }
                            UsageFileCache file;
                            if (!rescan && _files.TryGetValue(path, out var previous) && previous.Length == info.Length && previous.ModifiedTicks == info.LastWriteTimeUtc.Ticks)
                            { file = previous; cached++; }
                            else file = await ReadFileAsync(path, info.Length, info.LastWriteTimeUtc.Ticks, child, token).ConfigureAwait(false);
                            next[path] = file; malformed += file.Malformed;
                            if (file.SessionId.Length > 0 && file.Records.Length > 0) sessionIds.Add(file.SessionId);
                            if (child && file.Records.Length > 0) childControls.Add(Path.GetFileName(Path.GetDirectoryName(path))!);
                            foreach (var record in file.Records) Add(record);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or DecoderFallbackException)
                        { skipped++; }
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { warnings.Add("Some history files could not be enumerated: " + root); skipped++; }
            }
            var stored = await _database.ReadUsageRecordsAsync(sessionIds, next.Where(pair => pair.Value.Records.Length > 0).Select(pair => pair.Key).ToHashSet(PathComparer), token).ConfigureAwait(false);
            foreach (var record in stored.Records) Add(record);
            var unresolved = 0;
            foreach (var (thread, activity) in stored.Children.GroupBy(item => item.Activity.ControlId ?? item.Thread + ":" + item.Activity.ActivityId)
                .Select(group => group.MaxBy(item => item.Activity.UpdatedUtc)))
            {
                if (activity.UsageSessionId is { Length: > 0 } session && sessionIds.Contains(session) ||
                    activity.ControlId is { } control && childControls.Contains(control)) continue;
                // Extensions must identify their session, or explicitly declare own usage with no saved transcript.
                // Otherwise an activity summary could overlap arbitrary imported child history.
                if (activity.UsageSource != "activity") { unresolved++; continue; }
                var usage = activity.Usage!;
                var model = activity.Model ?? "unknown"; var provider = activity.UsageProvider ?? "unknown";
                var slash = model.IndexOf('/');
                if (slash > 0 && provider == "unknown") { provider = model[..slash]; model = model[(slash + 1)..]; }
                else if (model.StartsWith(provider + "/", StringComparison.Ordinal)) model = model[(provider.Length + 1)..];
                Add(new("activity:" + thread + ":" + activity.ActivityId, activity.UsageSessionId ?? "activity:" + thread,
                    activity.CompletedUtc ?? activity.UpdatedUtc, provider, model, usage.InputTokens, usage.OutputTokens,
                    usage.CacheReadTokens, usage.CacheWriteTokens, usage.ReasoningTokens ?? 0, usage.TotalTokens, activity.UsageCost, Child: true));
            }
            if (stored.Suppressed > 0) warnings.Add($"{stored.Suppressed} older aggregate records were superseded by message history. Legacy compaction/cache details may be incomplete.");
            if (unresolved > 0) warnings.Add($"{unresolved} child summaries excluded: their extension has not supplied a reconcilable usage source.");
            var serialized = JsonSerializer.SerializeToUtf8Bytes(new UsageDiskCache(1, next), UsageJsonContext.Default.UsageDiskCache);
            if (!_options.TemporaryHistory && serialized.Length <= 128 * 1024 * 1024)
            {
                try { await AtomicWriteAsync(CachePath, serialized, token).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { warnings.Add("Scan cache could not be saved; results are available for this run."); }
            }
            _files = next;
            var scan = new UsageScanStatus(DateTimeOffset.UtcNow, next.Count, cached, malformed, skipped, duplicate,
                records.Values.Count(r => r.Legacy), unresolved, roots, warnings.Take(20).ToArray());
            return Aggregate(query, records.Values, scan, _pricing);
            void Add(UsageRecord record)
            {
                if (records.TryGetValue(record.Key, out var previous))
                { duplicate++; if (previous.Cost is null && record.Cost is not null) records[record.Key] = previous with { Cost = record.Cost }; }
                else records[record.Key] = record;
            }
        }
        finally { _gate.Release(); }
    }
    private IEnumerable<string> Roots(string? extra)
    {
        yield return _options.SessionRoot;
        yield return Path.Combine(_options.CanonicalDataRoot, "agents");
        if (_options.TemporaryHistory) yield break;
        var agent = ResolveAgentDirectory(_options.LaunchConfiguration.EnvironmentVariables, Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        yield return Path.GetFullPath(Path.Combine(agent, "sessions"));
        if (!string.IsNullOrWhiteSpace(extra)) yield return Path.GetFullPath(extra);
    }
    internal static string ResolveAgentDirectory(IReadOnlyDictionary<string, string?>? variables, string? inherited, string home)
    {
        var configured = variables?.FirstOrDefault(pair => pair.Key.Equals("PI_CODING_AGENT_DIR", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        var value = configured is { Key: not null } ? configured.Value.Value : inherited;
        return string.IsNullOrWhiteSpace(value) ? Path.Combine(home, ".pi", "agent") : value;
    }
    private static bool IsUnder(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    internal static void Validate(UsageQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.FromUtc >= query.ToUtc || query.ToUtc - query.FromUtc > TimeSpan.FromDays(366) || query.Hourly && query.ToUtc - query.FromUtc > TimeSpan.FromDays(7))
            throw new ArgumentException("Choose a date range of up to 366 days (7 days for hourly charts).");
        _ = TimeZoneInfo.FindSystemTimeZoneById(query.TimeZoneId);
        if (query.Provider?.Length > 256 || query.Model?.Length > 256 || query.HistoryDirectory?.Length > 4096 ||
            !string.IsNullOrWhiteSpace(query.HistoryDirectory) && !Path.IsPathFullyQualified(query.HistoryDirectory)) throw new ArgumentException("Enter an absolute history directory and valid model filters.");
    }
    internal static async Task<UsageFileCache> ReadFileAsync(string path, long length, long modified, bool child, CancellationToken token)
    {
        var records = new List<UsageRecord>(); var malformed = 0; var sessionId = ""; var lineNumber = 0;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 32768, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var buffer = new char[16384]; var line = new StringBuilder(); var oversized = false; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] != '\n') { if (line.Length < 4 * 1024 * 1024 && !oversized) line.Append(buffer[index]); else oversized = true; continue; }
                token.ThrowIfCancellationRequested(); lineNumber++;
                if (oversized) malformed++;
                else if (line.Length > 0)
                {
                    try
                    {
                        using var json = JsonDocument.Parse(line.ToString()); var entry = json.RootElement;
                        if (lineNumber == 1)
                        {
                            if (UsageRecord.Text(entry, "type") != "session" || UsageRecord.Text(entry, "id") is not { Length: > 0 } id) throw new InvalidDataException("Invalid Pi session header.");
                            sessionId = id;
                        }
                        else if (UsageRecord.Text(entry, "type") == "message" && entry.TryGetProperty("message", out var message) && UsageRecord.Text(message, "role") == "assistant")
                        {
                            if (UsageRecord.Read(message, sessionId, UsageRecord.Text(entry, "id") ?? lineNumber.ToString(CultureInfo.InvariantCulture), child) is { } record) records.Add(record);
                            else malformed++;
                        }
                    }
                    catch (JsonException) { if (lineNumber == 1) throw; malformed++; }
                }
                line.Clear(); oversized = false;
            }
            if (records.Count > 100000) throw new InvalidDataException("Session message limit exceeded.");
        }
        // Pi appends newline-terminated records. Retry a partially written last record on the next scan.
        if (line.Length > 0 || oversized) malformed++;
        if (sessionId.Length == 0) throw new InvalidDataException("The Pi session header is incomplete.");
        return new(length, modified, sessionId, malformed, records.ToArray());
    }
    internal static UsageDashboard Aggregate(UsageQuery query, IEnumerable<UsageRecord> source, UsageScanStatus scan, UsagePricing pricing)
    {
        var window = source.Where(r => r.Timestamp >= query.FromUtc && r.Timestamp < query.ToUtc).ToArray();
        var filtered = window.Where(r => (string.IsNullOrEmpty(query.Provider) || r.Provider == query.Provider) &&
            (string.IsNullOrEmpty(query.Model) || r.Model == query.Model)).ToArray();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(query.TimeZoneId);
        string Bucket(DateTimeOffset timestamp) => TimeZoneInfo.ConvertTime(timestamp, zone).ToString(query.Hourly ? "yyyy-MM-dd HH':00' zzz" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var buckets = filtered.GroupBy(r => Bucket(r.Timestamp)).ToDictionary(g => g.Key, g => Sum(g, pricing));
        for (var time = query.FromUtc; time < query.ToUtc; time = time.AddHours(1)) buckets.TryAdd(Bucket(time), Sum([], pricing));
        var last = query.ToUtc.AddTicks(-1); buckets.TryAdd(Bucket(last), Sum([], pricing));
        return new(query, Sum(filtered, pricing), buckets.OrderBy(p => query.Hourly ? DateTimeOffset.ParseExact(p.Key, "yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture).UtcTicks :
                DateTime.ParseExact(p.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture).Ticks).Select(p => new UsageBucket(p.Key, p.Value)).ToArray(),
            filtered.GroupBy(r => (r.Provider, r.Model)).Select(g => new UsageModelBreakdown(g.Key.Provider, g.Key.Model, Sum(g, pricing))).OrderByDescending(b => b.Totals.TotalTokens).ToArray(),
            window.Select(r => r.Provider).Distinct().Order().ToArray(), window.Where(r => string.IsNullOrEmpty(query.Provider) || r.Provider == query.Provider).Select(r => r.Model).Distinct().Order().ToArray(), scan, pricing.Status);
    }
    private static UsageTotals Sum(IEnumerable<UsageRecord> records, UsagePricing pricing)
    {
        long input = 0, output = 0, read = 0, write = 0, reasoning = 0, total = 0; decimal cost = 0, savings = 0;
        var count = 0; var unpriced = 0; var reported = 0; var savingsPriced = 0; var children = 0;
        foreach (var r in records)
        {
            input = Add(input, r.Input); output = Add(output, r.Output); read = Add(read, r.CacheRead); write = Add(write, r.CacheWrite); reasoning = Add(reasoning, r.Reasoning); total = Add(total, r.Total); count++; if (r.Child) children++;
            var rate = r.Legacy ? null : pricing.Find(r.Provider, r.Model);
            if (r.Cost is { } amount) { cost += amount; reported++; }
            else if (rate is not null) cost += r.Input * rate.Input + r.Output * rate.Output + r.CacheRead * rate.CacheRead + r.CacheWrite * rate.CacheWrite;
            else unpriced++;
            if (rate is not null) { savings += r.CacheRead * (rate.Input - rate.CacheRead); savingsPriced++; }
        }
        return new(input, output, read, write, reasoning, total, cost, savings, count, unpriced, reported, savingsPriced, children);
    }
    private static long Add(long a, long b) => b > long.MaxValue - a ? long.MaxValue : a + b;
    internal static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() => _pricing.Dispose();
}
