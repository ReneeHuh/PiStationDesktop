using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PiStation.ClientRuntime;

public static class RemoteTransport
{
    public static HttpClientHandler CreateHandler(string? fingerprint)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (fingerprint is not null)
        {
            Protocol.Models.RemoteEndpoint.ValidateFingerprint(fingerprint);
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => Matches(certificate, fingerprint);
        }
        return handler;
    }

    // A pin delivered in the out-of-band invitation is the trust anchor, including for self-signed hosts.
    public static bool Matches(X509Certificate? certificate, string fingerprint)
    {
        if (certificate is null) return false;
        using var parsed = new X509Certificate2(certificate);
        return parsed.NotBefore.ToUniversalTime() <= DateTime.UtcNow &&
            parsed.NotAfter.ToUniversalTime() > DateTime.UtcNow &&
            string.Equals(parsed.GetCertHashString(HashAlgorithmName.SHA256), fingerprint, StringComparison.OrdinalIgnoreCase);
    }
}
