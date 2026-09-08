using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiStation.Host.Hosting;

[SupportedOSPlatform("windows")]
internal sealed class SshHostIdentity : IDisposable
{
    public required X509Certificate2 Certificate { get; init; }
    public required string Credential { get; init; }

    // Called only while holding the host data lock. Both fields survive reconnects;
    // the client learns them over SSH and pins TLS even on the local forwarded port.
    public static SshHostIdentity LoadOrCreate(string root)
    {
        var path = Path.Combine(root, "ssh-identity.protected");
        if (File.Exists(path))
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try
            {
                var stored = JsonSerializer.Deserialize(bytes, SshIdentityJsonContext.Default.SshIdentityDocument)
                    ?? throw new InvalidOperationException("The SSH host identity is unreadable.");
                var certificate = X509CertificateLoader.LoadPkcs12(stored.Certificate, null, X509KeyStorageFlags.UserKeySet);
                if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                {
                    certificate.Dispose();
                    throw new InvalidOperationException("The SSH host certificate has expired. Stop the host and rotate its ssh-identity.protected file, then reconnect.");
                }
                return new() { Certificate = certificate, Credential = stored.Credential };
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=PiStation SSH", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        var exported = created.Export(X509ContentType.Pkcs12);
        var credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var document = JsonSerializer.SerializeToUtf8Bytes(new SshIdentityDocument(exported, credential), SshIdentityJsonContext.Default.SshIdentityDocument);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, ProtectedData.Protect(document, null, DataProtectionScope.CurrentUser));
            File.Move(temporary, path);
            return new() { Certificate = X509CertificateLoader.LoadPkcs12(exported, null, X509KeyStorageFlags.UserKeySet), Credential = credential };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exported);
            CryptographicOperations.ZeroMemory(document);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Dispose() => Certificate.Dispose();
}

internal sealed record SshIdentityDocument(byte[] Certificate, string Credential);
[JsonSerializable(typeof(SshIdentityDocument))]
internal sealed partial class SshIdentityJsonContext : JsonSerializerContext;
