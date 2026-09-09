using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

public sealed record TailscaleMachine(string Name, string? DnsName, string Address, bool Online)
{
    public string Label => $"{Name} · {(Online ? "online" : "offline")} · {DnsName ?? Address}";
    public Uri GetHttpsAddress(int port)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "Use the PiStation host's sharing port (1024–65535).");
        if (!TailscaleDiscovery.IsTailscaleAddress(Address)) throw new ArgumentException("This is not a Tailscale IPv4 address.");
        var address = new UriBuilder(Uri.UriSchemeHttps, TailscaleDiscovery.NormalizeDnsName(DnsName) ?? Address, port).Uri;
        RemoteEndpoint.Validate(address);
        return address;
    }
}

public sealed record TailscaleNetwork(bool Running, TailscaleMachine? Self, IReadOnlyList<TailscaleMachine> Peers, string Message);

/// <summary>Reads the user's existing Windows Tailscale connection; never signs in or changes network configuration.</summary>
public static class TailscaleDiscovery
{
    private const int MaximumStatusCharacters = 4 * 1024 * 1024;

    public static async Task<TailscaleNetwork> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var executable = FindExecutable();
        if (executable is null) return new(false, null, [], "Tailscale was not found. Install and connect Tailscale on each computer, then refresh.");
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("status"); start.ArgumentList.Add("--json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            using var registration = timeout.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            });
            var output = ReadBoundedAsync(process.StandardOutput, MaximumStatusCharacters, timeout);
            var errors = ReadBoundedAsync(process.StandardError, 32 * 1024, timeout);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0) return new(false, null, [], "Tailscale status is unavailable. Check the Tailscale Windows app and service, then refresh.");
            return Parse(await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, null, [], "Tailscale did not respond in time. Check its Windows app and service, then refresh."); }
        catch (Exception error) when (error is JsonException or InvalidDataException or System.ComponentModel.Win32Exception or IOException)
        { return new(false, null, [], "Tailscale status could not be read. Check its Windows app and service, then refresh."); }
    }

    // https://tailscale.com/docs/reference/tailscale-cli — status --json is the machine-readable CLI surface.
    internal static TailscaleNetwork Parse(string json)
    {
        if (json.Length > MaximumStatusCharacters) throw new InvalidDataException("Tailscale status exceeds its size limit.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Tailscale status must be an object.");
        var state = Text(root, "BackendState");
        if (state != "Running") return new(false, null, [], state == "NeedsLogin"
            ? "Connect Tailscale using its Windows app, then refresh." : "Tailscale is stopped or starting. Connect it using its Windows app, then refresh.");
        var self = root.TryGetProperty("Self", out var selfJson) ? ReadMachine(selfJson, requireWindows: false) : null;
        var peers = new List<TailscaleMachine>();
        if (root.TryGetProperty("Peer", out var peersJson) && peersJson.ValueKind == JsonValueKind.Object)
        {
            foreach (var peer in peersJson.EnumerateObject().Take(2048))
                if (ReadMachine(peer.Value, requireWindows: true) is { } machine) peers.Add(machine);
        }
        return new(true, self, peers.DistinctBy(peer => peer.Address).OrderByDescending(peer => peer.Online)
            .ThenBy(peer => peer.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            self is null ? "Tailscale is connected but has no usable IPv4 address. Refresh after the connection finishes."
            : $"Connected as {self.DnsName ?? self.Name} ({self.Address}). PiStation uses HTTPS with its paired host certificate over this network.");
    }

    public static bool IsTailscaleAddress(string value)
    {
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    internal static string? NormalizeDnsName(string? name)
    {
        name = name?.TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(name) || name.Length > 253 || !name.EndsWith(".ts.net", StringComparison.Ordinal) ||
            name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-' && character != '.') ||
            name.Split('.').Any(label => label.Length is < 1 or > 63 || label.StartsWith('-') || label.EndsWith('-')))
            return null;
        return name;
    }

    private static TailscaleMachine? ReadMachine(JsonElement element, bool requireWindows)
    {
        if (element.ValueKind != JsonValueKind.Object || requireWindows && !string.Equals(Text(element, "OS"), "windows", StringComparison.OrdinalIgnoreCase)) return null;
        if (!element.TryGetProperty("TailscaleIPs", out var ips) || ips.ValueKind != JsonValueKind.Array) return null;
        var address = ips.EnumerateArray().Where(ip => ip.ValueKind == JsonValueKind.String).Select(ip => ip.GetString()!)
            .FirstOrDefault(IsTailscaleAddress);
        if (address is null) return null;
        var dns = NormalizeDnsName(Text(element, "DNSName"));
        var name = new string((Text(element, "HostName") ?? dns ?? address).Where(character => !char.IsControl(character)).Take(100).ToArray());
        var online = !requireWindows || element.TryGetProperty("Online", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new(name, dns, address, online);
    }

    private static string? Text(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? FindExecutable()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        if (File.Exists(installed)) return installed;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = directory.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(path)) continue;
            var candidate = Path.Combine(path, "tailscale.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum, CancellationTokenSource cancellation)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellation.Token).ConfigureAwait(false);
            if (read == 0) return text.ToString();
            if (text.Length + read > maximum)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                throw new InvalidDataException("Tailscale output exceeds its size limit.");
            }
            text.Append(buffer, 0, read);
        }
    }
}
