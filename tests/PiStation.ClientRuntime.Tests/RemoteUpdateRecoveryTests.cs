using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiStation.Host;
using PiStation.Host.Hosting;
using PiStation.Host.Hubs;
using PiStation.Host.Security;
using PiStation.Host.Updates;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime.Tests;

public sealed class RemoteUpdateRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompatibleWorkspaceCanStageCancelActivateAndRecoverWithAnIsolatedUpdateConnection(bool legacy)
    {
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var host = await RecoveryHost.StartAsync(directory, legacy);
        await using var workspace = new EnvironmentClient(host.Options);
        var mismatch = await Assert.ThrowsAsync<ConnectionValidationException>(() => workspace.ConnectAsync(timeout.Token));
        Assert.Equal(ConnectionFailure.Protocol, mismatch.Failure);
        Assert.Equal(EnvironmentConnectionState.Incompatible, workspace.ConnectionState);
        Assert.Null(workspace.Descriptor);
        await using var updates = workspace.CreateRemoteUpdateClient();
        Assert.True((await updates.GetDescriptorAsync(timeout.Token)).Enabled);
        var package = Path.Combine(directory.Path, "fixture.zip");
        await File.WriteAllTextAsync(package, "verified fixture", timeout.Token);
        var id = Guid.NewGuid();
        var staged = await updates.StageAsync(package, id, token: timeout.Token);
        Assert.Equal(RemoteUpdateState.Ready, staged.State);
        Assert.Equal(staged, await updates.StageAsync(package, id, token: timeout.Token));
        await File.WriteAllTextAsync(package, "different fixture", timeout.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => updates.StageAsync(package, id, token: timeout.Token));
        Assert.Equal(RemoteUpdateState.Canceled, (await updates.CancelAsync(id, timeout.Token)).State);
        await File.WriteAllTextAsync(package, "verified fixture", timeout.Token);
        id = Guid.NewGuid();
        await updates.StageAsync(package, id, token: timeout.Token);
        Assert.Equal(RemoteUpdateState.WaitingForIdle, (await updates.CommitAsync(new(id), timeout.Token)).State);
        await using var reopened = new RemoteUpdateClient(host.Options);
        await host.Owner.Activated.Task.WaitAsync(timeout.Token);
        var status = await reopened.GetReceiptAsync(id, timeout.Token);
        Assert.Equal(RemoteUpdateState.Succeeded, status?.State);
        Assert.Equal(2, (await reopened.GetHistoryAsync(timeout.Token)).Length);
        Assert.Equal(EnvironmentConnectionState.Incompatible, workspace.ConnectionState);
        Assert.Equal(0, workspace.ActiveThreadStreamCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongIdentityPinReadOnlyAndRevokedCredentialsCannotUseRecovery(bool legacy)
    {
        using var directory = new ClientTestDirectory();
        await using var host = await RecoveryHost.StartAsync(directory, legacy);
        await using var wrongEnvironment = new RemoteUpdateClient(host.Options with { ExpectedEnvironmentId = EnvironmentId.New() });
        Assert.Equal(ConnectionFailure.Identity, (await Assert.ThrowsAsync<ConnectionValidationException>(() => wrongEnvironment.GetDescriptorAsync())).Failure);
        await using var wrongPin = new RemoteUpdateClient(host.Options with { CertificateFingerprint = new string('0', 64) });
        Assert.Equal(ConnectionFailure.Certificate, (await Assert.ThrowsAsync<ConnectionValidationException>(() => wrongPin.GetDescriptorAsync())).Failure);
        await using var observer = new RemoteUpdateClient(host.Options with { BearerCredential = host.Access.IssueSession(RemoteAccessLevel.ReadOnly).Token });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => observer.GetDescriptorAsync());
        await using var updates = new RemoteUpdateClient(host.Options);
        Assert.True((await updates.GetDescriptorAsync()).Supported);
        host.Access.Revoke(host.DeviceId);
        Assert.Equal(ConnectionFailure.Authentication, (await Assert.ThrowsAsync<ConnectionValidationException>(() => updates.GetDescriptorAsync())).Failure);
    }

    [Fact]
    public async Task MaintenanceMutationsRequireEnvironmentIdentityAndReceiptsStayDeviceScoped()
    {
        using var directory = new ClientTestDirectory();
        await using var host = await RecoveryHost.StartAsync(directory, false);
        await using var updates = new RemoteUpdateClient(host.Options);
        var package = Path.Combine(directory.Path, "fixture.zip");
        await File.WriteAllTextAsync(package, "fixture");
        var id = Guid.NewGuid();
        await updates.StageAsync(package, id);
        var otherOptions = host.Options with { BearerCredential = host.Access.IssueSession().Token };
        await using var other = new RemoteUpdateClient(otherOptions);
        Assert.Null(await other.GetReceiptAsync(id));
        Assert.Empty(await other.GetHistoryAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => other.CommitAsync(new(id)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => other.CancelAsync(id));
        using var http = new HttpClient(RemoteTransport.CreateHandler(host.Options.CertificateFingerprint)) { BaseAddress = new(host.Options.HubAddress, "/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", host.Options.BearerCredential);
        using (var response = await http.PostAsync($"updates/v1/{id:D}/cancel", null)) Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        http.DefaultRequestHeaders.Add("X-PiStation-Environment-Id", EnvironmentId.New().Value);
        using (var response = await http.PostAsync($"updates/v1/{id:D}/cancel", null)) Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(RemoteUpdateState.Ready, (await updates.GetReceiptAsync(id))?.State);
        http.DefaultRequestHeaders.Remove("X-PiStation-Environment-Id");
        http.DefaultRequestHeaders.Add("X-PiStation-Environment-Id", host.Environment.EnvironmentId.Value);
        using var oversized = new StringContent(new string('x', 4097));
        using (var response = await http.PostAsync("updates/v1/prepare", oversized)) Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnsupportedMaintenanceVersionAndRedirectNeverFallBackToWorkspaceRpcs()
    {
        using var directory = new ClientTestDirectory();
        await using var host = await RecoveryHost.StartAsync(directory, false, maintenanceVersion: 999);
        await using var updates = new RemoteUpdateClient(host.Options);
        await Assert.ThrowsAsync<NotSupportedException>(() => updates.GetDescriptorAsync());
        Assert.Equal(0, host.WorkspaceProbes);
        host.RedirectDescriptor = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => updates.GetDescriptorAsync());
        Assert.Equal(0, host.WorkspaceProbes);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("https")]
    [InlineData("ssh")]
    public async Task ProductionListenersExposeUpdateRecoveryWithoutConnectingWorkspace(string transport)
    {
        if (transport == "ssh" && !OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        await using var local = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions());
        if (transport == "local")
        {
            await using var updates = new RemoteUpdateClient(new() { HubAddress = local.HubAddress, BearerCredential = local.BearerCredential });
            Assert.False((await updates.GetDescriptorAsync()).Supported);
            Assert.Empty(await updates.GetHistoryAsync());
            return;
        }
        if (transport == "ssh" && OperatingSystem.IsWindows())
        {
            var info = (await SshEnvironmentHost.TryDiscoverAsync(directory.CreateHostOptions().CanonicalDataRoot))!;
            await using var updates = new RemoteUpdateClient(new() { HubAddress = new($"https://127.0.0.1:{info.Port}/environment"),
                BearerCredential = info.BearerCredential, CertificateFingerprint = info.CertificateFingerprint, ExpectedEnvironmentId = info.EnvironmentId });
            Assert.Empty(await updates.GetHistoryAsync());
            return;
        }
        using var access = new RemoteAccessStore(Path.Combine(directory.Path, "access.db"));
        using var certificate = CreateCertificate();
        await using var remote = await RemoteEnvironmentHost.StartAsync(local.Environment, access, IPAddress.Loopback, 0, certificate);
        await using var remoteUpdates = new RemoteUpdateClient(new() { HubAddress = new(remote.Address, "/environment"), BearerCredential = access.IssueSession().Token,
            CertificateFingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256), ExpectedEnvironmentId = local.Environment.EnvironmentId });
        Assert.Empty(await remoteUpdates.GetHistoryAsync());
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Recovery tests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
    }

    private sealed class RecoveryHost : IAsyncDisposable
    {
        private WebApplication _app = null!;
        private X509Certificate2 _certificate = null!;
        internal EnvironmentService Environment { get; private set; } = null!;
        internal RemoteAccessStore Access { get; private set; } = null!;
        internal ClientRuntimeOptions Options { get; private set; } = null!;
        internal string DeviceId { get; private set; } = null!;
        internal TestOwner Owner { get; } = new();
        internal int WorkspaceProbes { get; set; }
        internal bool RedirectDescriptor { get; set; }

        internal static async Task<RecoveryHost> StartAsync(ClientTestDirectory directory, bool legacy, int maintenanceVersion = 1)
        {
            var host = new RecoveryHost();
            host.Environment = await EnvironmentService.CreateAsync(directory.CreateHostOptions());
            host.Environment.Updates.SetOwner(host.Owner);
            host.Environment.Updates.Enabled = true;
            host.Access = new(Path.Combine(directory.Path, "access.db"));
            host._certificate = CreateCertificate();
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(host._certificate)));
            builder.Services.AddTransient(_ => new EnvironmentHub(host.Environment));
            builder.Services.AddSignalR(o => { o.AddFilter<RemoteAuthorizationFilter>(); o.AddFilter(new IncompatibleFilter(host)); })
                .AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ProtocolJsonContext.Default));
            var app = host._app = builder.Build();
            app.Use(async (context, next) =>
            {
                var header = context.Request.Headers.Authorization.ToString();
                var authorization = host.Access.Authenticate(header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "");
                if (authorization is null) { context.Response.StatusCode = 401; return; }
                context.Items[RemoteAuthorizationFilter.AuthorizationItem] = authorization;
                using var revoked = authorization.Revoked.Register(context.Abort);
                if (context.Request.Path.StartsWithSegments("/updates") && authorization.Device.AccessLevel != RemoteAccessLevel.Operate)
                { context.Response.StatusCode = 403; return; }
                if (context.Request.Path == "/updates/v1/descriptor" && host.RedirectDescriptor)
                { context.Response.Redirect("/environment"); return; }
                await next(context);
            });
            app.MapHub<EnvironmentHub>("/environment");
            app.MapPost("/updates/{request}/package", host.Environment.Updates.UploadAsync);
            if (!legacy && maintenanceVersion == 1) RemoteUpdateEndpoints.Map(app, host.Environment);
            else if (!legacy) app.MapGet("/updates/v1/descriptor", () => Results.Json(new RemoteUpdateMaintenanceDescriptor(maintenanceVersion,
                host.Environment.EnvironmentId, host.Environment.Updates.Descriptor), ProtocolJsonContext.Default.RemoteUpdateMaintenanceDescriptor));
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var issued = host.Access.IssueSession();
            host.DeviceId = issued.Device.DeviceId;
            host.Options = new() { HubAddress = new(new Uri(address), "/environment"), BearerCredential = issued.Token,
                CertificateFingerprint = host._certificate.GetCertHashString(HashAlgorithmName.SHA256), ExpectedEnvironmentId = host.Environment.EnvironmentId };
            return host;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            await Environment.DisposeAsync();
            Access.Dispose();
            _certificate.Dispose();
        }
    }

    private sealed class IncompatibleFilter(RecoveryHost host) : IHubFilter
    {
        public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
        {
            var result = await next(context);
            if (result is EnvironmentDescriptor descriptor)
            {
                host.WorkspaceProbes++;
                return descriptor with { MinimumProtocolVersion = 999, MaximumProtocolVersion = 999 };
            }
            return result;
        }
    }

    private sealed class TestOwner : IRemoteUpdateOwner
    {
        public string Kind => "fixture";
        public string CurrentVersion => "1.0.0";
        public string PackageKind => ".zip";
        public string Trust => "Fixture only";
        internal TaskCompletionSource Activated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> ValidateAsync(string packagePath, string runtimeDirectory, CancellationToken cancellationToken) => Task.FromResult("1.0.1");
        public Task ActivateAsync(StagedRemoteUpdate update, CancellationToken cancellationToken)
        {
            RemoteUpdateCoordinator.CompleteActivation(update.ReceiptPath, true, "Fixture activation confirmed.");
            Activated.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
