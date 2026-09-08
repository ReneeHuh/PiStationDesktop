using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

/// <summary>A browser-scoped local origin. Device credentials remain in its native data channel.</summary>
public sealed class RemotePreviewProxy : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly HttpMessageInvoker _upstream;
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private readonly Func<Task> _close;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _requests = new(32, 32);
    private int _disposed;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    private RemotePreviewProxy(WebApplication application, Uri target, Func<CancellationToken, Task<Stream>> connect, Func<Task> close)
    {
        _application = application; Target = target; _connect = connect; _close = close;
        var handler = new SocketsHttpHandler
        {
            UseCookies = false, AllowAutoRedirect = false, UseProxy = false, MaxConnectionsPerServer = 16,
            ConnectTimeout = TimeSpan.FromSeconds(15), PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectCallback = async (_, token) => await _connect(token).ConfigureAwait(false),
        };
        _upstream = new HttpMessageInvoker(handler, disposeHandler: false);
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Uri Target { get; }
    public Uri Address { get; private set; } = null!;
    public string CookieName { get; } = "__pistation_preview_" + Guid.NewGuid().ToString("N");
    public string CookieValue { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal Func<bool>? IsAvailable { get; set; }
    public event EventHandler? Reconnected;
    internal void NotifyReconnected() => Reconnected?.Invoke(this, EventArgs.Empty);

    internal static async Task<RemotePreviewProxy> StartAsync(Uri target, Func<CancellationToken, Task<Stream>> connect,
        Func<Task> close, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxRequestBodySize = 32 * 1024 * 1024;
            options.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
            options.Limits.MaxConcurrentConnections = 64;
        });
        var app = builder.Build();
        var proxy = new RemotePreviewProxy(app, target, connect, close);
        app.UseWebSockets();
        app.Run(proxy.HandleAsync);
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            var bound = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            // Chromium resolves *.localhost to loopback. Different mappings get different cookie/storage origins.
            proxy.Address = new UriBuilder(bound) { Host = "pistation-" + Guid.NewGuid().ToString("N") + ".localhost" }.Uri;
            return proxy;
        }
        catch { await proxy.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public Uri ToBrowserUri(Uri logical) => SameOrigin(logical, Target)
        ? new UriBuilder(Address) { Path = logical.AbsolutePath, Query = logical.Query, Fragment = logical.Fragment }.Uri : logical;
    public Uri ToLogicalUri(Uri browser) => SameOrigin(browser, Address)
        ? new UriBuilder(Target) { Path = browser.AbsolutePath, Query = browser.Query, Fragment = browser.Fragment }.Uri : browser;

    private async Task HandleAsync(HttpContext context)
    {
        if (IsAvailable?.Invoke() == false || context.Request.Host.Value != Address.Authority ||
            !context.Request.Cookies.TryGetValue(CookieName, out var cookie) || !FixedEquals(cookie, CookieValue) ||
            context.Request.Headers.Origin.Count > 0 && context.Request.Headers.Origin.ToString() != Address.GetLeftPart(UriPartial.Authority))
        { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        if (!await _requests.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
        { context.Response.StatusCode = StatusCodes.Status429TooManyRequests; return; }
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stopping.Token);
        try
        {
            var target = new UriBuilder(Target) { Path = context.Request.Path.Value, Query = context.Request.QueryString.Value, Fragment = string.Empty }.Uri;
            if (context.WebSockets.IsWebSocketRequest)
            {
                await ForwardWebSocketAsync(context, target, stopping.Token).ConfigureAwait(false);
                return;
            }
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
            if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
                request.Content = new StreamContent(context.Request.Body);
            foreach (var header in context.Request.Headers)
            {
                if (Skip(header.Key, context.Request.Headers.Connection.ToString())) continue;
                var values = RequestValues(header.Key, header.Value.ToArray()!);
                if (!request.Headers.TryAddWithoutValidation(header.Key, values)) request.Content?.Headers.TryAddWithoutValidation(header.Key, values);
            }
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stopping.Token).ConfigureAwait(false);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (Skip(header.Key, string.Join(",", response.Headers.Connection))) continue;
                context.Response.Headers[header.Key] = header.Value.Select(value => ResponseValue(header.Key, value)).ToArray();
            }
            await response.Content.CopyToAsync(context.Response.Body, stopping.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!context.Response.HasStarted && !stopping.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsync("The remote preview is unavailable. Reconnect or reload Preview.", stopping.Token).ConfigureAwait(false);
        }
        finally { _requests.Release(); }
    }

    private async Task ForwardWebSocketAsync(HttpContext context, Uri target, CancellationToken cancellationToken)
    {
        using var remote = new ClientWebSocket();
        foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols) remote.Options.AddSubProtocol(protocol);
        foreach (var header in context.Request.Headers)
        {
            if (Skip(header.Key, context.Request.Headers.Connection.ToString()) || header.Key.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase)) continue;
            remote.Options.SetRequestHeader(header.Key, string.Join("; ", RequestValues(header.Key, header.Value.ToArray()!)));
        }
        var socketUri = new UriBuilder(target) { Scheme = target.Scheme == "https" ? "wss" : "ws" }.Uri;
        await remote.ConnectAsync(socketUri, _upstream, cancellationToken).ConfigureAwait(false);
        using var browser = await context.WebSockets.AcceptWebSocketAsync(remote.SubProtocol).ConfigureAwait(false);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var a = CopySocketAsync(browser, remote, stopping.Token);
        var b = CopySocketAsync(remote, browser, stopping.Token);
        await Task.WhenAny(a, b).ConfigureAwait(false);
        try { await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
        finally
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            if (browser.State != WebSocketState.Closed) browser.Abort();
            if (remote.State != WebSocketState.Closed) remote.Abort();
            try { await Task.WhenAll(a, b).ConfigureAwait(false); } catch (Exception) when (stopping.IsCancellationRequested) { }
        }
    }

    private static async Task CopySocketAsync(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await source.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await destination.CloseOutputAsync(source.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                    source.CloseStatusDescription, cancellationToken).ConfigureAwait(false);
                return;
            }
            await destination.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, cancellationToken).ConfigureAwait(false);
        }
    }

    private string[] RequestValues(string name, string[] values)
    {
        if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) return [Target.GetLeftPart(UriPartial.Authority)];
        if (name.Equals("Referer", StringComparison.OrdinalIgnoreCase))
            return values.Select(v => Uri.TryCreate(v, UriKind.Absolute, out var uri) ? ToLogicalUri(uri).AbsoluteUri : string.Empty).ToArray();
        if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            return [string.Join("; ", values.SelectMany(v => v.Split(';')).Select(v => v.Trim()).Where(v => !v.StartsWith("__pistation_preview_", StringComparison.Ordinal)))];
        return values;
    }

    private string ResponseValue(string name, string value)
    {
        if (name.Equals("Location", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return ToBrowserUri(uri).AbsoluteUri;
        if (name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            return string.Join(";", value.Split(';').Where(part => !part.TrimStart().StartsWith("Domain=", StringComparison.OrdinalIgnoreCase)));
        return value;
    }

    private static bool Skip(string name, string connection) =>
        name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) || name.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Trailer", StringComparison.OrdinalIgnoreCase) || connection.Split(',').Any(v => v.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
    private static bool SameOrigin(Uri a, Uri b) => a.Scheme == b.Scheme && a.Host == b.Host && a.Port == b.Port;
    private static bool FixedEquals(string a, string b) => a.Length == b.Length &&
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _application.StopAsync(timeout.Token).ConfigureAwait(false); }
        finally
        {
            await _application.DisposeAsync().ConfigureAwait(false);
            _http.Dispose(); _upstream.Dispose();
            try { await _close().ConfigureAwait(false); } catch (Exception) { }
            _stopping.Dispose();
        }
    }
}
