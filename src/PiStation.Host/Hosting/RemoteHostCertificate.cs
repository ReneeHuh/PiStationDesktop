using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PiStation.Host.Hosting;

/// <summary>The same DPAPI-protected LAN identity is used by the desktop and headless host.</summary>
[SupportedOSPlatform("windows")]
public static class RemoteHostCertificate
{
    public static X509Certificate2 LoadOrCreate(string dataRoot)
    {
        var path = Path.Combine(Path.GetFullPath(dataRoot), "remote-certificate.protected");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) return Load(path);
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=PiStation Remote", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        var exported = created.Export(X509ContentType.Pkcs12);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, ProtectedData.Protect(exported, null, DataProtectionScope.CurrentUser));
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { } // A concurrent creator won; never replace its pin.
            return Load(path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exported);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static X509Certificate2 Load(string path)
    {
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try
        {
            // Schannel requires a user key container; DPAPI is the durable copy.
            var certificate = X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.UserKeySet);
            if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            {
                certificate.Dispose();
                throw new InvalidOperationException("The remote sharing certificate has expired or has no private key. Stop sharing and rotate remote-certificate.protected, then re-pair devices.");
            }
            return certificate;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
