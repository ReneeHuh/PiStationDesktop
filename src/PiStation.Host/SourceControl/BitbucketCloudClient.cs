using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PiStation.Host.SourceControl;

/// <summary>Bounded Bitbucket Cloud transport. Credentials never enter command arguments, URLs or error bodies.</summary>
public sealed class BitbucketCloudClient : IDisposable
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly Func<AuthenticationHeaderValue?> _credentials;
    public BitbucketCloudClient(HttpMessageHandler? handler = null, Func<AuthenticationHeaderValue?>? credentials = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        _credentials = credentials ?? ReadCredentials;
    }
    public bool IsConfigured => _credentials() is not null;
    public static bool HasEnvironmentCredentials => ReadCredentials() is not null;
    private static AuthenticationHeaderValue? ReadCredentials()
    {
        var bearer = Environment.GetEnvironmentVariable("PISTATION_BITBUCKET_ACCESS_TOKEN");
        if (!string.IsNullOrWhiteSpace(bearer)) return bearer.Any(char.IsControl) ? null : new("Bearer", bearer.Trim());
        var email = Environment.GetEnvironmentVariable("PISTATION_BITBUCKET_EMAIL");
        var token = Environment.GetEnvironmentVariable("PISTATION_BITBUCKET_API_TOKEN");
        return string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token) || email.Any(char.IsControl) || token.Any(char.IsControl) ? null :
            new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(email.Trim() + ":" + token.Trim())));
    }
    internal static Uri Resolve(string path)
    {
        if (!Uri.TryCreate("https://api.bitbucket.org/2.0/" + path, UriKind.Absolute, out var uri) ||
            path.StartsWith('/') || path.Contains('\\') || Uri.TryCreate(path, UriKind.Absolute, out _) ||
            uri.Host != "api.bitbucket.org" || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !uri.AbsolutePath.StartsWith("/2.0/", StringComparison.Ordinal) || uri.Fragment.Length != 0 || path.Any(char.IsControl))
            throw new InvalidOperationException("Invalid Bitbucket API destination.");
        return uri;
    }
    public async Task<string> RequestAsync(string path, string method = "GET", object? body = null, CancellationToken token = default)
    {
        if (method is not ("GET" or "POST" or "PUT" or "DELETE")) throw new InvalidOperationException("Unsupported Bitbucket HTTP method.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        token = deadline.Token;
        var uri = Resolve(path);
        var credentials = _credentials();
        if (method != "GET" && credentials is null) throw new InvalidOperationException("Configure Bitbucket host credentials before writing.");
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), uri);
            request.Headers.Authorization = credentials;
            request.Headers.Accept.Add(new("application/json"));
            request.Headers.Accept.Add(new("text/plain", 0.9));
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var destination = response.Headers.Location is { } location ? new Uri(uri, location) : null;
                if (method != "GET" || redirects >= 3 || destination is null || destination.Scheme != "https" ||
                    destination.Authority != "api.bitbucket.org" || destination.UserInfo.Length != 0 || destination.Fragment.Length != 0 ||
                    !destination.AbsolutePath.StartsWith("/2.0/repositories/", StringComparison.Ordinal))
                    throw new InvalidOperationException("Bitbucket returned an unsupported redirect.");
                // Diff redirects may change the resource, but may not change the repository receiving credentials.
                var original = Resolve(path).AbsolutePath.Split('/'); var redirected = destination.AbsolutePath.Split('/');
                if (original.Length < 5 || redirected.Length < 5 || !original.Take(5).SequenceEqual(redirected.Take(5)))
                    throw new InvalidOperationException("Bitbucket redirected to another repository.");
                uri = destination; continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new BitbucketResponseException(response.StatusCode);
            if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidOperationException("Bitbucket response exceeds the supported size.");
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var contents = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if (contents.Length + read > MaximumBytes) throw new InvalidOperationException("Bitbucket response exceeds the supported size.");
                contents.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(contents.ToArray());
        }
    }
    public async Task<JsonElement> JsonAsync(string path, string method = "GET", object? body = null, CancellationToken token = default)
    {
        var raw = await RequestAsync(path, method, body, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw)) return default;
        try { using var document = JsonDocument.Parse(raw); return document.RootElement.Clone(); }
        catch (JsonException) { throw new InvalidOperationException("Bitbucket returned invalid JSON."); }
    }
    public void Dispose() => _http.Dispose();
}

public sealed class BitbucketResponseException(HttpStatusCode status) : InvalidOperationException(
    status == HttpStatusCode.Unauthorized ? "Bitbucket authentication was refused. Check the host credentials." :
    status == HttpStatusCode.Forbidden ? "Bitbucket denied this operation. Check account permissions and token scopes." :
    (int)status == 429 ? "Bitbucket rate limited this request. Wait before retrying." : $"Bitbucket returned HTTP {(int)status}.")
{
    public HttpStatusCode Status { get; } = status;
}
