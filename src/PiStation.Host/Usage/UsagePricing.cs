using System.Text.Json;
using PiStation.Protocol.Models;

namespace PiStation.Host.Usage;

internal sealed class UsagePricing : IDisposable
{
    internal const string PriceUrl = "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string? _path;
    private UsagePriceCache _cache = new(default, new(StringComparer.OrdinalIgnoreCase));
    private string _detail = "Refresh pricing to estimate records without reported costs.";
    private bool _failed;
    public UsagePricing(string? path, HttpClient? http = null)
    {
        _path = path; _ownsHttp = http is null; _http = http ?? new HttpClient();
        try
        {
            if (path is not null && File.Exists(path) && new FileInfo(path).Length <= 8 * 1024 * 1024 &&
                JsonSerializer.Deserialize(File.ReadAllBytes(path), UsageJsonContext.Default.UsagePriceCache) is { } saved &&
                saved.Rates is not null && saved.Rates.Values.All(r => r is not null && Valid(r)))
            { _cache = saved with { Rates = new(saved.Rates, StringComparer.OrdinalIgnoreCase) }; _detail = "Cached LiteLLM base API rates (USD)."; }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { }
    }
    private static bool Valid(UsageRate r) => r.Input is >= 0 and <= 100 && r.Output is >= 0 and <= 100 && r.CacheRead is >= 0 and <= 100 && r.CacheWrite is >= 0 and <= 100;
    public UsagePricingStatus Status => new(_cache.UpdatedUtc == default ? null : _cache.UpdatedUtc,
        _failed || DateTimeOffset.UtcNow - _cache.UpdatedUtc > TimeSpan.FromHours(24), _cache.Rates.Count, _detail);
    public async Task RefreshAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await _http.GetAsync(PriceUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new InvalidDataException("Pricing response is too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream(); var buffer = new byte[32768]; int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
            { if (bytes.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("Pricing response is too large."); bytes.Write(buffer, 0, count); }
            var rates = Parse(bytes.ToArray());
            if (rates.Count == 0) throw new InvalidDataException("Pricing response contains no usable model rates.");
            var next = new UsagePriceCache(DateTimeOffset.UtcNow, rates);
            if (_path is not null) await UsageService.AtomicWriteAsync(_path, JsonSerializer.SerializeToUtf8Bytes(next, UsageJsonContext.Default.UsagePriceCache), token).ConfigureAwait(false);
            _cache = next; _failed = false; _detail = "LiteLLM base API rates (USD); reported costs take precedence. Refresh does not change reported costs.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException or UnauthorizedAccessException)
        { _failed = true; _detail = "Pricing refresh failed; retained cached rates. " + (error is OperationCanceledException ? "Request timed out." : error.Message); }
    }
    internal static Dictionary<string, UsageRate> Parse(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a model price table.");
        var candidates = new Dictionary<string, List<UsageRate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var value = property.Value;
            if (Read(value, "input_cost_per_token") is not { } input || Read(value, "output_cost_per_token") is not { } output) continue;
            if (UsageRecord.Text(value, "mode") is { } mode && mode is not ("chat" or "completion")) continue;
            var rate = new UsageRate(input, output, Read(value, "cache_read_input_token_cost") ?? input,
                Read(value, "cache_creation_input_token_cost") ?? input);
            if (!Valid(rate)) continue;
            var key = property.Name.ToLowerInvariant();
            // Keep provider qualification. Conflicting prices never become an arbitrary bare alias.
            Add(key, rate);
            if (UsageRecord.Text(value, "litellm_provider") is { } provider && !key.StartsWith(provider + "/", StringComparison.OrdinalIgnoreCase)) Add(provider + "/" + key, rate);
        }
        return candidates.Where(pair => pair.Value.Distinct().Count() == 1).ToDictionary(pair => pair.Key, pair => pair.Value[0], StringComparer.OrdinalIgnoreCase);
        void Add(string key, UsageRate rate) { if (!candidates.TryGetValue(key, out var values)) candidates[key] = values = []; values.Add(rate); }
    }
    private static decimal? Read(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var number) && number >= 0 ? number : null;
    public UsageRate? Find(string provider, string model)
    {
        var key = model.StartsWith(provider + "/", StringComparison.OrdinalIgnoreCase) ? model : provider + "/" + model;
        return _cache.Rates.GetValueOrDefault(key);
    }
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
