using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PiStation.Host.Hubs;
using PiStation.Host.Security;
using PiStation.Host.Projects;
using PiStation.Host.SourceControl;
using PiStation.Host.Threads;
using PiStation.Host.Workspaces;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Hosting;

public sealed class EmbeddedEnvironmentHost : IAsyncDisposable
{
    public const string HubPath = "/environment";

    private readonly WebApplication _application;

    private EmbeddedEnvironmentHost(
        WebApplication application,
        EnvironmentService environment,
        Uri address,
        string bearerCredential)
    {
        _application = application;
        Environment = environment;
        Address = address;
        BearerCredential = bearerCredential;
    }

    public Uri Address { get; }

    public Uri HubAddress => new(Address, HubPath);

    public string BearerCredential { get; }

    public EnvironmentService Environment { get; }

    public static async Task<EmbeddedEnvironmentHost> StartAsync(
        HostOptions options,
        IPiProcessFactory? processFactory = null,
        Func<ThreadWorkspaceResolver, ProjectService, SourceControlHostingService>? sourceControlFactory = null,
        CancellationToken cancellationToken = default)
    {
        var environment = await EnvironmentService.CreateAsync(options, processFactory, sourceControlFactory, cancellationToken)
            .ConfigureAwait(false);
        var credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = options.MaximumFileAttachmentBytes;
            kestrel.Listen(IPAddress.Loopback, 0);
        });
        builder.Services.AddSingleton(environment);
        // Bounded multi-task workflows can exceed SignalR's 32 KiB default.
        builder.Services.AddSignalR(hub => hub.MaximumReceiveMessageSize = 1024 * 1024).AddJsonProtocol(json =>
            json.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ProtocolJsonContext.Default));
        var application = builder.Build();
        application.Use((context, next) => LoopbackAuthentication.InvokeAsync(context, credential, next));
        application.MapPost(DraftAttachmentEndpoint.Route, DraftAttachmentEndpoint.HandleAsync);
        application.MapHub<EnvironmentHub>(HubPath, hub => hub.ApplicationMaxBufferSize = 1024 * 1024);
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
            return new EmbeddedEnvironmentHost(application, environment, address, credential);
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
        await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
        await Environment.DisposeAsync().ConfigureAwait(false);
    }
}
