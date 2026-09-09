using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiStation.Host.Hubs;
using PiStation.Host.Security;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Hosting;

/// <summary>Owns only network exposure. Environment lifetime belongs to the desktop or headless host.</summary>
public sealed class RemoteEnvironmentHost : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly EnvironmentService _environment;
    private readonly object _forwardedPortOwner = new();
    private readonly string _fingerprint;
    private RemoteEnvironmentHost(WebApplication application, EnvironmentService environment, Uri address, string fingerprint)
    {
        _application = application; _environment = environment; Address = address; _fingerprint = fingerprint;
        environment.RegisterRemoteExposure(this, address, fingerprint);
        environment.PreviewLeases.RegisterControlPort(this, address.Port);
    }

    public Uri Address { get; }

    public void AdvertiseForwardedAddress(Uri address)
    {
        RemoteEndpoint.Validate(address);
        _environment.RegisterRemoteExposure(this, address, _fingerprint);
        _environment.PreviewLeases.RegisterControlPort(_forwardedPortOwner, address.Port);
    }

    public static async Task<RemoteEnvironmentHost> StartAsync(EnvironmentService environment,
        RemoteAccessStore access, IPAddress address, int port, X509Certificate2 certificate,
        long maximumAttachmentBytes = AttachmentDefaults.MaximumFileBytes,
        Action<string>? diagnosticLog = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast)
            throw new ArgumentException("Select a specific local network address.", nameof(address));
        if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            throw new ArgumentException("A valid server certificate with a private key is required.", nameof(certificate));
        var builder = WebApplication.CreateSlimBuilder();
        // Retain diagnostic event metadata only, never request state, headers or exception messages.
        builder.Logging.ClearProviders();
        if (diagnosticLog is not null)
        {
            builder.Logging.AddProvider(new RemoteDiagnosticLoggerProvider(diagnosticLog));
            builder.Logging.AddFilter<RemoteDiagnosticLoggerProvider>(null, LogLevel.Warning);
            builder.Logging.AddFilter<RemoteDiagnosticLoggerProvider>(RemoteDiagnosticLoggerProvider.HttpsCategory, LogLevel.Debug);
        }
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = maximumAttachmentBytes;
            kestrel.Listen(address, port, listener => listener.UseHttps(certificate));
        });
        // Do not register the shared disposable environment in DI: this listener does not own it.
        builder.Services.AddTransient(_ => new EnvironmentHub(environment));
        builder.Services.AddSignalR(options => { EnvironmentTransportLimits.Configure(options); options.AddFilter<RemoteAuthorizationFilter>(); options.AddFilter(new PiStation.Host.Updates.RemoteUpdateDrainFilter(environment)); })
            .AddJsonProtocol(json => json.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ProtocolJsonContext.Default));
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = RemotePairingRateLimiter.Create();
        });
        var app = builder.Build();
        app.UseWebSockets();
        app.UseRateLimiter();
        RemotePairingEndpoints.Map(app, environment, access);
        app.MapGet(RemoteEndpointIdentity.Path, () => Results.Json(
            new RemoteEndpointIdentity(environment.GetDescriptor().EnvironmentId, Protocol.ProtocolVersion.Current),
            ProtocolJsonContext.Default.RemoteEndpointIdentity));
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/remote/pair" || context.Request.Path == "/remote/pair/status" ||
                context.Request.Path == RemoteEndpointIdentity.Path)
            { await next(context).ConfigureAwait(false); return; }
            var header = context.Request.Headers.Authorization.ToString();
            var authorization = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? access.Authenticate(header[7..]) : null;
            if (authorization is null) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
            if ((context.Request.Path.StartsWithSegments("/session-transfers") || context.Request.Path.StartsWithSegments("/threads") || context.Request.Path.StartsWithSegments("/previews") || context.Request.Path.StartsWithSegments("/updates")) && authorization.Device.AccessLevel != RemoteAccessLevel.Operate)
            { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            context.Items[RemoteAuthorizationFilter.AuthorizationItem] = authorization;
            using var registration = authorization.Revoked.Register(context.Abort);
            using var stopping = app.Lifetime.ApplicationStopping.Register(context.Abort);
            await next(context).ConfigureAwait(false);
        });
        app.MapPost(DraftAttachmentEndpoint.Route, (HttpContext context) => DraftAttachmentEndpoint.HandleAsync(context, environment));
        app.MapGet(AttachmentDownloadEndpoint.Route, (HttpContext context) => AttachmentDownloadEndpoint.HandleAsync(context, environment));
        SessionTransferEndpoints.Map(app, environment);
        RemoteUpdateEndpoints.Map(app, environment);
        app.MapHub<EnvironmentHub>(EmbeddedEnvironmentHost.HubPath, hub => hub.ApplicationMaxBufferSize = EnvironmentTransportLimits.MaximumHubMessageBytes);
        app.MapGet("/previews/{lease}/tunnel", environment.PreviewLeases.TunnelAsync);
        app.MapPost("/updates/{request}/package", environment.Updates.UploadAsync);
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            var boundAddress = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, environment, new Uri(boundAddress), certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256));
        }
        catch { await app.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        _environment.UnregisterRemoteExposure(this);
        _environment.PreviewLeases.UnregisterControlPort(this);
        _environment.PreviewLeases.UnregisterControlPort(_forwardedPortOwner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _application.StopAsync(timeout.Token).ConfigureAwait(false); }
        finally { await _application.DisposeAsync().ConfigureAwait(false); }
    }
}
