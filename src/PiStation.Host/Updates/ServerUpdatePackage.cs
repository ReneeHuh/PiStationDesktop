using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiStation.Host.Updates;

public static class ServerUpdatePackage
{
    public static async Task<string> ValidateAsync(string packagePath, string runtimeDirectory, CancellationToken cancellationToken) =>
        (await ValidateAndReadManifestAsync(packagePath, runtimeDirectory, cancellationToken).ConfigureAwait(false)).Version;

    public static async Task<ServerPackageManifest> ValidateAndReadManifestAsync(string packagePath, string runtimeDirectory, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count is < 2 or > 4096 || archive.Entries.Sum(e => e.Length) > 1024L * 1024 * 1024)
            throw new InvalidDataException("The host archive exceeds its extraction limits.");
        var manifestEntry = archive.GetEntry("pistation-update.json");
        if (manifestEntry is null || manifestEntry.Length > 4096) throw new InvalidDataException("The host update manifest is missing.");
        await using var manifestStream = manifestEntry.Open();
        var manifest = await JsonSerializer.DeserializeAsync(manifestStream, ServerPackageJsonContext.Default.ServerPackageManifest, cancellationToken).ConfigureAwait(false);
        if (manifest is null || manifest.Platform != "win-x64" || manifest.ProtocolVersion <= 0 ||
            manifest.DatabaseCompatibilityVersion != 1 || manifest.StartupWriteGateVersion != 1 || !Version.TryParse(manifest.Version, out var expectedVersion))
            throw new InvalidDataException("The package platform, protocol, database, or startup safety version is incompatible.");
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
        // Workspace protocol changes are the reason an update may be needed. Check
        // against the package's own metadata without loading or running its code.
        var protocolAssembly = Path.Combine(root, "PiStation.Protocol.dll");
        if (!File.Exists(protocolAssembly) || ReadProtocolVersion(protocolAssembly) != manifest.ProtocolVersion)
            throw new InvalidDataException("The staged protocol version does not match its manifest.");
        using (var executableStream = File.OpenRead(executable))
        using (var pe = new PEReader(executableStream))
            if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64)
                throw new InvalidDataException("The host executable must target Windows x64.");
        return manifest;
    }

    internal static int ReadProtocolVersion(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            if (metadata.GetString(type.Namespace) != "PiStation.Protocol" || metadata.GetString(type.Name) != "ProtocolVersion") continue;
            foreach (var fieldHandle in type.GetFields())
            {
                var field = metadata.GetFieldDefinition(fieldHandle);
                if (metadata.GetString(field.Name) != "Current" || field.GetDefaultValue().IsNil) continue;
                var constant = metadata.GetConstant(field.GetDefaultValue());
                if (constant.TypeCode == ConstantTypeCode.Int32) return metadata.GetBlobReader(constant.Value).ReadInt32();
            }
        }
        throw new InvalidDataException("The staged protocol assembly has no version constant.");
    }
}

public sealed record ServerPackageManifest(string Version, int ProtocolVersion, string Platform, int DatabaseCompatibilityVersion, int StartupWriteGateVersion = 0);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ServerPackageManifest))]
internal sealed partial class ServerPackageJsonContext : JsonSerializerContext;
