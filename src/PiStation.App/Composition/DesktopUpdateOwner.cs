using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using PiStation.Host.Hosting;
using PiStation.Host.Updates;
using PiStation.Protocol.Models;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace PiStation.App.Composition;

internal sealed class DesktopUpdateOwner(Func<Task> prepareShutdown, Func<Task> finishShutdown) : IRemoteUpdateOwner
{
    public string Kind => "desktop";
    public string CurrentVersion
    {
        get { var v = Package.Current.Id.Version; return new Version(v.Major, v.Minor, v.Build, v.Revision).ToString(); }
    }
    public string PackageKind => ".msix";
    public string Trust => "Windows validates the package signature and the installed app publisher.";
    public async Task<string> ValidateAsync(string packagePath, string runtimeDirectory, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var entry = archive.GetEntry("AppxManifest.xml");
        if (entry is null || entry.Length > 256 * 1024) throw new InvalidDataException("The MSIX manifest is missing.");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        var identity = document.Root?.Elements().SingleOrDefault(e => e.Name.LocalName == "Identity");
        var package = Package.Current.Id;
        if (identity?.Attribute("Name")?.Value != package.Name || identity.Attribute("Publisher")?.Value != package.Publisher ||
            identity.Attribute("ProcessorArchitecture")?.Value is not ("x64" or "neutral") ||
            !Version.TryParse(identity.Attribute("Version")?.Value, out var version))
            throw new InvalidDataException("The package must match this desktop's identity, publisher, and architecture.");
        var manager = new PackageManager();
        var staged = await manager.StagePackageAsync(new Uri(packagePath), null, DeploymentOptions.None).AsTask(cancellationToken).ConfigureAwait(false);
        if (staged.ExtendedErrorCode is not null) throw new UnauthorizedAccessException("Windows did not accept the package signature or deployment.", staged.ExtendedErrorCode);
        return version.ToString();
    }

    public async Task ActivateAsync(StagedRemoteUpdate update, CancellationToken cancellationToken)
    {
        await prepareShutdown().ConfigureAwait(false);
        var info = await SshEnvironmentHost.TryDiscoverAsync(update.DataRoot, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The host identity is unavailable.");
        var source = Path.Combine(AppContext.BaseDirectory, "Updates", "apply-desktop-update.ps1");
        var directory = Path.GetDirectoryName(update.ReceiptPath)!;
        var helper = Path.Combine(directory, "apply-desktop-update.ps1");
        File.Copy(source, helper, overwrite: true);
        using var process = Process.GetCurrentProcess();
        var package = Package.Current.Id;
        var handoff = new DesktopUpdateHandoff(update, process.Id, process.StartTime.ToUniversalTime().Ticks,
            package.Name, package.Publisher, package.FamilyName + "!App", info.EnvironmentId.Value,
            info.CertificateFingerprint, SshEnvironmentHost.PipeName(update.DataRoot));
        var handoffPath = Path.Combine(directory, "desktop-owner.json");
        File.WriteAllBytes(handoffPath, JsonSerializer.SerializeToUtf8Bytes(handoff, DesktopUpdateJsonContext.Default.DesktopUpdateHandoff));
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helper, "-HandoffPath", handoffPath })
            start.ArgumentList.Add(argument);
        using var helperProcess = Process.Start(start) ?? throw new InvalidOperationException("The package update helper did not start.");
        // The helper is independent of the desktop and waits for this exact process to finish.
        await finishShutdown().ConfigureAwait(false);
    }
}

internal sealed record DesktopUpdateHandoff(StagedRemoteUpdate Update, int ParentId, long ParentStartedTicks,
    string PackageName, string Publisher, string AppUserModelId, string EnvironmentId, string CertificateFingerprint, string DiscoveryPipe);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DesktopUpdateHandoff))]
internal sealed partial class DesktopUpdateJsonContext : JsonSerializerContext;
