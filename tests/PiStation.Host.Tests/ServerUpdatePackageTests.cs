using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using PiStation.Host.Updates;
using PiStation.Protocol;

namespace PiStation.Host.Tests;

public sealed class ServerUpdatePackageTests
{
    [Fact]
    public async Task PackageMayChangeWorkspaceProtocolWhenItsAssemblyAndManifestAgree()
    {
        using var folder = new HostTestDirectory();
        var package = CreatePackage(folder, ProtocolVersion.Current + 1, ProtocolVersion.Current + 1);
        var manifest = await ServerUpdatePackage.ValidateAndReadManifestAsync(package, folder.GetPath("runtime"), CancellationToken.None);
        Assert.Equal(ProtocolVersion.Current + 1, manifest.ProtocolVersion);
    }

    [Fact]
    public async Task PackageCannotClaimAProtocolDifferentFromItsAssembly()
    {
        using var folder = new HostTestDirectory();
        var package = CreatePackage(folder, ProtocolVersion.Current + 1, ProtocolVersion.Current);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ServerUpdatePackage.ValidateAsync(package, folder.GetPath("runtime"), CancellationToken.None));
        Assert.Contains("protocol version does not match", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(999, 2, 1)]
    [InlineData(999, 1, 2)]
    public async Task RecoveryStillRequiresValidProtocolAndCompatibleDatabaseAndStartupGate(int protocol, int database, int gate)
    {
        using var folder = new HostTestDirectory();
        var package = CreatePackage(folder, protocol, ProtocolVersion.Current, database, gate);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ServerUpdatePackage.ValidateAsync(package, folder.GetPath("runtime"), CancellationToken.None));
        Assert.False(Directory.Exists(folder.GetPath("runtime")));
    }

    private static string CreatePackage(HostTestDirectory folder, int manifestProtocol, int assemblyProtocol, int database = 1, int gate = 1)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "PiStationDesktop.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var configuration = AppContext.BaseDirectory.Contains("Release", StringComparison.Ordinal) ? "Release" : "Debug";
        var server = Path.Combine(repository.FullName, "src", "PiStation.Server", "bin", configuration, "net10.0");
        var assembly = Path.Combine(server, "PiStation.Server.dll");
        Assert.True(File.Exists(assembly), "Build the solution before running host package tests.");
        var package = folder.GetPath("package.zip");
        using var archive = ZipFile.Open(package, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(Path.Combine(server, "PiStation.Server.exe"), "PiStation.Server.exe");
        archive.CreateEntryFromFile(assembly, "PiStation.Server.dll");
        using (var output = archive.CreateEntry("PiStation.Protocol.dll").Open())
            output.Write(ProtocolAssemblyWithVersion(assemblyProtocol));
        using var manifest = archive.CreateEntry("pistation-update.json").Open();
        JsonSerializer.Serialize(manifest, new ServerPackageManifest(AssemblyName.GetAssemblyName(assembly).Version!.ToString(),
            manifestProtocol, "win-x64", database, gate), ServerPackageJsonContext.Default.ServerPackageManifest);
        return package;
    }

    // This fixture changes only metadata for package validation; it is never executed.
    private static byte[] ProtocolAssemblyWithVersion(int version)
    {
        var bytes = File.ReadAllBytes(typeof(ProtocolVersion).Assembly.Location);
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var type = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition).Single(t =>
            metadata.GetString(t.Namespace) == "PiStation.Protocol" && metadata.GetString(t.Name) == "ProtocolVersion");
        var field = type.GetFields().Select(metadata.GetFieldDefinition).Single(f => metadata.GetString(f.Name) == "Current");
        var constant = metadata.GetConstant(field.GetDefaultValue());
        var offset = pe.PEHeaders.MetadataStartOffset + metadata.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(constant.Value);
        Assert.Equal(4, bytes[offset]); // A four-byte blob has a one-byte length prefix.
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 1, 4), version);
        return bytes;
    }
}
