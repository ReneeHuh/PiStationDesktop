using System.Net;
using System.Net.Sockets;
using System.Text;
using PiStation.Host.Hosting;
using PiStation.Host.Preview;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class PreviewDiscoveryIntegrationTests
{
    [Fact]
    public async Task ClientDiscoversAnHtmlLoopbackServerThroughTheAuthenticatedHost()
    {
        using var temporaryDirectory = new ClientTestDirectory();
        await using var previewServer = new LoopbackHtmlServer();
        await using var host = await EmbeddedEnvironmentHost.StartAsync(
            temporaryDirectory.CreateHostOptions());
        await using var client = new EnvironmentClient(new ClientRuntimeOptions
        {
            HubAddress = host.HubAddress,
            BearerCredential = host.BearerCredential,
        });
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new AddProjectRequest(
            temporaryDirectory.CreateDirectory("preview-project")));

        using (var httpClient = new HttpClient())
        using (var response = await httpClient.GetAsync(
            new Uri($"http://localhost:{previewServer.Port}/"),
            HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        }

        using (var probe = new PreviewPortScanner.PreviewHttpEndpointProbe())
        {
            Assert.NotNull(await probe.ProbeAsync(previewServer.Port, CancellationToken.None));
        }

        var result = await client.DiscoverProjectPreviewServersAsync(
            new DiscoverProjectPreviewServersRequest(project.ProjectId));

        Assert.Contains("preview.discover", client.Descriptor?.Capabilities ?? []);
        Assert.Equal(project.ProjectId, result.ProjectId);
        var discovered = Assert.Single(result.Servers, server => server.Port == previewServer.Port);
        Assert.Equal("http", discovered.Scheme);
        Assert.Equal(IPAddress.Loopback.ToString(), discovered.Host);
    }

    private sealed class LoopbackHtmlServer : IAsyncDisposable
    {
        private static readonly byte[] Response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            "Content-Length: 16\r\n" +
            "Connection: close\r\n\r\n" +
            "<h1>Preview</h1>");

        private readonly CancellationTokenSource _cancellation = new();
        private readonly TcpListener _listener;
        private readonly Task _listenTask;

        public LoopbackHtmlServer()
        {
            _listener = BindKnownDevelopmentPort();
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _listenTask = ListenAsync();
        }

        public int Port { get; }

        private static TcpListener BindKnownDevelopmentPort()
        {
            foreach (var port in new[] { 5175, 5500, 9000, 8081 })
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                try
                {
                    listener.Start();
                    listener.Stop();
                    return listener;
                }
                catch (SocketException)
                {
                    listener.Stop();
                }
            }

            throw new InvalidOperationException("No known development preview port was available.");
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            _listener.Stop();
            try
            {
                await _listenTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }

            _cancellation.Dispose();
        }

        private async Task ListenAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                _ = RespondAsync(client);
            }
        }

        private static async Task RespondAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[1024];
                var received = 0;
                var terminatorState = 0;
                while (received < 16 * 1024)
                {
                    var count = await stream.ReadAsync(buffer);
                    if (count == 0)
                    {
                        return;
                    }

                    received += count;
                    for (var index = 0; index < count; index++)
                    {
                        terminatorState = (terminatorState, buffer[index]) switch
                        {
                            (0, (byte)'\r') => 1,
                            (1, (byte)'\n') => 2,
                            (2, (byte)'\r') => 3,
                            (3, (byte)'\n') => 4,
                            (_, (byte)'\r') => 1,
                            _ => 0,
                        };
                        if (terminatorState == 4)
                        {
                            break;
                        }
                    }

                    if (terminatorState == 4)
                    {
                        break;
                    }
                }

                await stream.WriteAsync(Response);
            }
        }
    }
}
