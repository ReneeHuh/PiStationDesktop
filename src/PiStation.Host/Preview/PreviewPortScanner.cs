using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Preview;

public interface IPreviewListenerSource
{
    IReadOnlyCollection<int> GetListeningPorts();
    IReadOnlyCollection<PreviewListener> GetListeners() =>
        GetListeningPorts().Select(port => new PreviewListener(port)).ToArray();
}

public sealed record PreviewListener(int Port, int? ProcessId = null, string? ProcessName = null,
    string Host = "127.0.0.1");

public interface IPreviewEndpointProbe
{
    Task<DiscoveredPreviewServer?> ProbeAsync(int port, CancellationToken cancellationToken);
    Task<DiscoveredPreviewServer?> ProbeAsync(PreviewListener listener, CancellationToken cancellationToken) =>
        ProbeAsync(listener.Port, cancellationToken);
}

public sealed class PreviewPortScanner : IDisposable
{
    private static readonly int[] FallbackPorts =
    [
        3000, 3001, 3333, 4173, 4200, 4321, 5000, 5173, 5174, 5175,
        5500, 8000, 8080, 8081, 8888, 9000,
    ];

    private readonly IPreviewListenerSource _listenerSource;
    private readonly IPreviewEndpointProbe _probe;
    private readonly PreviewHttpEndpointProbe? _ownedProbe;
    private readonly Func<IReadOnlyDictionary<int, PreviewTerminalOwner>> _processOwners;
    private readonly ConcurrentDictionary<PreviewListener, (DateTimeOffset Expires, DiscoveredPreviewServer? Server)> _cache = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    public PreviewPortScanner(Func<IReadOnlyDictionary<int, PreviewTerminalOwner>>? processOwners = null)
    {
        _listenerSource = new WindowsPreviewListenerSource();
        _processOwners = processOwners ?? (() => new Dictionary<int, PreviewTerminalOwner>());
        var probe = new PreviewHttpEndpointProbe();
        _probe = probe;
        _ownedProbe = probe;
    }

    public PreviewPortScanner(IPreviewListenerSource listenerSource, IPreviewEndpointProbe probe,
        Func<IReadOnlyDictionary<int, PreviewTerminalOwner>>? processOwners = null)
    {
        _listenerSource = listenerSource ?? throw new ArgumentNullException(nameof(listenerSource));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _processOwners = processOwners ?? (() => new Dictionary<int, PreviewTerminalOwner>());
    }

