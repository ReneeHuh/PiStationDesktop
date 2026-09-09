using System.Net;
using System.Net.Sockets;
using PiStation.Host.Persistence;
using PiStation.Host.Preview;
using PiStation.Host.Projects;
using PiStation.Host.Terminals;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class PreviewOwnershipTests
{
    [Fact]
    public void DescendantsIncludeGrandchildrenBoundCyclesAndRejectIdleOrMissingRoots()
    {
        TerminalProcessEntry[] entries = [new(10, 31, "cmd"), new(30, 10, "npm"), new(31, 30, "node"), new(40, 1, "other")];
        Assert.Equal([10, 30, 31], TerminalProcessInspector.Descendants(entries, 10).Order());
        Assert.Empty(TerminalProcessInspector.Descendants(entries, 40));
        Assert.Empty(TerminalProcessInspector.Descendants(entries, 99));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeListenerTableReportsTheActualOwningProcess(bool ipv6)
    {
        if (ipv6 && !Socket.OSSupportsIPv6) return;
        using var listener = new TcpListener(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var item = Assert.Single(new WindowsPreviewListenerSource().GetListeners(), item => item.Port == port);
        Assert.Equal(Environment.ProcessId, item.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(item.ProcessName));
        Assert.Equal(ipv6 ? "::1" : "127.0.0.1", item.Host);
    }

    [Fact]
    public async Task OwnershipRefreshesIndependentlyOfCachedProbesAndPidChangesInvalidateCache()
    {
        var owner = new PreviewTerminalOwner(TerminalSessionId.New(), ProjectId.New(), ThreadId.New(), "Terminal 1");
        var source = new ListenerSource { Listeners = [new(5173, 31, "node"), new(8080, 40, "other")] };
        var owners = new Dictionary<int, PreviewTerminalOwner> { [31] = owner };
        var probe = new Probe();
        using var scanner = new PreviewPortScanner(source, probe, () => owners);
        var first = (await scanner.ScanAsync()).Servers;
        Assert.Equal(owner, Assert.Single(first, item => item.Port == 5173).Terminal);
        Assert.Null(Assert.Single(first, item => item.Port == 8080).Terminal);
        var count = probe.Count;
        owners.Clear();
        Assert.All((await scanner.ScanAsync()).Servers, item => Assert.Null(item.Terminal));
        Assert.Equal(count, probe.Count);
        source.Listeners = [new(5173, 99, "replacement"), new(8080, 40, "other")];
        var replaced = Assert.Single((await scanner.ScanAsync()).Servers, item => item.Port == 5173);
        Assert.Equal(99, replaced.ProcessId);
        Assert.Equal(count + 1, probe.Count);
    }

    [Fact]
    public async Task ClosingATerminalRemovesItsLiveProcessOwnership()
    {
        using var directory = new HostTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        var options = directory.CreateOptions();
        var database = new HostDatabase(options);
        await database.InitializeAsync(token);
        var project = await new ProjectService(database).AddAsync(new(directory.CreateDirectory("project")), token);
        await using var registry = new TerminalSessionRegistry(database, options);
        var terminal = await registry.StartAsync(new(project.ProjectId, TerminalShellKind.CommandPrompt), token);
        await registry.WriteAsync(new(terminal.TerminalSessionId, "ping -n 20 127.0.0.1\r"), token);
        IReadOnlyDictionary<int, PreviewTerminalOwner> owners;
        while ((owners = registry.GetPreviewProcessOwners()).Count == 0) await Task.Delay(50, token);
        Assert.All(owners.Values, owner => Assert.Equal(terminal.TerminalSessionId, owner.TerminalSessionId));
        await registry.CloseAsync(new(terminal.TerminalSessionId), token);
        Assert.Empty(registry.GetPreviewProcessOwners());
    }

    [Theory]
    [InlineData("localhost", false)]
    [InlineData("127.0.0.1", true)]
    public async Task RedirectOwnershipRequiresAKnownFinalEndpoint(string host, bool known)
    {
        var owner = new PreviewTerminalOwner(TerminalSessionId.New(), ProjectId.New(), ThreadId.New(), "Destination terminal");
        using var scanner = new PreviewPortScanner(new ListenerSource { Listeners = [new(5173, 30), new(8080, 40)] },
            new RedirectProbe(host), () => new Dictionary<int, PreviewTerminalOwner> { [40] = owner });
        var server = Assert.Single((await scanner.ScanAsync()).Servers);
        Assert.Equal(8080, server.Port);
        Assert.Equal(known ? owner : null, server.Terminal);
        Assert.Equal(known ? 40 : (int?)null, server.ProcessId);
    }

    [Fact]
    public async Task FailedOwnershipInspectionKeepsDiscoveryAvailableWithoutInventingOwners()
    {
        using var scanner = new PreviewPortScanner(new ListenerSource { Listeners = [new(5173, 31, "node")] },
            new Probe(), () => throw new System.ComponentModel.Win32Exception());
        var server = Assert.Single((await scanner.ScanAsync()).Servers, item => item.Port == 5173);
        Assert.Equal(31, server.ProcessId);
        Assert.Null(server.Terminal);
    }

    private sealed class ListenerSource : IPreviewListenerSource
    {
        public IReadOnlyCollection<PreviewListener> Listeners { get; set; } = [];
        public IReadOnlyCollection<int> GetListeningPorts() => Listeners.Select(item => item.Port).ToArray();
        public IReadOnlyCollection<PreviewListener> GetListeners() => Listeners;
    }
    private sealed class RedirectProbe(string host) : IPreviewEndpointProbe
    {
        public Task<DiscoveredPreviewServer?> ProbeAsync(int port, CancellationToken cancellationToken) =>
            Task.FromResult(port == 5173 ? new DiscoveredPreviewServer($"http://{host}:8080/", host, 8080, "http") : null);
    }

    private sealed class Probe : IPreviewEndpointProbe
    {
        public int Count;
        public Task<DiscoveredPreviewServer?> ProbeAsync(int port, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count);
            return Task.FromResult(port is 5173 or 8080 ? new DiscoveredPreviewServer($"http://127.0.0.1:{port}/", "127.0.0.1", port, "http") : null);
        }
    }
}
