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
    public async Task AuthenticatedDiscoveryAssociatesATerminalChildServerWithItsThread()
    {
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = timeout.Token;
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(), cancellationToken: token);
        await using var client = new EnvironmentClient(new ClientRuntimeOptions { HubAddress = host.HubAddress, BearerCredential = host.BearerCredential });
        await client.ConnectAsync(token);
        var root = directory.CreateDirectory("owned-preview");
        var project = await client.AddProjectAsync(new AddProjectRequest(root), token);
        var thread = await client.CreateThreadAsync(new(project.ProjectId, "Preview ownership"), token);
        await File.WriteAllTextAsync(Path.Combine(root, "preview-server.ps1"), """
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
            $listener.Start()
            [System.IO.File]::WriteAllText((Join-Path $PWD 'port.txt'), [string]$listener.LocalEndpoint.Port)
            try {
                while ($true) {
                    $client = $listener.AcceptTcpClient()
                    try {
                        $stream = $client.GetStream()
                        $buffer = [byte[]]::new(8192)
                        $null = $stream.Read($buffer, 0, $buffer.Length)
                        $response = [System.Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Type: text/html`r`nContent-Length: 2`r`nConnection: close`r`n`r`nOK")
                        $stream.Write($response, 0, $response.Length)
                    } finally { $client.Dispose() }
                }
            } finally { $listener.Stop() }
            """, token);
        var terminal = await client.StartTerminalSessionAsync(new(project.ProjectId, TerminalShellKind.CommandPrompt, ThreadId: thread.ThreadId), token);
        try
        {
            await client.WriteTerminalInputAsync(new(terminal.TerminalSessionId,
                "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\\preview-server.ps1\r"), token);
            var portPath = Path.Combine(root, "port.txt");
            var port = 0;
            while (!File.Exists(portPath) || !int.TryParse(await File.ReadAllTextAsync(portPath, token), out port)) await Task.Delay(50, token);
            var result = await client.DiscoverProjectPreviewServersAsync(new(project.ProjectId, thread.ThreadId), token);
            var server = Assert.Single(result.Servers, item => item.Port == port);
            Assert.NotNull(server.Terminal);
            Assert.Equal(terminal.TerminalSessionId, server.Terminal.TerminalSessionId);
            Assert.Equal(thread.ThreadId, server.Terminal.ThreadId);
            Assert.Equal(project.ProjectId, server.Terminal.ProjectId);
            Assert.True(server.ProcessId > 0);
            Assert.NotEqual(Environment.ProcessId, server.ProcessId);
            var otherProject = await client.AddProjectAsync(new(directory.CreateDirectory("other-preview-project")), token);
            await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() =>
                client.DiscoverProjectPreviewServersAsync(new(otherProject.ProjectId, thread.ThreadId), token));
        }
        finally { await client.CloseTerminalSessionAsync(new(terminal.TerminalSessionId), CancellationToken.None); }
    }

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
        Assert.Equal(Environment.ProcessId, discovered.ProcessId);
        Assert.Null(discovered.Terminal);
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
