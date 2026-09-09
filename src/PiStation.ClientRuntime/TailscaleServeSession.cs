using System.Globalization;
using PiStation.Protocol.Identifiers;

namespace PiStation.ClientRuntime;

/// <summary>Forwards PiStation HTTPS unchanged, retaining the invitation's certificate pin.</summary>
public sealed class TailscaleServeSession : IAsyncDisposable
{
    private readonly ITailscaleServeProcess _process;
    private static readonly string[] StatusArguments = ["serve", "status", "--json"];

    private TailscaleServeSession(Uri address, ITailscaleServeProcess process) { Address = address; _process = process; }
    public Uri Address { get; }
    public bool IsRunning => !_process.HasExited;

    public static Task<TailscaleServeSession> StartAsync(TailscaleMachine machine, int localPort, int servePort,
        string fingerprint, EnvironmentId environmentId, CancellationToken cancellationToken = default) =>
        StartAsync(machine, localPort, servePort, TailscaleServeProcess.QueryAsync,
            arguments => new TailscaleServeProcess(arguments),
            (address, token) => RemoteEndpointProbe.VerifyAsync(address, fingerprint, environmentId, token), cancellationToken);

    internal static async Task<TailscaleServeSession> StartAsync(TailscaleMachine machine, int localPort, int servePort,
        Func<IReadOnlyList<string>, CancellationToken, Task<string>> query, Func<IReadOnlyList<string>, ITailscaleServeProcess> start,
        Func<Uri, CancellationToken, Task> probe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentOutOfRangeException.ThrowIfLessThan(localPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(localPort, 65535);
        if (!machine.Online || TailscaleDiscovery.NormalizeDnsName(machine.DnsName) is null)
            throw new InvalidOperationException("Connect Tailscale and enable MagicDNS for this computer, then refresh.");
        var address = machine.GetHttpsAddress(servePort);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        TailscaleServeConfiguration.EnsurePortAvailable(await query(StatusArguments, deadline.Token).ConfigureAwait(false), servePort);
        // Deliberately omit --bg, --https, --yes, and Funnel. Raw forwarding preserves
        // TLS end to end; the foreground lease removes only this app's mapping on exit.
        deadline.Token.ThrowIfCancellationRequested();
        var process = start(["serve", "--tcp=" + servePort.ToString(CultureInfo.InvariantCulture),
            "tcp://127.0.0.1:" + localPort.ToString(CultureInfo.InvariantCulture)]);
        var ready = false;
        try
        {
            while (true)
            {
                if (process.HasExited) throw new InvalidOperationException("Tailscale Serve stopped. Check its Windows app and service, then try again.");
                if (TailscaleServeConfiguration.HasForward(await query(StatusArguments, deadline.Token).ConfigureAwait(false), servePort, localPort))
                {
                    try
                    {
                        await probe(address, deadline.Token).ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        if (process.HasExited) throw new InvalidOperationException("Tailscale Serve stopped during the endpoint check.");
                        ready = true;
                        return new(address, process);
                    }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                }
                await Task.Delay(300, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Tailscale sharing could not be verified. Check MagicDNS, the port, and connectivity, then try again."); }
        finally { if (!ready) await process.DisposeAsync().ConfigureAwait(false); }
    }

    public ValueTask DisposeAsync() => _process.DisposeAsync();
}
