using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using PiStation.Host.Hubs;
using PiStation.Host.Security;
using PiStation.Host.Threads;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Hosting;

public sealed class EmbeddedEnvironmentHost : IAsyncDisposable
{
    public const string HubPath = "/environment";

    private readonly WebApplication _application;
    private readonly FileStream _dataLock;
    private SshEnvironmentHost? _sshListener;

    private EmbeddedEnvironmentHost(
        WebApplication application,
        EnvironmentService environment,
        Uri address,
        string bearerCredential, FileStream dataLock)
    {
        _application = application;
        Environment = environment;
        Address = address;
        BearerCredential = bearerCredential;
        _dataLock = dataLock;
        environment.PreviewLeases.RegisterControlPort(this, address.Port);
    }

    public Uri Address { get; }

    public Uri HubAddress => new(Address, HubPath);

    public string BearerCredential { get; }

    public EnvironmentService Environment { get; }

    public static async Task<EmbeddedEnvironmentHost> StartAsync(
        HostOptions options,
        IPiProcessFactory? processFactory = null,
        CancellationToken cancellationToken = default)
    {
        var dataLock = HostDataLock.Acquire(options);
        try { return await StartCoreAsync(options, processFactory, dataLock, cancellationToken).ConfigureAwait(false); }
        catch { dataLock.Dispose(); throw; }
    }

    private static async Task<EmbeddedEnvironmentHost> StartCoreAsync(HostOptions options,
        IPiProcessFactory? processFactory, FileStream dataLock, CancellationToken cancellationToken)
    {
        var environment = await EnvironmentService.CreateAsync(options, processFactory, cancellationToken)
            .ConfigureAwait(false);
        var credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = options.MaximumFileAttachmentBytes;
            kestrel.Listen(IPAddress.Loopback, 0);
        });
        builder.Services.AddSingleton(environment);
        builder.Services.AddSignalR(options => { EnvironmentTransportLimits.Configure(options); options.AddFilter(new PiStation.Host.Updates.RemoteUpdateDrainFilter(environment)); }).AddJsonProtocol(json =>
            json.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ProtocolJsonContext.Default));
        var application = builder.Build();
        application.UseWebSockets();
        application.Use((context, next) => LoopbackAuthentication.InvokeAsync(context, credential, next));
        application.MapPost(DraftAttachmentEndpoint.Route, DraftAttachmentEndpoint.HandleAsync);
        application.MapHub<EnvironmentHub>(HubPath);
        application.MapGet("/previews/{lease}/tunnel", environment.PreviewLeases.TunnelAsync);
        application.MapPost("/updates/{request}/package", environment.Updates.UploadAsync);
        try
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            var addresses = application.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?.Addresses;
            var address = addresses?.Select(static value => new Uri(value)).SingleOrDefault()
                ?? throw new InvalidOperationException("Kestrel did not report its loopback address.");
            var host = new EmbeddedEnvironmentHost(application, environment, address, credential, dataLock);
            if (OperatingSystem.IsWindows())
                host._sshListener = await SshEnvironmentHost.ShareAsync(options, environment,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            return host;
        }
        catch
        {
            await application.DisposeAsync().ConfigureAwait(false);
            await environment.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Environment.PreviewLeases.UnregisterControlPort(this);
        try
        {
            try { if (OperatingSystem.IsWindows() && _sshListener is not null) await _sshListener.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { await _application.StopAsync().ConfigureAwait(false); }
                finally { await _application.DisposeAsync().ConfigureAwait(false); }
            }
        }
        finally
        {
            try { await Environment.DisposeAsync().ConfigureAwait(false); }
            finally { _dataLock.Dispose(); }
        }
    }
}
