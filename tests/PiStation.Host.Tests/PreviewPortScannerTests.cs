using PiStation.Host.Preview;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class PreviewPortScannerTests
{
    [Fact]
    public async Task ScannerDeduplicatesOrdersAndBoundsConcurrentProbes()
    {
        var probe = new RecordingProbe([8080, 5173], TimeSpan.FromMilliseconds(10));
        using var scanner = new PreviewPortScanner(
            new StaticListenerSource([8080, 5173, 8080, 0, 70_000]),
            probe);

        var (servers, isTruncated) = await scanner.ScanAsync();

        Assert.Equal([5173, 8080], servers.Select(static server => server.Port));
        Assert.False(isTruncated);
        Assert.Equal(probe.ProbedPorts.Distinct().Count(), probe.ProbedPorts.Count);
        Assert.InRange(probe.MaximumConcurrency, 1, PreviewDiscoveryDefaults.MaximumProbeConcurrency);
    }

    [Fact]
    public async Task ScannerMarksCandidateAndResultLimitsAsTruncated()
    {
        var ports = Enumerable.Range(1, PreviewDiscoveryDefaults.MaximumCandidatePorts + 20).ToArray();
        using var scanner = new PreviewPortScanner(
            new StaticListenerSource(ports),
            new RecordingProbe(ports, TimeSpan.Zero));

        var (servers, isTruncated) = await scanner.ScanAsync();

        Assert.Equal(PreviewDiscoveryDefaults.MaximumResults, servers.Count);
        Assert.True(isTruncated);
        Assert.Equal(
            servers.OrderBy(static server => server.Port).Select(static server => server.Port),
            servers.Select(static server => server.Port));
    }

    private sealed class StaticListenerSource(IReadOnlyCollection<int> ports) : IPreviewListenerSource
    {
        public IReadOnlyCollection<int> GetListeningPorts() => ports;
    }

    private sealed class RecordingProbe(IReadOnlyCollection<int> availablePorts, TimeSpan delay)
        : IPreviewEndpointProbe
    {
        private readonly HashSet<int> _availablePorts = [.. availablePorts];
        private int _active;
        private int _maximumConcurrency;

        public List<int> ProbedPorts { get; } = [];

        public int MaximumConcurrency => _maximumConcurrency;

        public async Task<DiscoveredPreviewServer?> ProbeAsync(
            int port,
            CancellationToken cancellationToken)
        {
            lock (ProbedPorts)
            {
                ProbedPorts.Add(port);
            }

            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                return _availablePorts.Contains(port)
                    ? new DiscoveredPreviewServer(
                        $"http://localhost:{port}/",
                        "localhost",
                        port,
                        "http")
                    : null;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrency);
                if (active <= current ||
                    Interlocked.CompareExchange(ref _maximumConcurrency, active, current) == current)
                {
                    return;
                }
            }
        }
    }
}
