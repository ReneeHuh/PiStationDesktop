using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using PiStation.Host;
using PiStation.Host.Hosting;
using PiStation.Host.Security;
using PiStation.Protocol.Models;
using QRCoder;

namespace PiStation.Server;

internal static partial class RemoteAuthCli
{
    internal const string Help = """
        PiStation.Server (Windows)
          serve [--data-root PATH] [--pi-executable PATH] [--host IP --port 52740]
          attach [--data-root PATH] [--pi-executable PATH]
          status [--data-root PATH] [--json]
          pair [--ttl 5m] [--label NAME] [--access read-only|operate] [--no-qr] [--json]
          auth pairing create [--ttl 5m] [--label NAME] [--access read-only|operate] [--json]
          auth pairing list|pending [--json]
          auth pairing revoke|reject ID [--json]
          auth pairing approve ID --code SIX_DIGITS [--json]
          auth session issue [--ttl 30d] [--label NAME] [--subject TEXT]
                             [--access read-only|operate] [--json|--token-only]
          auth session list [--json]
          auth session revoke ID [--json]

        All commands accept --data-root PATH (--base-dir is an alias).
        Pairing creation accepts --base-url HTTPS_ORIGIN and --certificate SHA256.
        pair requires a running host; auth can administer its database while offline.
        Compare the client's verification code before approving a pending request.
        Pairing TTL: 1 second–1 day (default 5 minutes); session TTL: 1 second–180 days
        (default 30 days). Approved pairings last 180 days. Re-pair after expiry.
        Tokens are secrets, printed once on creation; lists never include tokens/hashes.
        serve defaults to loopback-only; --host explicitly enables LAN/VPN sharing.
        No firewall, Windows service, SSH daemon, or Tailscale configuration is changed.
        attach stops only a host it starts when stdin closes or receives 'stop'.
        """;

    [SupportedOSPlatform("windows")]
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var options = Options.Parse(args);
        var root = options.Root;
        SshHostInfo? host = null;
        if (options.Command is "pair" or "auth pairing create" or "status")
        {
            host = await SshEnvironmentHost.TryDiscoverAsync(root, cancellationToken).ConfigureAwait(false);
            if (host is not null) await ProbeAsync(host, cancellationToken).ConfigureAwait(false);
        }
        if (options.Command == "status")
        {
            var status = new HostStatus(host is not null, root, host?.EnvironmentId.Value, host?.Name, host?.HostKind,
                host?.ServerVersion, host?.PairingAddress, host?.PairingCertificateFingerprint);
            if (options.Json) WriteJson(output, status);
            else output.WriteLine(host is null ? $"No running host for {root}." :
                $"Running {host.HostKind}: {host.Name}\nVersion: {host.ServerVersion ?? "unknown"}\nData root: {root}\nPairing address: {host.PairingAddress?.AbsoluteUri ?? "unavailable; update host"}");
            return 0;
        }
        if (options.Command == "pair" && host is null)
            throw new InvalidOperationException("Start the PiStation desktop or 'serve' with this data root before using pair.");

        // Resolve the endpoint BEFORE creating a grant, so invalid options leave no credential behind.
        (Uri Address, string Fingerprint)? endpoint = null;
        if (options.Command is "pair" or "auth pairing create")
        {
            var baseUrl = options.Get("--base-url");
            var pin = options.Get("--certificate");
            if (baseUrl is not null)
            {
                var address = new Uri(baseUrl, UriKind.Absolute);
                RemoteEndpoint.Validate(address);
                pin ??= host?.PairingCertificateFingerprint;
                if (pin is null) throw new ArgumentException("Offline --base-url requires --certificate with the server's SHA-256 certificate fingerprint.");
                RemoteEndpoint.ValidateFingerprint(pin);
                endpoint = (address, pin.ToUpperInvariant());
            }
            else
            {
                if (pin is not null) throw new ArgumentException("--certificate requires --base-url.");
                if (host?.PairingAddress is { } address && host.PairingCertificateFingerprint is { } fingerprint)
                    endpoint = (address, fingerprint);
                else if (host is not null)
                    throw new InvalidOperationException("Update and restart the running host to enable CLI pairing discovery.");
            }
        }

