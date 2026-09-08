using System.Net;
using System.Text;
using System.Text.Json;
using PiStation.Host;
using PiStation.Host.Hosting;
using PiStation.PiRpc.Discovery;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Server;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal) || args[0] == "help")
        {
            Console.WriteLine(RemoteAuthCli.Help);
            return 0;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The PiStation server currently requires Windows.");
            return 1;
        }
        using var lifetime = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
        try
        {
            if (args[0] == "update-manifest")
            {
                Console.WriteLine(JsonSerializer.Serialize(new { version = typeof(EnvironmentService).Assembly.GetName().Version!.ToString(),
                    protocolVersion = PiStation.Protocol.ProtocolVersion.Current, platform = "win-x64", databaseCompatibilityVersion = 1, startupWriteGateVersion = 1 }));
                return 0;
            }
            if (args[0] == "supervise") return await ServerUpdateLauncher.RunAsync(args, lifetime.Token).ConfigureAwait(false);
            if (args[0] is "pair" or "auth" or "status")
            {
                Console.OutputEncoding = new UTF8Encoding(false);
                return await RemoteAuthCli.RunAsync(args, Console.Out, Console.Error, lifetime.Token).ConfigureAwait(false);
            }
            var attach = args[0] == "attach";
            if (!attach && args[0] != "serve") throw new ArgumentException("Unknown command. See --help.");
            string? root = null;
            string? piPath = null;
            string? ownerDirectory = null;
            bool enableUpdates = false;
            IPAddress? sharingAddress = null;
            int sharingPort = 52740;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length) throw new ArgumentException("A host option is missing its value.");
                var key = args[i] == "--base-dir" ? "--data-root" : args[i];
                if (!seen.Add(key)) throw new ArgumentException("Duplicate host option.");
                switch (key)
                {
                    case "--data-root": root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(args[i + 1])); break;
                    case "--pi-executable": piPath = args[i + 1]; break;
                    case "--owner-directory" when !attach: ownerDirectory = Path.GetFullPath(args[i + 1]); break;
                    case "--enable-remote-updates" when !attach: enableUpdates = bool.Parse(args[i + 1]); break;
                    case "--host" when !attach:
                        if (!IPAddress.TryParse(args[i + 1], out sharingAddress) || sharingAddress.Equals(IPAddress.Any) ||
                            sharingAddress.Equals(IPAddress.IPv6Any) || sharingAddress.IsIPv6Multicast)
                            throw new ArgumentException("--host requires a specific local IP address, not a wildcard.");
                        break;
                    case "--port" when !attach:
                        if (!int.TryParse(args[i + 1], out sharingPort) || sharingPort is < 1024 or > 65535)
                            throw new ArgumentException("--port must be between 1024 and 65535.");
                        break;
                    default: throw new ArgumentException("Unknown host option. See --help.");
                }
            }
            if (seen.Contains("--port") && sharingAddress is null) throw new ArgumentException("--port requires --host.");
            root ??= HostOptions.DefaultDataRoot;
            // EOF or an explicit stop ends an owned host. A reused host is never stopped here.
            var inputEnded = attach || ownerDirectory is not null ? WatchInputAsync(lifetime) : Task.CompletedTask;
            if (attach && await SshEnvironmentHost.TryDiscoverAsync(root, lifetime.Token).ConfigureAwait(false) is { } existing)
            {
                await WriteInfoAsync(existing).ConfigureAwait(false);
                await inputEnded.ConfigureAwait(false);
                return 0;
            }
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(60));
            var configuration = PiRuntimeSettingsStore.Load(root);
            var pi = await new PiLocator().LocateAsync(new PiLocatorOptions { ExplicitPiPath = piPath ?? configuration.ExecutablePath }, startup.Token).ConfigureAwait(false);
            SshEnvironmentHost? host = null;
            try
            {
                host = await SshEnvironmentHost.StartAsync(CreateHostOptions(root, pi, configuration), diagnosticLog: message => Console.Error.WriteLine(message), sharingAddress: sharingAddress, sharingPort: sharingPort, cancellationToken: startup.Token).ConfigureAwait(false);
            }
            catch (IOException) when (attach)
            {
                // Another attach may win startup. Only reuse through the authenticated pipe,
                // never from stale PID/port files and never kill the other process.
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    if (await SshEnvironmentHost.TryDiscoverAsync(root, startup.Token).ConfigureAwait(false) is { } winner)
                    {
                        await WriteInfoAsync(winner).ConfigureAwait(false);
                        await inputEnded.ConfigureAwait(false);
                        return 0;
                    }
                    await Task.Delay(200, startup.Token).ConfigureAwait(false);
                }
                throw;
            }
            await using (host.ConfigureAwait(false))
            {
                if (ownerDirectory is not null)
                {
                    if (ownerDirectory != Path.GetFullPath(Path.Combine(root, "update-owner")))
                        throw new InvalidDataException("The update owner directory must belong to this environment.");
                    host.Environment.Updates.SetOwner(new PiStation.Host.Updates.StandaloneUpdateOwner(ownerDirectory, lifetime.Cancel));
                }
                if (enableUpdates) host.Environment.Updates.Enabled = true;
                if (attach)
                {
                    await WriteInfoAsync(host.Info with { StartedByConnection = true }).ConfigureAwait(false);
                    await inputEnded.ConfigureAwait(false);
                }
                else
                {
                    Console.WriteLine($"PiStation SSH host ready: {host.Info.Name}. Keep this process running; Ctrl+C stops it.");
                    if (sharingAddress is not null) Console.WriteLine($"LAN/VPN sharing: {host.Info.PairingAddress}. Run 'pair' in another terminal to create a link.");
                    await Task.Delay(Timeout.Infinite, lifetime.Token).ConfigureAwait(false);
                }
            }
            return 0;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return 0; }
        catch (Exception exception)
        {
            // Do not print objects, command lines or handshake data containing credentials.
            Console.Error.WriteLine($"PiStation command failed: {exception.Message}");
            return 1;
        }
    }

    internal static HostOptions CreateHostOptions(string root, PiInstallation pi, PiRuntimeConfiguration configuration) => new()
    {
        ApplicationDataRoot = root,
        EnvironmentName = Environment.MachineName,
        PiInstallation = pi,
        Extensions = configuration.Extensions,
        LaunchConfiguration = configuration.Launch ?? new(),
        PlanExtensionPath = Path.Combine(AppContext.BaseDirectory, "PiExtensions", "pistation-plan.ts"),
        AgentExtensionPath = Path.Combine(AppContext.BaseDirectory, "PiExtensions", "pistation-agents.ts"),
        ManagementExtensionPath = Path.Combine(AppContext.BaseDirectory, "PiExtensions", "pistation-resources.ts"),
    };

    private static async Task WriteInfoAsync(SshHostInfo info)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        await Console.Out.WriteLineAsync("PISTATION_SSH " + JsonSerializer.Serialize(info, ProtocolJsonContext.Default.SshHostInfo)).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WatchInputAsync(CancellationTokenSource lifetime)
    {
        try
        {
            // StreamReader.ReadLineAsync on a console may block before returning a Task.
            await Task.Run(async () =>
            {
                while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
                    if (line == "stop") break;
            }).ConfigureAwait(false);
        }
        finally { await lifetime.CancelAsync().ConfigureAwait(false); }
    }
}
