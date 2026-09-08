using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiStation.Protocol;

namespace PiStation.Host.Updates;

public static class ServerUpdatePackage
{
    public static async Task<string> ValidateAsync(string packagePath, string runtimeDirectory, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count is < 2 or > 4096 || archive.Entries.Sum(e => e.Length) > 1024L * 1024 * 1024)
            throw new InvalidDataException("The host archive exceeds its extraction limits.");
        var manifestEntry = archive.GetEntry("pistation-update.json");
        if (manifestEntry is null || manifestEntry.Length > 4096) throw new InvalidDataException("The host update manifest is missing.");
        await using var manifestStream = manifestEntry.Open();
        var manifest = await JsonSerializer.DeserializeAsync(manifestStream, ServerPackageJsonContext.Default.ServerPackageManifest, cancellationToken).ConfigureAwait(false);
        if (manifest is null || manifest.Platform != "win-x64" || manifest.ProtocolVersion != ProtocolVersion.Current ||
            manifest.DatabaseCompatibilityVersion != 1 || !Version.TryParse(manifest.Version, out var expectedVersion))
            throw new InvalidDataException("The package platform, protocol, or database version is incompatible.");
        var root = Path.GetFullPath(runtimeDirectory);
        Directory.CreateDirectory(root);
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("The staging directory cannot be a link.");
        var prefix = root + Path.DirectorySeparatorChar;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.Contains(':') || entry.FullName.Contains('\\') || Path.IsPathRooted(entry.FullName) ||
                ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || entry.Length > 256L * 1024 * 1024)
                throw new InvalidDataException("The archive contains an unsupported entry.");
            var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !paths.Add(path))
                throw new InvalidDataException("The archive contains an invalid or duplicated path.");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var source = entry.Open();
            if (File.Exists(path))
            {
                await using var existing = File.OpenRead(path);
                var existingHash = await SHA256.HashDataAsync(existing, cancellationToken).ConfigureAwait(false);
                var packageHash = await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(existingHash, packageHash))
                    throw new InvalidDataException("The staged runtime no longer matches its package.");
            }
            else
            {
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
        }
        var executable = Path.Combine(root, "PiStation.Server.exe");
        var assembly = Path.Combine(root, "PiStation.Server.dll");
        if (!File.Exists(executable) || !File.Exists(assembly) || AssemblyName.GetAssemblyName(assembly).Version != expectedVersion)
            throw new InvalidDataException("The staged server version does not match its manifest.");
        using (var executableStream = File.OpenRead(executable))
        using (var pe = new System.Reflection.PortableExecutable.PEReader(executableStream))
            if (pe.PEHeaders.CoffHeader.Machine != System.Reflection.PortableExecutable.Machine.Amd64)
                throw new InvalidDataException("The host executable must target Windows x64.");
        return manifest.Version;
    }
}

public sealed record ServerPackageManifest(string Version, int ProtocolVersion, string Platform, int DatabaseCompatibilityVersion);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ServerPackageManifest))]
internal sealed partial class ServerPackageJsonContext : JsonSerializerContext;