        using var store = new RemoteAccessStore(Path.Combine(root, "remote-access.db"));
        switch (options.Command)
        {
            case "pair":
            case "auth pairing create":
            {
                var issued = store.IssueInvitation(options.Access, options.Ttl, options.Get("--label"));
                var url = endpoint is { } target ? new RemoteInvitation(target.Address, target.Fingerprint, issued.Token).Encode() : null;
                var result = new PairingOutput(issued.Invitation.Id, issued.Invitation.Label, issued.Invitation.AccessLevel,
                    issued.Invitation.ExpiresAt, issued.Token, url);
                if (options.Json) WriteJson(output, result);
                else
                {
                    output.WriteLine($"Pairing: {result.Id}\nAccess: {result.AccessLevel}\nExpires: {result.ExpiresAt:O}");
                    if (result.Label is not null) output.WriteLine($"Label: {result.Label}");
                    output.WriteLine(url ?? $"Token: {result.Token}");
                    if (options.Command == "pair" && !options.Has("--no-qr") && url is not null)
                    {
                        using var data = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.M);
                        using var qr = new AsciiQRCode(data);
                        output.WriteLine(qr.GetGraphic(1));
                    }
                    output.WriteLine("On the client, paste the link into Remote Connections. Compare its code, then approve on the host.");
                    if (url is null) output.WriteLine("No running host: supply --base-url and --certificate to produce a pairing link.");
                }
                if (endpoint?.Address.IsLoopback == true)
                    error.WriteLine("This link uses loopback. Another machine needs an HTTPS tunnel, or enable LAN/VPN sharing and create a new link.");
                if (!options.Json) output.WriteLine("Headless approval: auth pairing pending; auth pairing approve REQUEST_ID --code CLIENT_CODE (use the same --data-root).");
                return 0;
            }
            case "auth pairing list":
            {
                var invitations = store.ListInvitations().ToArray();
                if (options.Json) WriteJson(output, invitations);
                else
                {
                    foreach (var item in invitations) output.WriteLine($"{item.Id}  {item.AccessLevel}  {item.ExpiresAt:O}  {item.Label ?? "(unlabelled)"}");
                    if (invitations.Length == 0) output.WriteLine("No unused pairing invitations.");
                }
                return 0;
            }
            case "auth pairing pending":
            {
                var pending = store.ListPending().ToArray();
                if (options.Json) WriteJson(output, pending);
                else
                {
                    foreach (var item in pending) output.WriteLine($"{item.RequestId}  {item.DeviceName}  {item.AccessLevel}  code {item.VerificationCode}  expires {item.ExpiresAt:O}");
                    if (pending.Length == 0) output.WriteLine("No pending pairing requests.");
                }
                return 0;
            }
            case "auth pairing approve":
                store.Approve(options.Id!, options.Get("--code"));
                return Mutation(output, options, true, "approved");
            case "auth pairing reject":
            {
                var found = store.ListPending().Any(item => item.RequestId == options.Id);
                store.Reject(options.Id!);
                return Mutation(output, options, found, "rejected");
            }
            case "auth pairing revoke":
                return Mutation(output, options, store.RevokeInvitation(options.Id!), "revoked");
            case "auth session issue":
            {
                var issued = store.IssueSession(options.Access, options.Ttl, options.Get("--label"), options.Get("--subject"));
                if (options.Has("--token-only")) output.WriteLine(issued.Token);
                else if (options.Json) WriteJson(output, new SessionOutput(issued.Device, issued.Token));
                else
                {
                    var device = issued.Device;
                    output.WriteLine($"Session: {device.DeviceId}\nLabel: {device.DeviceName}\nAccess: {device.AccessLevel}\nExpires: {device.ExpiresAt:O}");
                    if (device.Subject is not null) output.WriteLine($"Subject: {device.Subject}");
                    output.WriteLine($"Token: {issued.Token}\nKeep this bearer credential secret. It will not be displayed again.");
                }
                return 0;
            }
            case "auth session list":
            {
                var sessions = store.ListDevices().ToArray();
                if (options.Json) WriteJson(output, sessions);
                else
                {
                    foreach (var item in sessions) output.WriteLine($"{item.DeviceId}  {item.DeviceName}  {item.AccessLevel}  {item.ExpiresAt:O}  {item.Subject}");
                    if (sessions.Length == 0) output.WriteLine("No active device sessions.");
                }
                return 0;
            }
            case "auth session revoke":
                return Mutation(output, options, store.RevokeSession(options.Id!), "revoked");
            default: throw new ArgumentException("Unknown command. See --help.");
        }
    }

    private static int Mutation(TextWriter output, Options options, bool changed, string action)
    {
        if (options.Json) WriteJson(output, new MutationOutput(options.Id!, action, changed));
        else output.WriteLine(changed ? $"{options.Id}: {action}." : $"{options.Id}: no matching active record.");
        return changed ? 0 : 1;
    }

    private static async Task ProbeAsync(SshHostInfo host, CancellationToken cancellationToken)
    {
        host.Validate();
        using var handler = new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null && string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256),
                    host.CertificateFingerprint, StringComparison.OrdinalIgnoreCase),
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{host.Port}/ssh/health");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.BearerCredential);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("The discovered host did not pass its authenticated readiness check.");
    }

    internal static void WriteJson<T>(TextWriter writer, T value) =>
        writer.WriteLine(JsonSerializer.Serialize(value, (JsonTypeInfo<T>)CliJsonContext.Default.GetTypeInfo(typeof(T))!));

    internal sealed class Options
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
        public required string Command { get; init; }
        public string? Id { get; private set; }
        public string Root => Path.GetFullPath(Environment.ExpandEnvironmentVariables(Get("--data-root") ?? Get("--base-dir") ?? HostOptions.DefaultDataRoot));
        public bool Json => Has("--json");
        public TimeSpan? Ttl => Get("--ttl") is { } value ? ParseTtl(value) : null;
        public RemoteAccessLevel Access => Get("--access") switch
        {
            null or "operate" => RemoteAccessLevel.Operate,
            "read-only" => RemoteAccessLevel.ReadOnly,
            _ => throw new ArgumentException("--access must be read-only or operate."),
        };
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public bool Has(string key) => _values.ContainsKey(key);

        public static Options Parse(string[] args)
        {
            if (args.Length == 0) throw new ArgumentException("A command is required.");
            var offset = args[0] == "auth" ? 3 : 1;
            if (args.Length < offset) throw new ArgumentException("Use auth pairing or auth session with an action. See --help.");
            var command = string.Join(" ", args.Take(offset));
            string[] extra = command switch
            {
                "pair" => ["--ttl", "--label", "--access", "--base-url", "--certificate", "--no-qr"],
                "auth pairing create" => ["--ttl", "--label", "--access", "--base-url", "--certificate"],
                "auth session issue" => ["--ttl", "--label", "--subject", "--access", "--token-only"],
                "auth pairing approve" => ["--code"],
                "status" or "auth pairing list" or "auth pairing pending" or "auth pairing revoke" or "auth pairing reject"
                    or "auth session list" or "auth session revoke" => [],
                _ => throw new ArgumentException("Unknown command. See --help."),
            };
            var allowed = new HashSet<string>(extra.Concat(["--data-root", "--base-dir", "--json"]), StringComparer.Ordinal);
            var options = new Options { Command = command };
            bool needsId = command is "auth pairing revoke" or "auth pairing approve" or "auth pairing reject" or "auth session revoke";
            for (var i = offset; i < args.Length; i++)
            {
                var key = args[i];
                if (!key.StartsWith("--", StringComparison.Ordinal))
                {
                    if (!needsId || options.Id is not null || !Guid.TryParseExact(key, "N", out var id))
                        throw new ArgumentException("Provide one record ID exactly as shown by list or pending.");
                    options.Id = id.ToString("N");
                    continue;
                }
                if (!allowed.Contains(key) || options.Has(key)) throw new ArgumentException("Unknown or duplicate command option. See --help.");
                string? value = null;
                if (key is not ("--json" or "--token-only" or "--no-qr"))
                {
                    if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[i]))
                        throw new ArgumentException("A command option is missing its value.");
                    value = args[i];
                }
                options._values.Add(key, value);
            }
            if (needsId && options.Id is null) throw new ArgumentException("A record ID is required.");
            if (options.Has("--data-root") && options.Has("--base-dir")) throw new ArgumentException("Use only one data-root option.");
            if (options.Json && options.Has("--token-only")) throw new ArgumentException("--json and --token-only cannot be combined.");
            if (command == "auth pairing approve" && (options.Get("--code") is not { Length: 6 } code || !code.All(char.IsAsciiDigit)))
                throw new ArgumentException("Approval requires --code with the six digits shown by the pairing client.");
            _ = options.Access;
            _ = options.Ttl;
            return options;
        }
    }

    internal static TimeSpan ParseTtl(string value)
    {
        var match = DurationPattern().Match(value);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            throw new ArgumentException("Use a positive lifetime such as 30s, 5m, 1h, or 30d.");
        var multiplier = char.ToLowerInvariant(match.Groups[2].Value[0]) switch { 's' => 1, 'm' => 60, 'h' => 3600, 'd' => 86400, _ => 0 };
        var seconds = number * multiplier;
        if (!double.IsFinite(seconds) || seconds < 1 || seconds > TimeSpan.FromDays(180).TotalSeconds)
            throw new ArgumentException("Lifetime must be between one second and 180 days (pairing invitations: at most one day).");
        return TimeSpan.FromSeconds(seconds);
    }

    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)\s*(s|sec|second|seconds|m|min|minute|minutes|h|hr|hour|hours|d|day|days)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex DurationPattern();
}

internal sealed record HostStatus(bool Running, string DataRoot, string? EnvironmentId, string? Name, string? HostKind,
    string? ServerVersion, Uri? PairingAddress, string? CertificateFingerprint);
internal sealed record PairingOutput(string Id, string? Label, RemoteAccessLevel AccessLevel, DateTimeOffset ExpiresAt, string Token, string? PairingUrl)
{
    public override string ToString() => Id;
}
internal sealed record SessionOutput(RemoteDevice Device, string Token)
{
    public override string ToString() => Device.DeviceId;
}
internal sealed record MutationOutput(string Id, string Action, bool Changed);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, WriteIndented = true)]
[JsonSerializable(typeof(HostStatus))]
[JsonSerializable(typeof(PairingOutput))]
[JsonSerializable(typeof(SessionOutput))]
[JsonSerializable(typeof(MutationOutput))]
[JsonSerializable(typeof(RemotePairingInvitation[]))]
[JsonSerializable(typeof(PendingRemoteDevice[]))]
[JsonSerializable(typeof(RemoteDevice[]))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
