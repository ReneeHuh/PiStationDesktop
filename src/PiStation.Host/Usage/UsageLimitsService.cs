using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiStation.Protocol.Models;

namespace PiStation.Host.Usage;

internal sealed class UsageLimitsService : IAsyncDisposable
{
    private readonly object _state = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _settingsPath;
    private readonly string _feedsPath;
    private readonly Func<(bool Allowed, string Reason)> _background;
    private readonly TimeProvider _clock;
    private readonly Task _polling;
    private ProtectedQuotaSettings _settings = new(0, []);
    private string? _settingsError;
    private long _refreshGeneration;
    private Dictionary<string, UsageLimitSource> _sources = new(StringComparer.Ordinal);
    internal UsageLimitsService(string root, Func<(bool Allowed, string Reason)> background, HttpClient? http = null, TimeProvider? clock = null)
    {
        _settingsPath = Path.Combine(root, "usage-limit-sources.protected");
        _feedsPath = Path.Combine(root, "quota-feeds");
        _background = background; _clock = clock ?? TimeProvider.System;
        _http = http ?? new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }); _ownsHttp = http is null;
        try
        {
            if (File.Exists(_settingsPath))
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                if (new FileInfo(_settingsPath).Length > 128 * 1024) throw new JsonException();
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(_settingsPath), null, DataProtectionScope.CurrentUser);
                try
                {
                    var settings = JsonSerializer.Deserialize(bytes, QuotaStorageJsonContext.Default.ProtectedQuotaSettings) ?? throw new JsonException();
                    if (settings.Revision < 0 || settings.Sources is null || settings.Sources.Count > 8 ||
                        settings.Sources.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != settings.Sources.Count) throw new JsonException();
                    foreach (var s in settings.Sources)
                    {
                        if (s is null || s.Id is null || string.IsNullOrEmpty(s.ManagementKey)) throw new JsonException();
                        Validate(new(settings.Revision, s.Id, s.Label, s.BaseUrl, s.Enabled, s.ManagementKey));
                    }
                    _settings = settings;
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or PlatformNotSupportedException or NullReferenceException)
        { _settingsError = "Protected quota settings could not be opened on this host. Restore access to the original Windows user profile or repair the settings file."; }
        _polling = PollAsync();
    }

    internal UsageLimitsDashboard Snapshot(bool includeSettings = true)
    {
        lock (_state)
        {
            var now = _clock.GetUtcNow();
            var sources = _sources.Values.Where(s => s.Kind == "piExtension").ToList();
            if (sources.Count == 0) sources.Add(new("pi", "piExtension", "Pi subscription limits", null, [],
                "Unsupported until a Pi extension publishes quota data."));
            foreach (var configuration in _settings.Sources)
            {
                sources.Add(!configuration.Enabled ? new(configuration.Id, "cliproxy", configuration.Label, null, [], "Monitoring disabled.") :
                    _sources.GetValueOrDefault(configuration.Id) ?? new(configuration.Id, "cliproxy", configuration.Label, null, [], "Not checked yet."));
            }
            return new(new(_settings.Revision, includeSettings ? _settings.Sources.Select(s => new UsageLimitSourceConfiguration(s.Id, s.Label, s.BaseUrl, s.Enabled, s.ManagementKey.Length > 0)).ToArray() : [], _settingsError),
                sources.Select(s => s with { Stale = s.Stale || s.CheckedAt is { } at && now - at > TimeSpan.FromMinutes(5) ||
                    s.Accounts.Any(a => now - a.CheckedAt > TimeSpan.FromMinutes(5) || a.Unavailable == "probeFailed") }).ToArray(), now, _background().Reason);
        }
    }

    internal UsageLimitsDashboard Save(SaveUsageLimitSourceRequest request)
    {
        Validate(request);
        lock (_state)
        {
            CheckRevision(request.Revision);
            var old = _settings.Sources.FirstOrDefault(s => s.Id == request.Id);
            if (request.Id is not null && old is null) throw new ArgumentException("The quota source no longer exists. Refresh before saving.");
            var origin = Origin(request.BaseUrl);
            if (old is not null && old.BaseUrl != origin && request.ManagementKey is null)
                throw new ArgumentException("Enter a new management key when changing the hub address.");
            var key = request.ManagementKey ?? old?.ManagementKey ?? "";
            if (key.Length == 0) throw new ArgumentException("Enter the hub management key.");
            var id = old?.Id ?? Guid.NewGuid().ToString("N");
            var next = _settings.Sources.Where(s => s.Id != id).Append(new ProtectedQuotaSource(id, request.Label.Trim(), origin, request.Enabled, key)).ToArray();
            if (next.Length > 8) throw new ArgumentException("At most eight quota hubs can be monitored.");
            Persist(new(checked(_settings.Revision + 1), next));
            // Address/credential changes must not inherit another account's bars.
            if (old is null || old.BaseUrl != origin || old.ManagementKey != key || !request.Enabled) _sources.Remove(id);
            else if (_sources.TryGetValue(id, out var source)) _sources[id] = source with { Label = request.Label.Trim() };
            return Snapshot();
        }
    }
    internal UsageLimitsDashboard Remove(RemoveUsageLimitSourceRequest request)
    {
        lock (_state)
        {
            CheckRevision(request.Revision);
            if (!_settings.Sources.Any(s => s.Id == request.Id)) throw new ArgumentException("The quota source no longer exists. Refresh before saving.");
            Persist(new(checked(_settings.Revision + 1), _settings.Sources.Where(s => s.Id != request.Id).ToArray()));
            _sources.Remove(request.Id);
            return Snapshot();
        }
    }
    private void CheckRevision(long revision)
    {
        if (_settingsError is not null) throw new InvalidOperationException(_settingsError);
        if (revision != _settings.Revision) throw new InvalidOperationException("Quota settings changed. Refresh before saving again.");
    }
    private void Persist(ProtectedQuotaSettings next)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Quota management keys require Windows user-profile encryption on this host.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, QuotaStorageJsonContext.Default.ProtectedQuotaSettings);
        var temporary = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllBytes(temporary, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
            File.Move(temporary, _settingsPath, overwrite: true);
            _settings = next;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        { throw new InvalidOperationException("Could not save protected quota settings on this host."); }
        finally { CryptographicOperations.ZeroMemory(bytes); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal async Task<UsageLimitsDashboard> RefreshAsync(CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        token = linked.Token;
        long generation;
        lock (_state) generation = _refreshGeneration;
        await _refresh.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ProtectedQuotaSettings settings;
            Dictionary<string, UsageLimitSource> previous;
            lock (_state)
            {
                if (_refreshGeneration != generation) return Snapshot();
                settings = _settings; previous = new(_sources, StringComparer.Ordinal);
            }
            var now = _clock.GetUtcNow();
            var next = await ReadFeedsAsync(previous, now, token).ConfigureAwait(false);
            using var concurrency = new SemaphoreSlim(4, 4);
            var hubs = await Task.WhenAll(settings.Sources.Where(s => s.Enabled).Select(async s =>
            {
                await concurrency.WaitAsync(token).ConfigureAwait(false);
                try { return await ProbeAsync(s, previous.GetValueOrDefault(s.Id), now, token).ConfigureAwait(false); }
                finally { concurrency.Release(); }
            })).ConfigureAwait(false);
            foreach (var hub in hubs) next[hub.Id] = hub;
            lock (_state)
            {
                // A completed old probe can never resurrect removed/changed settings.
                if (_settings.Revision == settings.Revision) { _sources = next; _refreshGeneration++; }
                return Snapshot();
            }
        }
        finally { _refresh.Release(); }
    }
    private async Task<UsageLimitSource> ProbeAsync(ProtectedQuotaSource source, UsageLimitSource? previous, DateTimeOffset now, CancellationToken token)
    {
        string error;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(source.BaseUrl), "/v0/management/quota-scheduler/status"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", source.ManagementKey);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) error = $"Quota refresh failed (HTTP {(int)response.StatusCode}). Check the hub address, scheduler support and management key.";
            else
            {
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                var bytes = await UsageLimitAdapters.ReadBoundedAsync(stream, deadline.Token).ConfigureAwait(false);
                return new(source.Id, "cliproxy", source.Label, now, UsageLimitAdapters.CliProxy(bytes, now));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { error = "Quota refresh timed out after 10 seconds."; }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { error = "Quota refresh failed: the hub was unreachable or returned an invalid or oversized response."; }
        return previous is null ? new(source.Id, "cliproxy", source.Label, null, [], error, true) : previous with { Error = error, Stale = true };
    }
    private async Task<Dictionary<string, UsageLimitSource>> ReadFeedsAsync(Dictionary<string, UsageLimitSource> previous, DateTimeOffset now, CancellationToken token)
    {
        var result = new Dictionary<string, UsageLimitSource>(StringComparer.Ordinal);
        try
        {
            if (!Directory.Exists(_feedsPath)) return result;
            var files = Directory.EnumerateFiles(_feedsPath, "*.json", SearchOption.TopDirectoryOnly).Take(33).ToArray();
            if (files.Length > 32) throw new JsonException();
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16384, FileOptions.Asynchronous);
                var source = UsageLimitAdapters.PiFeed(await UsageLimitAdapters.ReadBoundedAsync(stream, token).ConfigureAwait(false), now);
                if (!result.TryAdd(source.Id, source)) throw new JsonException();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // No partial authoritative snapshot: retain last successful feed readings on malformed writes.
            result = previous.Values.Where(s => s.Kind == "piExtension").ToDictionary(s => s.Id, s => s with { Stale = true, Error = "Pi quota feed is unreadable or invalid; retained the last successful readings." }, StringComparer.Ordinal);
            if (result.Count == 0) result["pi"] = new("pi", "piExtension", "Pi subscription limits", null, [], "Pi quota feed is unreadable or invalid.", true);
        }
        return result;
    }
    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        var last = DateTimeOffset.MinValue;
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                if (!_background().Allowed || _clock.GetUtcNow() - last < TimeSpan.FromMinutes(1)) continue;
                await RefreshAsync(_shutdown.Token).ConfigureAwait(false);
                last = _clock.GetUtcNow();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }
    private static void Validate(SaveUsageLimitSourceRequest request)
    {
        if (request is null || request.Label is null || string.IsNullOrWhiteSpace(request.Label) || request.Label.Length > 128 || request.Label.Any(char.IsControl) ||
            request.Id is not null && (!Guid.TryParseExact(request.Id, "N", out _)) || request.ManagementKey is { } key && (key.Length is 0 or > 8192 || key.Any(char.IsWhiteSpace) || key.Any(char.IsControl)))
            throw new ArgumentException("Enter a source label and a valid management key.");
        _ = Origin(request.BaseUrl);
    }
    private static string Origin(string value)
    {
        if (value is null || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/" ||
            uri.Scheme != "https" && !(uri.Scheme == "http" && (uri.Host == "localhost" || IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address))))
            throw new ArgumentException("Use an HTTPS hub origin, or HTTP on localhost/loopback, without a path, query or credentials.");
        return uri.GetLeftPart(UriPartial.Authority) + "/";
    }
    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _polling.ConfigureAwait(false);
        await _refresh.WaitAsync().ConfigureAwait(false);
        _refresh.Release();
        if (_ownsHttp) _http.Dispose();
        _shutdown.Dispose();
    }
}

internal sealed record ProtectedQuotaSource(string Id, string Label, string BaseUrl, bool Enabled, string ManagementKey);
internal sealed record ProtectedQuotaSettings(long Revision, IReadOnlyList<ProtectedQuotaSource> Sources);
[JsonSerializable(typeof(ProtectedQuotaSettings))]
internal sealed partial class QuotaStorageJsonContext : JsonSerializerContext;
