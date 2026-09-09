using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiStation.Protocol;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime.Tests;

public sealed class RemoteEndpointProbeTests
{
    [Theory]
    [InlineData("protocol")]
    [InlineData("oversized")]
    [InlineData("redirect")]
    [InlineData("malformed")]
    public async Task RejectsInvalidOrRedirectedIdentityResponses(string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var key = RSA.Create(2048);
        using var created = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, options => options.UseHttps(certificate)));
        await using var app = builder.Build();
        var id = EnvironmentId.New();
        var redirected = false;
        app.MapGet(RemoteEndpointIdentity.Path, async (HttpContext context) =>
        {
            Assert.False(context.Request.Headers.ContainsKey("Authorization"));
            if (scenario == "redirect") { context.Response.Redirect("/redirected"); return; }
            context.Response.ContentType = "application/json";
            var body = scenario switch
            {
                "protocol" => JsonSerializer.Serialize(new RemoteEndpointIdentity(id, ProtocolVersion.Current + 1), ProtocolJsonContext.Default.RemoteEndpointIdentity),
                "oversized" => new string(' ', 4097),
                _ => "{broken",
            };
            await context.Response.WriteAsync(body, context.RequestAborted);
        });
        app.MapGet("/redirected", () => { redirected = true; return Results.Ok(); });
        await app.StartAsync(timeout.Token);
        try
        {
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            var operation = () => RemoteEndpointProbe.VerifyAsync(address, certificate.GetCertHashString(HashAlgorithmName.SHA256), id, timeout.Token);
            if (scenario == "redirect") await Assert.ThrowsAsync<HttpRequestException>(operation);
            else if (scenario == "malformed") await Assert.ThrowsAnyAsync<JsonException>(operation);
            else await Assert.ThrowsAsync<InvalidDataException>(operation);
            Assert.False(redirected);
        }
        finally { await app.StopAsync(timeout.Token); }
    }
}
