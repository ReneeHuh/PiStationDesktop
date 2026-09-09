using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PiStation.ClientRuntime;
using PiStation.Host;
using PiStation.Host.Hosting;
using PiStation.Host.Security;
using PiStation.PiRpc.Discovery;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.RemoteUiFixture;

// Test-only executable. No changes to the user's app data, firewall, SSH, or Tailscale.
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string[] SetupChecks = ["Pinned HTTPS connection", "Isolated protected profiles", "Read-only icon bytes", "200 + 5 host entries"];
    private static readonly string[] DataChecks = ["Host runtime settings preserved; timeout 90", "Client icon bytes persisted on host", "Redacted diagnostics saved on client"];

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--serve-process-fixture")
            return await ServeProcessFixture.RunAsync(args[1], args[2]);
        if (args.Length < 2 || args[0] is not ("serve" or "smoke" or "verify"))
        {
            Console.Error.WriteLine("Usage: PiStation.RemoteUiFixture serve|smoke NEW_ROOT FAKE_PI_EXE | verify ROOT");
            return 2;
        }
        var root = Path.GetFullPath(args[1]);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(args[0] == "serve" ? 60 : 2));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
        try
        {
            if (args[0] == "verify")
            {
                Require(args.Length == 2, "verify accepts only the fixture root.");
                await VerifyAsync(root, "native-data-check", lifetime.Token);
            }
            else
            {
                Require(args.Length == 3, "serve and smoke require the FakePi executable.");
                await RunAsync(root, Path.GetFullPath(args[2]), args[0] == "smoke", lifetime.Token);
            }
            return 0;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            Console.Error.WriteLine("Remote fixture lifetime ended.");
            return args[0] == "serve" ? 0 : 1;
        }
        catch (Exception error)
        {
            // Keep credentials and transport exception details out of runner output.
            Console.Error.WriteLine($"Remote fixture failed ({error.GetType().Name}).");
            if (error.Data["FixtureCheck"] is string check) Console.Error.WriteLine(check);
            return 1;
        }
    }

    private static async Task RunAsync(string root, string fakePi, bool smoke, CancellationToken cancellationToken)
    {
        Require(!Directory.Exists(root) && !File.Exists(root), "Use a new fixture root.");
        Require(File.Exists(fakePi) && Path.GetFileName(fakePi) == "PiStation.FakePi.exe", "Build FakePi first.");
        Directory.CreateDirectory(root);
        var hostRoot = Path.Combine(root, "host");
        var projectPath = Directory.CreateDirectory(Path.Combine(root, "host-project")).FullName;
        var clientRoot = Directory.CreateDirectory(Path.Combine(root, "client")).FullName;
        var clientFiles = Directory.CreateDirectory(Path.Combine(root, "client-files")).FullName;
        var extension = Path.Combine(projectPath, "fixture-extension.ts");
        await File.WriteAllTextAsync(extension, "export default () => {};", cancellationToken);
        var hostIcon = Path.Combine(projectPath, "host-icon.png");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "host-icon.png"), hostIcon);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "client-icon.png"), Path.Combine(clientFiles, "client-icon.png"));
        var paged = Directory.CreateDirectory(Path.Combine(projectPath, "paged")).FullName;
        for (var index = 0; index < 205; index++)
            await File.WriteAllTextAsync(Path.Combine(paged, $"entry-{index:D3}.txt"), "fixture", cancellationToken);
        var baseline = new PiRuntimeConfiguration(fakePi, new(true, [extension]),
            new(["--provider", "fixture-host", "--append-system-prompt", ""], new Dictionary<string, string?>
            {
                ["REMOTE_FIXTURE_VALUE"] = " value=with equals ", ["REMOTE_FIXTURE_EMPTY"] = "",
                ["REMOTE_FIXTURE_REMOVE"] = null, ["REMOTE_FIXTURE_MULTILINE"] = "first\nsecond",
            }, 75, 8));
        await PiRuntimeSettingsStore.SaveAsync(hostRoot, baseline, cancellationToken);
        await PiRuntimeSettingsStore.SaveAsync(clientRoot, new(fakePi, new(),
            new(["--provider", "fixture-client"], new Dictionary<string, string?> { ["CLIENT_ONLY"] = "local" })), cancellationToken);
        var options = new HostOptions
        {
            ApplicationDataRoot = hostRoot, EnvironmentName = "Remote acceptance host",
            ConfiguredPiExecutablePath = fakePi, Extensions = baseline.Extensions, LaunchConfiguration = baseline.Launch!,
            PiInstallation = new(PiInstallationKind.NativeExecutable, fakePi, [], new SemanticVersion(0, 84, 4), null, null, "ui-fixture"),
            AdditionalPiArguments = ["--fake-pi-scenario", "normal"],
        };
        await using var host = await EmbeddedEnvironmentHost.StartAsync(options, cancellationToken: cancellationToken);
        using var access = new RemoteAccessStore(Path.Combine(hostRoot, "acceptance-access.db"));
        using var key = RSA.Create(2048);
        using var created = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(2));
        using var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
        await using var remote = await RemoteEnvironmentHost.StartAsync(host.Environment, access, IPAddress.Loopback, 0, certificate,
            cancellationToken: cancellationToken);
        SavedRemoteEnvironment SaveConnection(string directory, RemoteAccessLevel level)
        {
            var session = access.IssueSession(level, TimeSpan.FromHours(1), "Native acceptance fixture");
            var saved = new SavedRemoteEnvironment(host.Environment.GetDescriptor().EnvironmentId, options.EnvironmentName,
                remote.Address, certificate.GetCertHashString(HashAlgorithmName.SHA256), session.Token, ClientId.New());
            new RemoteConnectionStore(Path.Combine(directory, "remote-connections.protected")).Save(saved);
            return saved;
        }
        var connection = SaveConnection(clientRoot, RemoteAccessLevel.Operate);
        var viewer = SaveConnection(Path.Combine(root, "client-read-only"), RemoteAccessLevel.ReadOnly);
        await using var client = new EnvironmentClient(connection.CreateOptions());
        await client.ConnectAsync(cancellationToken);
        var project = await client.AddProjectAsync(new(projectPath), cancellationToken);
        await client.UpdateProjectDefaultsAsync(new(project.ProjectId, project.DefaultWorkspaceMode, null, null, null, false,
            Icon: hostIcon, UpdateCustomization: true), cancellationToken);
        await client.CreateThreadAsync(new(project.ProjectId, "Remote acceptance thread"), cancellationToken);
        await using var readOnly = new EnvironmentClient(viewer.CreateOptions());
        await readOnly.ConnectAsync(cancellationToken);
        var expectedHostIcon = await File.ReadAllBytesAsync(hostIcon, cancellationToken);
        Require((await readOnly.ReadProjectIconAsync(project.ProjectId, cancellationToken))!.SequenceEqual(expectedHostIcon), "Read-only icon transport failed.");
        var first = await client.BrowseHostPathAsync(new(paged), cancellationToken);
        var next = await client.BrowseHostPathAsync(new(paged, first.NextOffset ?? throw new InvalidOperationException("Missing second page.")), cancellationToken);
        Require(first.Entries.Count == 200 && next.Entries.Count == 5 && next.NextOffset is null, "Host paging failed.");
        var manifest = new FixtureManifest(connection.EnvironmentId.ToString(), project.ProjectId.ToString(), fakePi,
            baseline, Path.Combine(clientFiles, "diagnostics.json"));
        await WriteJsonAsync(Path.Combine(root, "fixture.json"), manifest, cancellationToken);
        await WriteJsonAsync(Path.Combine(root, "ready.json"), new
        {
            host = options.EnvironmentName, address = remote.Address.AbsoluteUri, clientRoot, projectPath,
            readOnlyClientRoot = Path.Combine(root, "client-read-only"),
            setupChecks = SetupChecks,
            nativeAcceptance = "pending", physicalTwoMachineAcceptance = "pending",
        }, cancellationToken);
        if (smoke)
        {
            var loaded = await client.GetPiRuntimeConfigurationAsync(cancellationToken);
            var launch = PiLaunchEditor.Parse(PiLaunchEditor.FormatArguments(loaded.Launch!), PiLaunchEditor.FormatEnvironment(loaded.Launch!), 90, 8, loaded.Launch);
            var result = await client.ConfigurePiRuntimeAsync(new(loaded.ExecutablePath, loaded.Extensions, launch), cancellationToken);
            Require(result.Available, "Runtime save failed.");
            await client.UpdateProjectDefaultsAsync(new(project.ProjectId, project.DefaultWorkspaceMode, null, null, null, false,
                UpdateCustomization: true, UploadedIcon: new("client-icon.png", await File.ReadAllBytesAsync(Path.Combine(clientFiles, "client-icon.png"), cancellationToken))), cancellationToken);
            await client.ExportDiagnosticsAsync(new(manifest.DiagnosticsDestination), cancellationToken);
            await VerifyAsync(root, "fixture-smoke", cancellationToken);
            return;
        }
        while (!File.Exists(Path.Combine(root, "stop"))) await Task.Delay(500, cancellationToken);
    }

    private static async Task VerifyAsync(string root, string evidenceKind, CancellationToken cancellationToken)
    {
        var manifest = JsonSerializer.Deserialize<FixtureManifest>(await File.ReadAllTextAsync(Path.Combine(root, "fixture.json"), cancellationToken))
            ?? throw new InvalidOperationException("Missing fixture manifest.");
        await WriteJsonAsync(Path.Combine(root, evidenceKind + ".json"), new
        {
            evidenceKind, passed = false, status = "incomplete", timestamp = DateTimeOffset.UtcNow,
        }, cancellationToken);
        var connection = new RemoteConnectionStore(Path.Combine(root, "client", "remote-connections.protected")).Load().Single();
        Require(connection.EnvironmentId.ToString() == manifest.EnvironmentId && IPAddress.IsLoopback(IPAddress.Parse(connection.Address.Host)), "Wrong fixture identity or address.");
        await using var client = new EnvironmentClient(connection.CreateOptions());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(deadline.Token);
        var actual = await client.GetPiRuntimeConfigurationAsync(deadline.Token);
        var expected = manifest.Baseline;
        Require(actual.ExecutablePath == expected.ExecutablePath && actual.Extensions.DiscoverInstalled == expected.Extensions.DiscoverInstalled &&
            actual.Extensions.Paths!.SequenceEqual(expected.Extensions.Paths!), "Runtime path or extensions changed.");
        Require(actual.Launch!.CommandTimeoutSeconds == 90 && actual.Launch.ShutdownTimeoutSeconds == 8 &&
            actual.Launch.Arguments!.SequenceEqual(expected.Launch!.Arguments!) &&
            actual.Launch.EnvironmentVariables!.OrderBy(pair => pair.Key, StringComparer.Ordinal).SequenceEqual(expected.Launch.EnvironmentVariables!.OrderBy(pair => pair.Key, StringComparer.Ordinal)),
            "Runtime values were not preserved when setting timeout to 90.");
        var icon = await client.ReadProjectIconAsync(ProjectId.Parse(manifest.ProjectId), deadline.Token);
        var expectedClientIcon = await File.ReadAllBytesAsync(Path.Combine(root, "client-files", "client-icon.png"), deadline.Token);
        Require(icon is not null && icon.SequenceEqual(expectedClientIcon), "Uploaded icon differs from client bytes.");
        var diagnostics = await File.ReadAllTextAsync(manifest.DiagnosticsDestination, deadline.Token);
        using var document = JsonDocument.Parse(diagnostics);
        Require(ContainsString(document.RootElement, "[redacted]") &&
            !ContainsString(document.RootElement, Path.Combine(root, "host")) &&
            !ContainsString(document.RootElement, connection.DeviceCredential), "Diagnostics were not redacted.");
        await WriteJsonAsync(Path.Combine(root, evidenceKind + ".json"), new
        {
            evidenceKind, passed = true, timestamp = DateTimeOffset.UtcNow,
            checks = DataChecks,
            nativeVisualAcceptance = "pending", physicalTwoMachineAcceptance = "pending",
        }, deadline.Token);
        Console.WriteLine($"{evidenceKind}: passed. Visual and physical-machine checks remain pending.");
    }

    private static Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, Json), cancellationToken);

    private static void Require(bool condition, string message)
    {
        if (condition) return;
        var error = new InvalidOperationException(message);
        error.Data["FixtureCheck"] = message;
        throw error;
    }

    private static bool ContainsString(JsonElement element, string value) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()!.Contains(value, StringComparison.OrdinalIgnoreCase),
        JsonValueKind.Array => element.EnumerateArray().Any(item => ContainsString(item, value)),
        JsonValueKind.Object => element.EnumerateObject().Any(item => ContainsString(item.Value, value)),
        _ => false,
    };

    private sealed record FixtureManifest(string EnvironmentId, string ProjectId, string FakePi, PiRuntimeConfiguration Baseline, string DiagnosticsDestination);
}
