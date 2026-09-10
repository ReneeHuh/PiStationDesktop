using System.IO.Pipes;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiStation.Host.Hubs;
using PiStation.Host.Security;
using PiStation.Host.Threads;
using PiStation.Protocol;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Hosting;

/// <summary>A loopback HTTPS host with optional explicit LAN sharing, discovered through a current-user Windows pipe.</summary>
[SupportedOSPlatform("windows")]
public sealed class SshEnvironmentHost : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly FileStream? _dataLock;
    private readonly SshHostIdentity _identity;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _discovery;
    private readonly RemoteAccessStore _access;
    private readonly RemoteEnvironmentHost? _sharing;
    private readonly X509Certificate2? _sharingCertificate;
    private readonly SshHostInfo _info;

    private SshEnvironmentHost(WebApplication application, EnvironmentService environment,
        FileStream? dataLock, SshHostIdentity identity, SshHostInfo info, string root,
        RemoteAccessStore access, RemoteEnvironmentHost? sharing, X509Certificate2? sharingCertificate)
    {
        _application = application;
        Environment = environment;
        _dataLock = dataLock;
        _identity = identity;
        _info = info;
        _access = access;
        _sharing = sharing;
        _sharingCertificate = sharingCertificate;
        _discovery = ServeDiscoveryAsync(PipeName(root), _lifetime.Token);
    }

    public SshHostInfo Info
    {
        get
        {
            var exposure = Environment.CurrentRemoteExposure;
            return _info with
            {
                PairingAddress = exposure?.Address ?? new Uri($"https://127.0.0.1:{_info.Port}/"),
                PairingCertificateFingerprint = exposure?.CertificateFingerprint ?? _info.CertificateFingerprint,
            };
        }
    }
    public EnvironmentService Environment { get; }

    public static async Task<SshEnvironmentHost> StartAsync(HostOptions options,
        IPiProcessFactory? processFactory = null, Action<string>? diagnosticLog = null,
        IPAddress? sharingAddress = null, int sharingPort = 52740, CancellationToken cancellationToken = default)
    {
        var dataLock = HostDataLock.Acquire(options);
        EnvironmentService? environment = null;
        try
        {
            environment = await EnvironmentService.CreateAsync(options, processFactory, cancellationToken: cancellationToken).ConfigureAwait(false);
            return await StartListenerAsync(options, environment, dataLock, "server", diagnosticLog, sharingAddress, sharingPort, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { if (environment is not null) await environment.DisposeAsync().ConfigureAwait(false); }
            finally { dataLock.Dispose(); }
            throw;
        }
    }

    // The desktop owns the database/service. This listener owns only its transport and identity.
    internal static Task<SshEnvironmentHost> ShareAsync(HostOptions options, EnvironmentService environment,
        Action<string>? diagnosticLog = null, CancellationToken cancellationToken = default) =>
        StartListenerAsync(options, environment, null, "desktop", diagnosticLog, null, 52740, cancellationToken);

    private static async Task<SshEnvironmentHost> StartListenerAsync(HostOptions options,
        EnvironmentService environment, FileStream? dataLock, string hostKind,
        Action<string>? diagnosticLog, IPAddress? sharingAddress, int sharingPort, CancellationToken cancellationToken)
    {
        SshHostIdentity? identity = null;
        WebApplication? application = null;
        RemoteAccessStore? access = null;
        RemoteEnvironmentHost? sharing = null;
        X509Certificate2? sharingCertificate = null;
        try
        {
            identity = SshHostIdentity.LoadOrCreate(options.CanonicalDataRoot);
            access = new RemoteAccessStore(Path.Combine(options.CanonicalDataRoot, "remote-access.db"));
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            if (diagnosticLog is not null)
            {
                builder.Logging.AddProvider(new RemoteDiagnosticLoggerProvider(diagnosticLog));
                builder.Logging.AddFilter<RemoteDiagnosticLoggerProvider>(null, LogLevel.Warning);
            }
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.Limits.MaxRequestBodySize = options.MaximumFileAttachmentBytes;
                kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(identity.Certificate));
            });
            builder.Services.AddTransient(_ => new EnvironmentHub(environment));
            builder.Services.AddSignalR(signalR => { EnvironmentTransportLimits.Configure(signalR); signalR.AddFilter<SshAuthorizationFilter>(); signalR.AddFilter(new PiStation.Host.Diagnostics.OperationDiagnosticsFilter(environment.Health)); signalR.AddFilter(new PiStation.Host.Updates.RemoteUpdateDrainFilter(environment)); })
                .AddJsonProtocol(json => json.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ProtocolJsonContext.Default));
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.GlobalLimiter = RemotePairingRateLimiter.Create();
            });
            application = builder.Build();
            var app = application;
            app.UseWebSockets();
            app.UseRateLimiter();
            RemotePairingEndpoints.Map(app, environment, access);
            app.Use(async (context, next) =>
            {
                using var stopping = app.Lifetime.ApplicationStopping.Register(context.Abort);
                if (context.Connection.RemoteIpAddress is not { } remoteAddress || !IPAddress.IsLoopback(remoteAddress))
                { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
                if (context.Request.Path == "/remote/pair" || context.Request.Path == "/remote/pair/status")
                { await next(context).ConfigureAwait(false); return; }
                var header = context.Request.Headers.Authorization.ToString();
                var credential = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : string.Empty;
                if (LoopbackAuthentication.FixedTimeEquals(credential, identity.Credential))
                { await next(context).ConfigureAwait(false); return; }
                var authorization = access.Authenticate(credential);
                if (authorization is null) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
                if ((context.Request.Path.StartsWithSegments("/session-transfers") || context.Request.Path.StartsWithSegments("/threads") || context.Request.Path.StartsWithSegments("/previews") || context.Request.Path.StartsWithSegments("/updates")) && authorization.Device.AccessLevel != RemoteAccessLevel.Operate)
                { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
                context.Items[RemoteAuthorizationFilter.AuthorizationItem] = authorization;
                using var revoked = authorization.Revoked.Register(context.Abort);
                await next(context).ConfigureAwait(false);
            });
            app.MapGet("/ssh/health", () => "ready");
            app.MapPost(DraftAttachmentEndpoint.Route, (Microsoft.AspNetCore.Http.HttpContext context) => DraftAttachmentEndpoint.HandleAsync(context, environment));
            app.MapGet(AttachmentDownloadEndpoint.Route, (Microsoft.AspNetCore.Http.HttpContext context) => AttachmentDownloadEndpoint.HandleAsync(context, environment));
            SessionTransferEndpoints.Map(app, environment);
            RemoteUpdateEndpoints.Map(app, environment);
            app.MapHub<EnvironmentHub>(EmbeddedEnvironmentHost.HubPath, hub => hub.ApplicationMaxBufferSize = EnvironmentTransportLimits.MaximumHubMessageBytes);
            app.MapGet("/previews/{lease}/tunnel", environment.PreviewLeases.TunnelAsync);
            app.MapPost("/updates/{request}/package", environment.Updates.UploadAsync);
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            environment.PreviewLeases.RegisterControlPort(app, address.Port);
            var info = new SshHostInfo(SshHostInfo.CurrentBootstrapVersion, ProtocolVersion.Current,
                environment.EnvironmentId, environment.GetDescriptor().EnvironmentName, address.Port,
                identity.Certificate.GetCertHashString(HashAlgorithmName.SHA256), identity.Credential, false,
                ProductVersion.Current, hostKind);
            info.Validate();
            if (sharingAddress is not null)
            {
                sharingCertificate = RemoteHostCertificate.LoadOrCreate(options.CanonicalDataRoot);
                sharing = await RemoteEnvironmentHost.StartAsync(environment, access, sharingAddress, sharingPort,
                    sharingCertificate, options.MaximumFileAttachmentBytes, diagnosticLog, cancellationToken).ConfigureAwait(false);
            }
            return new(app, environment, dataLock, identity, info, options.CanonicalDataRoot, access, sharing, sharingCertificate);
        }
        catch
        {
            try
            {
                try { if (sharing is not null) await sharing.DisposeAsync().ConfigureAwait(false); }
                finally { if (application is not null) await application.DisposeAsync().ConfigureAwait(false); }
            }
            finally { sharingCertificate?.Dispose(); access?.Dispose(); identity?.Dispose(); }
            throw;
        }
    }

    public static string PipeName(string dataRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).ToUpperInvariant();
        return "PiStation.Ssh." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
    }

    public static async Task<SshHostInfo?> TryDiscoverAsync(string dataRoot, CancellationToken cancellationToken = default)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName(dataRoot), PipeDirection.In,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(500, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var reader = new StreamReader(pipe, Encoding.UTF8);
        var buffer = new char[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
            if (read == 0)
            {
                var info = JsonSerializer.Deserialize(buffer.AsSpan(0, length), ProtocolJsonContext.Default.SshHostInfo)
                    ?? throw new InvalidOperationException("Invalid SSH host discovery response.");
                info.Validate(requireCompatibleVersion: false);
                return info;
            }
            length += read;
        }
        throw new InvalidOperationException("SSH host discovery response exceeded its limit.");
    }

    private Task ServeDiscoveryAsync(string pipeName, CancellationToken cancellationToken) =>
        Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ServeDiscoveryWorkerAsync(pipeName, cancellationToken)));

    private async Task ServeDiscoveryWorkerAsync(string pipeName, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 4, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    await JsonSerializer.SerializeAsync(pipe, Info, ProtocolJsonContext.Default.SshHostInfo, timeout.Token).ConfigureAwait(false);
                    await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (IOException) { }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        Environment.PreviewLeases.UnregisterControlPort(_application);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            try
            {
                try { await _discovery.ConfigureAwait(false); }
                finally { if (_sharing is not null) await _sharing.DisposeAsync().ConfigureAwait(false); }
            }
            finally
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _application.StopAsync(timeout.Token).ConfigureAwait(false); }
                finally { await _application.DisposeAsync().ConfigureAwait(false); }
            }
        }
        finally
        {
            try { if (_dataLock is not null) await Environment.DisposeAsync().ConfigureAwait(false); }
            finally { _access.Dispose(); _sharingCertificate?.Dispose(); _identity.Dispose(); _dataLock?.Dispose(); _lifetime.Dispose(); }
        }
    }
}
