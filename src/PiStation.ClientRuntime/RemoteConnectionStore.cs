using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace PiStation.ClientRuntime;

/// <summary>Windows protects the complete document, including credentials and private certificate keys.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsProtectedStorage
{
    public static byte[] Read(string path) => ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);

    public static void Write(string path, byte[] content)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, ProtectedData.Protect(content, null, DataProtectionScope.CurrentUser));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

[SupportedOSPlatform("windows")]
public sealed class RemoteConnectionStore(string path)
{
    private readonly object _gate = new();

    public IReadOnlyList<SavedRemoteEnvironment> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(path)) return [];
            var bytes = WindowsProtectedStorage.Read(path);
            try
            {
                var records = JsonSerializer.Deserialize(bytes, RemoteStorageJsonContext.Default.RemoteStorageEntryArray)
                    ?? throw new InvalidOperationException("Saved remote connections could not be read.");
                return records.Select(r => new SavedRemoteEnvironment(Protocol.Identifiers.EnvironmentId.Parse(r.EnvironmentId),
                    r.Name, new Uri(r.Address), r.CertificateFingerprint, r.DeviceCredential, Protocol.Identifiers.ClientId.Parse(r.ClientId))).ToArray();
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    public void Save(SavedRemoteEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        Protocol.Models.RemoteEndpoint.Validate(environment.Address);
        environment.CreateOptions().Validate();
        lock (_gate)
        {
            var entries = Load().Where(e => e.EnvironmentId != environment.EnvironmentId).Append(environment).ToArray();
            Write(entries);
        }
    }

    public void Forget(Protocol.Identifiers.EnvironmentId environmentId)
    {
        lock (_gate)
        {
            var entries = Load().Where(e => e.EnvironmentId != environmentId).ToArray();
            Write(entries);
        }
    }

    public void Replace(SavedRemoteEnvironment expected, SavedRemoteEnvironment replacement)
    {
        if (expected.EnvironmentId != replacement.EnvironmentId || expected.ClientId != replacement.ClientId)
            throw new ArgumentException("Endpoint changes must retain the saved identity.", nameof(replacement));
        lock (_gate)
        {
            if (Load().SingleOrDefault(e => e.EnvironmentId == expected.EnvironmentId) != expected)
                throw new InvalidOperationException("This connection changed while it was being verified. Select it again.");
            Save(replacement);
        }
    }

    public async Task<SavedRemoteEnvironment> VerifyAndReplaceAsync(SavedRemoteEnvironment expected, Uri address,
        CancellationToken cancellationToken = default)
    {
        Protocol.Models.RemoteEndpoint.Validate(address);
        var replacement = expected with { Address = address };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var probe = new EnvironmentClient(replacement.CreateOptions());
        await probe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        timeout.Token.ThrowIfCancellationRequested();
        Replace(expected, replacement);
        return replacement;
    }

    private void Write(SavedRemoteEnvironment[] entries)
    {
        var records = entries.Select(e => new RemoteStorageEntry(e.EnvironmentId.ToString(), e.Name, e.Address.AbsoluteUri,
            e.CertificateFingerprint, e.DeviceCredential, e.ClientId.ToString())).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(records, RemoteStorageJsonContext.Default.RemoteStorageEntryArray);
        try { WindowsProtectedStorage.Write(path, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

internal sealed record RemoteStorageEntry(string EnvironmentId, string Name, string Address, string CertificateFingerprint,
    string DeviceCredential, string ClientId);
[System.Text.Json.Serialization.JsonSerializable(typeof(RemoteStorageEntry[]))]
internal sealed partial class RemoteStorageJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