    public async Task<(IReadOnlyList<DiscoveredPreviewServer> Servers, bool IsTruncated)> ScanAsync(
        CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ScanCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _scanGate.Release(); }
    }

    private async Task<(IReadOnlyList<DiscoveredPreviewServer> Servers, bool IsTruncated)> ScanCoreAsync(
        CancellationToken cancellationToken)
    {
        var listeners = _listenerSource.GetListeners().Where(listener => listener.Port is > 0 and <= 65535)
            .GroupBy(listener => listener.Port).ToDictionary(group => group.Key,
                group => group.OrderBy(listener => listener.Host == "127.0.0.1" ? 0 : 1).First());
        IReadOnlyDictionary<int, PreviewTerminalOwner> owners;
        try { owners = _processOwners(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning("Preview process ownership is unavailable: {0}", error.Message);
            owners = new Dictionary<int, PreviewTerminalOwner>();
        }
        var allCandidates = listeners.Keys.Concat(FallbackPorts).Distinct()
            .OrderBy(port => listeners.GetValueOrDefault(port)?.ProcessId is { } pid && owners.ContainsKey(pid) ? 0 : 1)
            .ThenBy(port => port)
            .ToArray();
        var candidatePorts = allCandidates
            .Take(PreviewDiscoveryDefaults.MaximumCandidatePorts)
            .ToArray();
        var found = new ConcurrentBag<DiscoveredPreviewServer>();

        await Parallel.ForEachAsync(
            candidatePorts,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = PreviewDiscoveryDefaults.MaximumProbeConcurrency,
            },
            async (port, token) =>
            {
                var listener = listeners.GetValueOrDefault(port) ?? new PreviewListener(port);
                if (!_cache.TryGetValue(listener, out var cached) || cached.Expires <= DateTimeOffset.UtcNow)
                {
                    var probed = await _probe.ProbeAsync(listener, token).ConfigureAwait(false);
                    cached = (DateTimeOffset.UtcNow.AddSeconds(15), probed);
                    _cache[listener] = cached;
                }
                var server = cached.Server;
                if (server is not null)
                {
                    // Redirects can change ports. Attribute the resulting endpoint,
                    // never the process serving the redirect on the original port.
                    var endpoint = listeners.GetValueOrDefault(server.Port);
                    // A DNS alias (including localhost) can select a different
                    // address family or listener. Only claim the known endpoint.
                    if (endpoint?.Host != server.Host) endpoint = null;
                    found.Add(server with
                    {
                        ProcessId = endpoint?.ProcessId,
                        ProcessName = endpoint?.ProcessName,
                        Terminal = endpoint?.ProcessId is { } pid ? owners.GetValueOrDefault(pid) : null,
                    });
                }
            }).ConfigureAwait(false);

        foreach (var key in _cache.Keys)
            if (!candidatePorts.Contains(key.Port) || (listeners.GetValueOrDefault(key.Port) ?? new PreviewListener(key.Port)) != key)
                _cache.TryRemove(key, out _);

        var ordered = found
            .OrderBy(static server => server.Port)
            .ThenBy(static server => server.Scheme, StringComparer.Ordinal)
            .Take(PreviewDiscoveryDefaults.MaximumResults)
            .ToArray();
        return (
            ordered,
            allCandidates.Length > candidatePorts.Length || found.Count > ordered.Length);
    }

    public void Dispose() => _ownedProbe?.Dispose();

    public sealed class PreviewHttpEndpointProbe : IPreviewEndpointProbe, IDisposable
    {
        private const int MaximumRedirects = 3;
        private readonly HttpClient _client;

        public PreviewHttpEndpointProbe()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = static (request, _, _, errors) =>
                    errors == System.Net.Security.SslPolicyErrors.None ||
                    request.RequestUri is { } uri && IsLoopback(uri),
            };
            _client = new HttpClient(handler, disposeHandler: true);
        }

        public async Task<DiscoveredPreviewServer?> ProbeAsync(
            int port,
            CancellationToken cancellationToken) => await ProbeAsync(new PreviewListener(port), cancellationToken).ConfigureAwait(false);

        public async Task<DiscoveredPreviewServer?> ProbeAsync(PreviewListener listener, CancellationToken cancellationToken)
        {
            foreach (var scheme in new[] { Uri.UriSchemeHttp, Uri.UriSchemeHttps })
            {
                var discovered = await ProbeSchemeAsync(scheme, listener.Host, listener.Port, cancellationToken).ConfigureAwait(false);
                if (discovered is not null)
                {
                    return discovered;
                }
            }

            return null;
        }

        public void Dispose() => _client.Dispose();

        private async Task<DiscoveredPreviewServer?> ProbeSchemeAsync(
            string scheme,
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            var current = new UriBuilder(scheme, host, port).Uri;
            try
            {
                for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, current);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(PreviewDiscoveryDefaults.ProbeTimeoutMilliseconds);
                    using var response = await _client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token).ConfigureAwait(false);

                    if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
                    {
                        var redirected = location.IsAbsoluteUri ? location : new Uri(current, location);
                        if (!IsAllowedLoopbackUri(redirected))
                        {
                            return null;
                        }

                        current = redirected;
                        continue;
                    }

                    if (!response.IsSuccessStatusCode || !IsHtml(response.Content.Headers.ContentType?.MediaType))
                    {
                        return null;
                    }

                    var url = current.AbsoluteUri;
                    if (url.Length > PreviewDiscoveryDefaults.MaximumUrlLength)
                    {
                        return null;
                    }

                    return new DiscoveredPreviewServer(
                        url,
                        current.IdnHost,
                        current.Port,
                        current.Scheme);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (HttpRequestException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            return null;
        }

        private static bool IsHtml(string? mediaType) =>
            string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

        private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
            HttpStatusCode.MovedPermanently or
            HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

        private static bool IsAllowedLoopbackUri(Uri uri) =>
            uri.AbsoluteUri.Length <= PreviewDiscoveryDefaults.MaximumUrlLength &&
            (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
            IsLoopback(uri);

        private static bool IsLoopback(Uri uri)
        {
            if (string.Equals(uri.IdnHost, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IPAddress.TryParse(uri.IdnHost, out var address) && IPAddress.IsLoopback(address);
        }
    }
}
