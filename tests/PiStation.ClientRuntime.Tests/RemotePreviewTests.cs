using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiStation.Host.Hosting;
using PiStation.Host.Security;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class RemotePreviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostLoopbackPreviewForwardsHttpEventsAndWebSocketsThroughAuthenticatedTransport(bool sshListener)
    {
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var options = directory.CreateHostOptions();
        await using var local = await EmbeddedEnvironmentHost.StartAsync(options, cancellationToken: timeout.Token);
        using var access = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"));
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=preview-fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var createdCertificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(createdCertificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
        var credential = RemoteAccessStore.NewSecret();
        access.Approve(access.BeginPairing(new(access.CreateInvitation(RemoteAccessLevel.Operate), "Preview", credential)).RequestId);
        await using var remote = await RemoteEnvironmentHost.StartAsync(local.Environment, access, IPAddress.Loopback, 0, certificate, cancellationToken: timeout.Token);
        var clientOptions = new ClientRuntimeOptions
        {
            HubAddress = new(remote.Address, "/environment"), BearerCredential = credential,
            CertificateFingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256),
        };
        if (sshListener && OperatingSystem.IsWindows())
        {
            var info = await SshEnvironmentHost.TryDiscoverAsync(options.CanonicalDataRoot, timeout.Token);
            Assert.NotNull(info);
            clientOptions = new()
            {
                HubAddress = new Uri($"https://127.0.0.1:{info.Port}/environment"),
                BearerCredential = info.BearerCredential, CertificateFingerprint = info.CertificateFingerprint,
            };
        }
        await using var client = new EnvironmentClient(clientOptions);
        await client.ConnectAsync(timeout.Token);
        var project = await client.AddProjectAsync(new(directory.CreateDirectory("project")), timeout.Token);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var site = builder.Build();
        site.UseWebSockets();
        site.MapGet("/", async context =>
        {
            Assert.DoesNotContain("__pistation_preview_", context.Request.Headers.Cookie.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(credential, context.Request.Headers.Authorization.ToString(), StringComparison.Ordinal);
            context.Response.Headers.SetCookie = "app=session; Path=/; HttpOnly";
            await context.Response.WriteAsync("<script src='/app.js'></script>", context.RequestAborted);
        });
        site.MapGet("/app.js", () => "console.log('preview');");
        site.MapPost("/form", async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            await context.Response.WriteAsync(await reader.ReadToEndAsync(context.RequestAborted), context.RequestAborted);
        });
        site.MapGet("/events", async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: live\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        });
        site.MapGet("/socket", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var bytes = new byte[128];
            var read = await socket.ReceiveAsync(bytes.AsMemory(), context.RequestAborted);
            await socket.SendAsync(bytes.AsMemory(0, read.Count), WebSocketMessageType.Text, true, context.RequestAborted);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
        });
        await site.StartAsync(timeout.Token);
        var siteAddress = new Uri(site.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        await using var proxy = await client.OpenRemotePreviewAsync(new(project.ProjectId, siteAddress), timeout.Token);
        using var handler = new SocketsHttpHandler
        {
            UseCookies = false, UseProxy = false, AllowAutoRedirect = false,
            ConnectCallback = async (context, token) =>
            {
                var tcp = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try { await tcp.ConnectAsync(IPAddress.Loopback, context.DnsEndPoint.Port, token); return new NetworkStream(tcp, ownsSocket: true); }
                catch { tcp.Dispose(); throw; }
            },
        };
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = proxy.Address };
        using (var denied = await http.GetAsync("/", timeout.Token)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        http.DefaultRequestHeaders.Add("Cookie", proxy.CookieName + "=" + proxy.CookieValue);
        using (var page = await http.GetAsync("/", timeout.Token))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("/app.js", await page.Content.ReadAsStringAsync(timeout.Token), StringComparison.Ordinal);
            Assert.Contains(page.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("app=", StringComparison.Ordinal));
        }
        Assert.Contains("preview", await http.GetStringAsync("/app.js", timeout.Token), StringComparison.Ordinal);
        using (var form = await http.PostAsync("/form", new StringContent("submitted"), timeout.Token))
            Assert.Equal("submitted", await form.Content.ReadAsStringAsync(timeout.Token));
        Assert.Equal("data: live\n\n", await http.GetStringAsync("/events", timeout.Token));
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var browserSocket = new ClientWebSocket();
        browserSocket.Options.SetRequestHeader("Cookie", proxy.CookieName + "=" + proxy.CookieValue);
        browserSocket.Options.SetRequestHeader("Origin", proxy.Address.GetLeftPart(UriPartial.Authority));
        await browserSocket.ConnectAsync(new UriBuilder(proxy.Address) { Scheme = "ws", Path = "/socket" }.Uri, invoker, timeout.Token);
        await browserSocket.SendAsync(Encoding.UTF8.GetBytes("reload").AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        var reply = new byte[128];
        var received = await browserSocket.ReceiveAsync(reply.AsMemory(), timeout.Token);
        Assert.Equal("reload", Encoding.UTF8.GetString(reply, 0, received.Count));
        await Assert.ThrowsAnyAsync<Exception>(() => client.OpenRemotePreviewAsync(new(project.ProjectId, remote.Address), timeout.Token));
        await Assert.ThrowsAnyAsync<Exception>(() => client.OpenRemotePreviewAsync(new(project.ProjectId, new Uri("http://example.com/")), timeout.Token));
        await client.DisconnectAsync(timeout.Token);
        using (var denied = await http.GetAsync("/", timeout.Token)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await client.ConnectAsync(timeout.Token);
        Assert.Contains("preview", await http.GetStringAsync("/app.js", timeout.Token), StringComparison.Ordinal);
        await proxy.DisposeAsync();
        await site.StopAsync(timeout.Token);
    }
}
