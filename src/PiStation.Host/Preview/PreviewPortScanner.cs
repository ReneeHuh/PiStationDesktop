using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using PiStation.Protocol.Models;

namespace PiStation.Host.Preview;

public interface IPreviewListenerSource
{
    IReadOnlyCollection<int> GetListeningPorts();
}

public interface IPreviewEndpointProbe
{
    Task<DiscoveredPreviewServer?> ProbeAsync(int port, CancellationToken cancellationToken);
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

    public PreviewPortScanner()
    {
        _listenerSource = new SystemPreviewListenerSource();
        var probe = new PreviewHttpEndpointProbe();
        _probe = probe;
        _ownedProbe = probe;
    }

    public PreviewPortScanner(IPreviewListenerSource listenerSource, IPreviewEndpointProbe probe)
    {
        _listenerSource = listenerSource ?? throw new ArgumentNullException(nameof(listenerSource));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public async Task<(IReadOnlyList<DiscoveredPreviewServer> Servers, bool IsTruncated)> ScanAsync(
        CancellationToken cancellationToken = default)
    {
        var allCandidates = _listenerSource.GetListeningPorts()
            .Concat(FallbackPorts)
            .Where(static port => port is >= IPEndPoint.MinPort and <= IPEndPoint.MaxPort)
            .Distinct()
            .Order()
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
                var server = await _probe.ProbeAsync(port, token).ConfigureAwait(false);
                if (server is not null)
                {
                    found.Add(server);
                }
            }).ConfigureAwait(false);

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

    private sealed class SystemPreviewListenerSource : IPreviewListenerSource
    {
        public IReadOnlyCollection<int> GetListeningPorts()
        {
            try
            {
                return IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpListeners()
                    .Where(static endpoint =>
                        endpoint.Address.Equals(IPAddress.Any) ||
                        endpoint.Address.Equals(IPAddress.IPv6Any) ||
                        IPAddress.IsLoopback(endpoint.Address))
                    .Select(static endpoint => endpoint.Port)
                    .Distinct()
                    .ToArray();
            }
            catch (NetworkInformationException)
            {
                return [];
            }
        }
    }

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
            CancellationToken cancellationToken)
        {
            foreach (var scheme in new[] { Uri.UriSchemeHttp, Uri.UriSchemeHttps })
            {
                var discovered = await ProbeSchemeAsync(scheme, port, cancellationToken).ConfigureAwait(false);
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
            int port,
            CancellationToken cancellationToken)
        {
            var current = new UriBuilder(scheme, IPAddress.Loopback.ToString(), port).Uri;
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
